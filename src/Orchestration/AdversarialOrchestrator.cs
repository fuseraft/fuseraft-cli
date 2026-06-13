using System.Runtime.CompilerServices;
using AgentGovernance;
using AgentGovernance.Sre;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

// Disambiguate from Microsoft.Agents.AI.AgentFactory
using fuseraft.Infrastructure;
using AgentFactory = fuseraft.Infrastructure.AgentFactory;

namespace fuseraft.Orchestration;

/// <summary>
/// Adversarial orchestrator — applies GAN-style generate → critique → revise loops.
/// Activated by <c>Selection.Type: "adversarial"</c>.
///
/// <para>
/// <b>Stages</b>: each <see cref="AdversarialStageConfig"/> pairs a generator agent with
/// a critic agent. Stages run sequentially; the approved artifact from each stage is
/// appended to a shared history that subsequent generators receive as prior context.
/// </para>
///
/// <para>
/// <b>Context firewall</b>: the critic always invokes with a fresh context window
/// containing only its own system instructions and the artifact under review. It never
/// sees the generator's reasoning chain or prior shared history. This is the mechanism
/// that produces genuine independent review rather than rubber-stamping.
/// </para>
///
/// <para>
/// <b>Loop</b>: within each stage the orchestrator runs up to
/// <see cref="AdversarialConfig.Rounds"/> generate/critique cycles. When the critic emits
/// <see cref="AdversarialConfig.PassKeyword"/> on its own line the stage exits early and
/// the artifact is promoted. If all rounds are exhausted the last artifact is promoted
/// regardless so the pipeline continues.
/// </para>
/// </summary>
public sealed class AdversarialOrchestrator(
    OrchestrationConfig config,
    AgentFactory agentFactory,
    ILogger<AdversarialOrchestrator> logger,
    ChangeTracker? changeTracker = null,
    EventEmitter? eventEmitter = null,
    GovernanceKernel? governanceKernel = null,
    IHumanApprovalService? approvalService = null) : IOrchestrator
{
    private readonly AdversarialConfig _advConfig =
        config.Selection.Adversarial ?? new AdversarialConfig();

    // Reserved for future HITL integration (e.g. require human approval before stage promotion).
    private readonly IHumanApprovalService? _approvalService = approvalService;

    private string _sessionId = string.Empty;

    // IOrchestrator events

    public event Action<string>? AgentStarting;
    public event Action<string, string, string?>? ToolCalling;
    public event Action<string, int, int>? TokenBudgetWarning;

    public void SetSessionId(string sessionId)
    {
        _sessionId = sessionId;
        agentFactory.SetSessionId(sessionId);
    }

    public async Task<OrchestrationResult> RunAsync(
        string task,
        IReadOnlyList<AgentMessage>? priorHistory = null,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<AgentMessage>();
        var start    = DateTime.UtcNow;

        try
        {
            await foreach (var msg in StreamAsync(task, priorHistory, cancellationToken).ConfigureAwait(false))
                messages.Add(msg);

            return new OrchestrationResult
            {
                SessionId         = _sessionId,
                Succeeded         = true,
                Messages          = messages,
                Duration          = DateTime.UtcNow - start,
                TerminationReason = "Completed"
            };
        }
        catch (BudgetExceededException ex)
        {
            return new OrchestrationResult
            {
                SessionId         = _sessionId,
                Succeeded         = false,
                Messages          = messages,
                Duration          = DateTime.UtcNow - start,
                TerminationReason = "TokenBudgetExceeded",
                ErrorMessage      = ex.Message
            };
        }
        catch (OperationCanceledException)
        {
            return new OrchestrationResult
            {
                SessionId         = _sessionId,
                Succeeded         = false,
                Messages          = messages,
                Duration          = DateTime.UtcNow - start,
                TerminationReason = "Cancelled",
                ErrorMessage      = "Operation was cancelled."
            };
        }
        catch (Exception ex)
        {
            return new OrchestrationResult
            {
                SessionId         = _sessionId,
                Succeeded         = false,
                Messages          = messages,
                Duration          = DateTime.UtcNow - start,
                TerminationReason = "Error",
                ErrorMessage      = ex.Message
            };
        }
    }

    public async IAsyncEnumerable<AgentMessage> StreamAsync(
        string task,
        IReadOnlyList<AgentMessage>? priorHistory = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_advConfig.Stages.Count == 0)
            throw new InvalidOperationException("Adversarial config has no stages defined.");

        // Build all agents once; re-use across stages.
        var agents = config.Agents
            .Select(a => agentFactory.Create(a, onToolCalling: (agent, tool, args) => ToolCalling?.Invoke(agent, tool, args)))
            .ToDictionary(a => a.Name!, StringComparer.OrdinalIgnoreCase);
        var agentInstructions = config.Agents
            .Where(a => !string.IsNullOrWhiteSpace(a.Instructions))
            .ToDictionary(a => a.Name, a => a.Instructions, StringComparer.OrdinalIgnoreCase);

        int turn             = priorHistory is { Count: > 0 } ? priorHistory[^1].TurnIndex + 1 : 0;
        int cumulativeTokens = 0;

        // Shared history: prior-stage approved artifacts accumulated for subsequent generators.
        // Critics never see this list — they always receive a fresh, isolated context.
        var sharedHistory = new List<ChatMessage>();

        for (int stageIndex = 0; stageIndex < _advConfig.Stages.Count; stageIndex++)
        {
            var stage = _advConfig.Stages[stageIndex];
            var label = stage.Label ?? $"{stage.Generator} → {stage.Critic}";
            var stageTag = $"[Adversarial:Stage{stageIndex + 1}:{label}]";

            if (!agents.TryGetValue(stage.Generator, out var generator))
                throw new InvalidOperationException(
                    $"Adversarial stage '{label}': generator agent '{stage.Generator}' not found in config.");
            if (!agents.TryGetValue(stage.Critic, out var critic))
                throw new InvalidOperationException(
                    $"Adversarial stage '{label}': critic agent '{stage.Critic}' not found in config.");

            agentInstructions.TryGetValue(stage.Generator, out var generatorInstructions);
            agentInstructions.TryGetValue(stage.Critic,    out var criticInstructions);

            logger.LogInformation(
                "[AdversarialOrchestrator] Stage {Index}/{Total} '{Label}' starting.",
                stageIndex + 1, _advConfig.Stages.Count, label);

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync(EventTypes.AdversarialStageStart, agent: stageTag,
                    payload: new { stage = stageIndex + 1, label, generator = stage.Generator, critic = stage.Critic });

            // --- Initial generation ---

            AgentStarting?.Invoke(generator.Name ?? stage.Generator);
            agentFactory.OnAgentTurnStarting();
            changeTracker?.BeginTurn(generator.Name ?? stage.Generator, turn);

            var generatorContext = BuildGeneratorContext(
                generatorInstructions, sharedHistory, task, revision: null);

            var genResponse = await InvokeAgentAsync(generator, generatorContext, cancellationToken);
            var artifact    = genResponse.Text ?? string.Empty;

            var genMsg = MakeMessage(
                $"{stageTag}:{generator.Name}",
                artifact, turn++, OrchestratorHelpers.ExtractUsage(genResponse), OrchestratorHelpers.ExtractToolCalls(genResponse.Messages));
            cumulativeTokens += genMsg.Usage?.TotalTokens ?? 0;
            FireTokenBudgetWarning(genMsg);
            yield return genMsg;

            if (config.MaxTotalTokens is { } cap1 && cumulativeTokens > cap1)
                throw new BudgetExceededException(cumulativeTokens, cap1);

            await FlushChangeTrackerAsync(genMsg);

            // --- Critique / revise loop ---

            bool approved = false;

            for (int round = 1; round <= _advConfig.Rounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Critic: fresh context — only its system instructions + artifact under review.
                // No shared history, no generator reasoning. This is the context firewall.
                AgentStarting?.Invoke(critic.Name ?? stage.Critic);
                agentFactory.OnAgentTurnStarting();
                changeTracker?.BeginTurn(critic.Name ?? stage.Critic, turn);

                var criticContext = BuildCriticContext(criticInstructions, artifact, round, _advConfig.Rounds, _advConfig.PassKeyword);
                var critiqueResponse = await InvokeAgentAsync(critic, criticContext, cancellationToken);
                var critiqueText     = critiqueResponse.Text ?? string.Empty;

                var critiqueMsg = MakeMessage(
                    $"{stageTag}:{critic.Name}:Round{round}",
                    critiqueText, turn++, OrchestratorHelpers.ExtractUsage(critiqueResponse), OrchestratorHelpers.ExtractToolCalls(critiqueResponse.Messages));
                cumulativeTokens += critiqueMsg.Usage?.TotalTokens ?? 0;
                FireTokenBudgetWarning(critiqueMsg);

                logger.LogDebug(
                    "[AdversarialOrchestrator] Stage {Index} round {Round}/{Max}: critic '{Critic}' responded ({Chars} chars).",
                    stageIndex + 1, round, _advConfig.Rounds, stage.Critic, critiqueText.Length);

                yield return critiqueMsg;

                if (config.MaxTotalTokens is { } cap2 && cumulativeTokens > cap2)
                    throw new BudgetExceededException(cumulativeTokens, cap2);

                await FlushChangeTrackerAsync(critiqueMsg);

                if (PassKeywordFound(critiqueText, _advConfig.PassKeyword))
                {
                    approved = true;
                    logger.LogInformation(
                        "[AdversarialOrchestrator] Stage {Index} '{Label}' passed at round {Round}/{Max}.",
                        stageIndex + 1, label, round, _advConfig.Rounds);

                    if (eventEmitter is not null)
                        await eventEmitter.EmitAsync(EventTypes.AdversarialStagePass, agent: stageTag,
                            payload: new { stage = stageIndex + 1, label, round });
                    break;
                }

                // Not approved — revise if rounds remain.
                if (round < _advConfig.Rounds)
                {
                    AgentStarting?.Invoke(generator.Name ?? stage.Generator);
                    agentFactory.OnAgentTurnStarting();
                    changeTracker?.BeginTurn(generator.Name ?? stage.Generator, turn);

                    var revisionContext = BuildGeneratorContext(
                        generatorInstructions, sharedHistory, task, revision: (artifact, critiqueText));
                    var revisionResponse = await InvokeAgentAsync(generator, revisionContext, cancellationToken);
                    artifact = revisionResponse.Text ?? string.Empty;

                    var revisionMsg = MakeMessage(
                        $"{stageTag}:{generator.Name}:Revision{round}",
                        artifact, turn++, OrchestratorHelpers.ExtractUsage(revisionResponse), OrchestratorHelpers.ExtractToolCalls(revisionResponse.Messages));
                    cumulativeTokens += revisionMsg.Usage?.TotalTokens ?? 0;
                    FireTokenBudgetWarning(revisionMsg);
                    yield return revisionMsg;

                    if (config.MaxTotalTokens is { } cap3 && cumulativeTokens > cap3)
                        throw new BudgetExceededException(cumulativeTokens, cap3);

                    await FlushChangeTrackerAsync(revisionMsg);
                }
            }

            if (!approved)
            {
                logger.LogWarning(
                    "[AdversarialOrchestrator] Stage {Index} '{Label}' exhausted {Max} rounds without approval — promoting anyway.",
                    stageIndex + 1, label, _advConfig.Rounds);

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync(EventTypes.AdversarialStageTimeout, agent: stageTag,
                        payload: new { stage = stageIndex + 1, label, rounds = _advConfig.Rounds });
            }

            // Promote the final artifact into shared history so subsequent generators see it.
            sharedHistory.Add(new ChatMessage(ChatRole.Assistant, artifact)
            {
                AuthorName = generator.Name ?? stage.Generator
            });
        }

        logger.LogInformation("[AdversarialOrchestrator] All {Count} stages complete.", _advConfig.Stages.Count);

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync(EventTypes.AdversarialComplete, agent: "[Adversarial]",
                payload: new { stages = _advConfig.Stages.Count });
    }

    // Context builders

    /// <summary>
    /// Builds the generator's invocation context.
    /// On the initial call <paramref name="revision"/> is null — the generator receives
    /// system instructions + prior-stage shared history + the task.
    /// On revision calls it additionally receives the previous artifact and the critique
    /// as a user message so it knows exactly what to fix.
    /// </summary>
    private static List<ChatMessage> BuildGeneratorContext(
        string? instructions,
        List<ChatMessage> sharedHistory,
        string task,
        (string PrevArtifact, string Critique)? revision)
    {
        var context = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(instructions))
            context.Add(new ChatMessage(ChatRole.System, instructions));

        context.AddRange(sharedHistory);
        context.Add(new ChatMessage(ChatRole.User, task));

        if (revision is var (prevArtifact, critique))
        {
            // Inject previous artifact as an assistant turn so the LLM knows what it produced.
            context.Add(new ChatMessage(ChatRole.Assistant, prevArtifact));
            context.Add(new ChatMessage(ChatRole.User,
                $"The reviewer raised the following concerns:\n\n{critique}\n\n" +
                "Please revise your response to address all of these points."));
        }

        return context;
    }

    /// <summary>
    /// Builds the critic's invocation context — always fresh, always isolated.
    /// The critic sees only its system instructions and the artifact to review.
    /// It never sees shared history or generator reasoning.
    /// </summary>
    private static List<ChatMessage> BuildCriticContext(
        string? instructions,
        string artifact,
        int round,
        int totalRounds,
        string passKeyword)
    {
        var context = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(instructions))
            context.Add(new ChatMessage(ChatRole.System, instructions));

        var roundNote = totalRounds > 1
            ? $" (review round {round} of {totalRounds})"
            : string.Empty;

        context.Add(new ChatMessage(ChatRole.User,
            $"Review the following artifact{roundNote} and provide specific, actionable feedback.\n\n" +
            $"If the artifact meets all requirements, respond with \"{passKeyword}\" on its own line " +
            "and nothing else before it. Otherwise describe what must be improved — be specific.\n\n" +
            $"ARTIFACT:\n\n{artifact}"));

        return context;
    }

    private async Task<AgentResponse> InvokeAgentAsync(
        AIAgent agent,
        IEnumerable<ChatMessage> context,
        CancellationToken cancellationToken)
    {
        return governanceKernel?.CircuitBreaker is { } cb
            ? await cb.ExecuteAsync(() => agent.RunAsync(context, null, null, cancellationToken))
            : await agent.RunAsync(context, null, null, cancellationToken);
    }

    private async Task FlushChangeTrackerAsync(AgentMessage msg)
    {
        if (changeTracker is null) return;
        try
        {
            await changeTracker.FlushTurnAsync(msg.AgentName, msg.TurnIndex, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "ChangeTracker flush failed for turn {Turn} ({Agent}).", msg.TurnIndex, msg.AgentName);
        }
    }

    // Pass-keyword detection: the keyword must appear on its own line (case-insensitive).
    private static bool PassKeywordFound(string text, string keyword) =>
        text.Split('\n').Any(line =>
            line.Trim().Equals(keyword, StringComparison.OrdinalIgnoreCase));

    private void FireTokenBudgetWarning(AgentMessage msg)
    {
        var threshold = config.WarnTurnTokens;
        if (threshold > 0 && msg.Usage?.InputTokens is { } inputToks && inputToks > threshold)
            TokenBudgetWarning?.Invoke(msg.AgentName, inputToks, threshold);
    }

    private static AgentMessage MakeMessage(
        string agentName,
        string content,
        int turnIndex,
        TokenUsage? usage,
        IReadOnlyList<ToolCallRecord>? toolCalls = null)
        => new()
        {
            AgentName = agentName,
            Content   = content,
            Role      = "assistant",
            TurnIndex = turnIndex,
            Usage     = usage,
            ToolCalls = toolCalls,
        };

}
