using System.Runtime.CompilerServices;
using AgentGovernance;
using AgentGovernance.Sre;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

using fuseraft.Orchestration.Parallel;

// Disambiguate from Microsoft.Agents.AI.AgentFactory
using fuseraft.Infrastructure;
using AgentFactory = fuseraft.Infrastructure.Agents.AgentFactory;

namespace fuseraft.Orchestration;

/// <summary>
/// Scatter-gather orchestrator. Activated by <c>Selection.Type: "scattergather"</c>.
///
/// <para>
/// <b>Phase 1 — Scatter</b>: all <see cref="ScatterGatherConfig.Participants"/> are invoked
/// in parallel, each receiving the same task in an isolated history snapshot. Participants
/// cannot see each other's in-progress work, producing N independent responses.
/// </para>
///
/// <para>
/// <b>Phase 2 — Gather</b>: the <see cref="ScatterGatherConfig.Synthesizer"/> agent receives
/// the original task history plus every participant's labeled output, then produces the
/// single final answer. The synthesizer may vote, merge, rank, or reconcile depending on
/// how it is instructed.
/// </para>
/// </summary>
public sealed class ScatterGatherOrchestrator(
    OrchestrationConfig config,
    AgentFactory agentFactory,
    ILogger<ScatterGatherOrchestrator> logger,
    ChangeTracker? changeTracker = null,
    EventEmitter? eventEmitter = null,
    GovernanceKernel? governanceKernel = null,
    IHumanApprovalService? humanApprovalService = null,
    IContextAssemblyPipeline? contextPipeline = null,
    RepositoryKnowledgeStore? repositoryKnowledgeStore = null) : IOrchestrator
{
    private readonly ScatterGatherConfig _sgConfig =
        config.Selection.ScatterGather ?? new ScatterGatherConfig();
    private readonly IHumanApprovalService? _humanApprovalService = humanApprovalService;

    private string _sessionId = string.Empty;
    private string _task = string.Empty;

    // IOrchestrator events

    public event Action<string>? AgentStarting;
    public event Action<string, string, string?>? ToolCalling;
    public event Action<string, int, int>? TokenBudgetWarning;

    public void SetSessionId(string sessionId)
    {
        _sessionId = sessionId;
        agentFactory.SetSessionId(sessionId);
        contextPipeline?.SetSessionId(sessionId);
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
            logger.LogError(ex, "[ScatterGatherOrchestrator] Session {SessionId} failed.", _sessionId);
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
        _task = task;

        // Build all agents once.
        var agents = config.Agents
            .Select(a => agentFactory.Create(a, onToolCalling: (agent, tool, args) => ToolCalling?.Invoke(agent, tool, args)))
            .ToDictionary(a => a.Name!, StringComparer.OrdinalIgnoreCase);

        var agentInstructions = config.Agents
            .Where(a => !string.IsNullOrWhiteSpace(a.Instructions))
            .ToDictionary(a => a.Name, a => a.Instructions, StringComparer.OrdinalIgnoreCase);

        var agentConfigs = config.Agents.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

        // Resolve participant agents.
        var participants = new List<(string Name, AIAgent Agent, string? Instructions)>();
        foreach (var name in _sgConfig.Participants)
        {
            if (!agents.TryGetValue(name, out var agent))
                throw new InvalidOperationException(
                    $"ScatterGather: Participant '{name}' not found in config.");
            agentInstructions.TryGetValue(name, out var instr);
            participants.Add((name, agent, instr));
        }

        if (!agents.TryGetValue(_sgConfig.Synthesizer, out var synthesizer))
            throw new InvalidOperationException(
                $"ScatterGather: Synthesizer '{_sgConfig.Synthesizer}' not found in config.");
        agentInstructions.TryGetValue(_sgConfig.Synthesizer, out var synthInstr);

        int turn             = priorHistory is { Count: > 0 } ? priorHistory[^1].TurnIndex + 1 : 0;
        int cumulativeTokens = priorHistory?.Sum(m => m.Usage?.TotalTokens ?? 0) ?? 0;

        // Shared history snapshot: task + any prior turns.
        var baseHistory = new List<ChatMessage>();
        if (priorHistory?.Count > 0)
        {
            foreach (var prior in priorHistory)
            {
                var role    = prior.Role == MessageRole.User ? ChatRole.User : ChatRole.Assistant;
                var content = prior.Content ?? string.Empty;
                var msg     = new ChatMessage(role, content);
                if (role == ChatRole.Assistant && prior.AgentName is not null)
                    msg.AuthorName = prior.AgentName;
                baseHistory.Add(msg);
            }
        }
        baseHistory.Add(new ChatMessage(ChatRole.User, task));

        // -----------------------------------------------------------------------
        // Phase 1: Scatter (all participants in parallel)
        // -----------------------------------------------------------------------

        logger.LogInformation(
            "[ScatterGatherOrchestrator] Phase 1/2: Scatter — {Count} participant(s), concurrency={Concurrency}.",
            participants.Count,
            _sgConfig.MaxConcurrency == 0 ? "unlimited" : _sgConfig.MaxConcurrency.ToString());

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync(EventTypes.PhaseStart,
                payload: new { phase = 1, participants = _sgConfig.Participants });

        var semaphore = _sgConfig.MaxConcurrency > 0
            ? new SemaphoreSlim(_sgConfig.MaxConcurrency)
            : null;

        int baseTurn = turn;

        var scatterTasks = participants.Select((p, index) => Task.Run(async () =>
        {
            if (semaphore is not null) await semaphore.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                AgentStarting?.Invoke(p.Agent.Name ?? p.Name);

                if (eventEmitter is not null)
                    _ = eventEmitter.EmitAsync(EventTypes.ParallelBranchStart,
                        agent: p.Name,
                        payload: new { participant_index = index, participant = p.Name });

                // Each participant gets their own isolated copy of the base history.
                var participantHistory = new List<ChatMessage>(baseHistory);
                var context = await AssembleContextAsync(
                    p.Agent.Name ?? p.Name, p.Instructions, participantHistory,
                    agentConfigs.GetValueOrDefault(p.Name), baseTurn + index, cancellationToken);

                var response = await InvokeAgentAsync(p.Agent, context, cancellationToken);
                var text     = response.Text ?? string.Empty;

                var msg = MakeMessage(
                    p.Agent.Name ?? p.Name,
                    text,
                    baseTurn + index,
                    OrchestratorHelpers.ExtractUsage(response),
                    OrchestratorHelpers.ExtractToolCalls(response.Messages));

                await PersistObservationsAsync(response, p.Agent.Name ?? p.Name, msg.TurnIndex);

                if (eventEmitter is not null)
                    _ = eventEmitter.EmitAsync(EventTypes.ParallelBranchEnd,
                        agent: p.Name,
                        payload: new { participant_index = index });

                return (Index: index, Name: p.Agent.Name ?? p.Name, Msg: msg, Text: text);
            }
            finally
            {
                semaphore?.Release();
            }
        }, cancellationToken)).ToList();

        var scatterResults = await Task.WhenAll(scatterTasks);

        // Yield scatter messages in declaration order; build gather context from them.
        var gatherHistory = new List<ChatMessage>(baseHistory);

        foreach (var r in scatterResults.OrderBy(r => r.Index))
        {
            cumulativeTokens += r.Msg.Usage?.TotalTokens ?? 0;
            FireTokenBudgetWarning(r.Msg);
            yield return r.Msg;

            if (config.MaxTotalTokens is { } cap && cumulativeTokens > cap)
                throw new BudgetExceededException(cumulativeTokens, cap);

            await FlushChangeTrackerAsync(r.Msg);

            // Inject into gather history as a labeled assistant message.
            var labeled = $"[Participant: {r.Name}]\n{r.Text}";
            gatherHistory.Add(new ChatMessage(ChatRole.Assistant, labeled) { AuthorName = r.Name });
        }

        turn = baseTurn + participants.Count;

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync(EventTypes.PhaseEnd,
                payload: new { phase = 1, gathered = scatterResults.Length });

        // -----------------------------------------------------------------------
        // Phase 2: Gather (synthesizer)
        // -----------------------------------------------------------------------

        logger.LogInformation(
            "[ScatterGatherOrchestrator] Phase 2/2: Gather — agent '{Synthesizer}'.", _sgConfig.Synthesizer);

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync(EventTypes.PhaseStart,
                payload: new { phase = 2, agent = _sgConfig.Synthesizer });

        gatherHistory.Add(new ChatMessage(ChatRole.User,
            $"You have received {participants.Count} independent response(s) above. " +
            "Synthesise them into a single, cohesive final answer."));

        AgentStarting?.Invoke(synthesizer.Name ?? _sgConfig.Synthesizer);
        agentFactory.OnAgentTurnStarting();
        changeTracker?.BeginTurn(synthesizer.Name ?? _sgConfig.Synthesizer, turn);

        var gatherContext = await AssembleContextAsync(
            synthesizer.Name ?? _sgConfig.Synthesizer, synthInstr, gatherHistory,
            agentConfigs.GetValueOrDefault(_sgConfig.Synthesizer), turn, cancellationToken);
        var gatherResponse = await InvokeAgentAsync(synthesizer, gatherContext, cancellationToken);
        var gatherText     = gatherResponse.Text ?? string.Empty;

        var gatherMsg = MakeMessage(
            synthesizer.Name ?? _sgConfig.Synthesizer,
            gatherText, turn++,
            OrchestratorHelpers.ExtractUsage(gatherResponse),
            OrchestratorHelpers.ExtractToolCalls(gatherResponse.Messages));

        cumulativeTokens += gatherMsg.Usage?.TotalTokens ?? 0;
        FireTokenBudgetWarning(gatherMsg);
        yield return gatherMsg;

        if (config.MaxTotalTokens is { } cap2 && cumulativeTokens > cap2)
            throw new BudgetExceededException(cumulativeTokens, cap2);

        await FlushChangeTrackerAsync(gatherMsg);
        await PersistObservationsAsync(gatherResponse, synthesizer.Name ?? _sgConfig.Synthesizer, gatherMsg.TurnIndex);

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync(EventTypes.PhaseEnd, payload: new { phase = 2 });

        logger.LogInformation(
            "[ScatterGatherOrchestrator] Session {SessionId} complete — {Turn} total turns, {Tokens:N0} tokens.",
            _sessionId, turn, cumulativeTokens);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    // Shared with MapReduceOrchestrator (and, except context assembly, AdversarialOrchestrator)
    // via FanOutHelpers — see that class's doc comment for what's shared and why.
    private Task<IEnumerable<ChatMessage>> AssembleContextAsync(
        string agentName, string? instructions, IReadOnlyList<ChatMessage> history,
        AgentConfig? agentCfg, int turn, CancellationToken ct) =>
        FanOutHelpers.AssembleContextAsync(
            contextPipeline, eventEmitter, agentName, _task, instructions, history, agentCfg, _sessionId, turn, ct);

    private Task PersistObservationsAsync(AgentResponse response, string agentName, int turn) =>
        FanOutHelpers.PersistObservationsAsync(repositoryKnowledgeStore, _sessionId, response, agentName, turn);

    private Task<AgentResponse> InvokeAgentAsync(AIAgent agent, IEnumerable<ChatMessage> context, CancellationToken ct) =>
        FanOutHelpers.InvokeAgentAsync(agent, context, governanceKernel, ct);

    private static AgentMessage MakeMessage(
        string agentName, string content, int turn,
        TokenUsage? usage, IReadOnlyList<ToolCallRecord>? toolCalls) =>
        FanOutHelpers.MakeMessage(agentName, content, turn, usage, toolCalls);

    private void FireTokenBudgetWarning(AgentMessage msg) =>
        FanOutHelpers.FireTokenBudgetWarning(
            msg, config.WarnTurnTokens, (a, i, t) => TokenBudgetWarning?.Invoke(a, i, t));

    private Task FlushChangeTrackerAsync(AgentMessage msg) =>
        FanOutHelpers.FlushChangeTrackerAsync(msg, changeTracker, logger, nameof(ScatterGatherOrchestrator));
}
