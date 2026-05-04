using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// Magentic-style orchestrator. A manager LLM drives a two-level loop:
/// <list type="bullet">
///   <item><b>Outer loop</b> — gather facts about the task, then produce a step-by-step plan.</item>
///   <item><b>Inner loop</b> — evaluate progress via a JSON ledger, select the next participant
///         agent, issue a targeted instruction, collect the response, and detect completion or
///         stalling. Stalls trigger replanning; repeated replan failures terminate the session.</item>
/// </list>
/// All participant agents are built from <see cref="OrchestrationConfig.Agents"/> through the
/// standard <see cref="AgentFactory"/>. The manager communicates via its own <see cref="IChatClient"/>
/// so it never sees raw JSON ledger blobs from its private context.
/// </summary>
public sealed class MagenticOrchestrator(
    OrchestrationConfig config,
    AgentFactory agentFactory,
    IChatClient managerClient,
    ILogger<MagenticOrchestrator> logger,
    IHumanApprovalService? approvalService = null,
    ChangeTracker? changeTracker = null,
    EventEmitter? eventEmitter = null,
    GovernanceKernel? governanceKernel = null) : IOrchestrator
{
    // Agent name tags used in the message stream so the UI and checkpoints can identify them.
    private const string ManagerPlanTag     = "[MagenticManager:Plan]";
    private const string ManagerReplanTag   = "[MagenticManager:Replan]";
    private const string ManagerFinalTag    = "[MagenticManager:Final]";
    private const string ManagerInternalTag = "[MagenticManager:Internal]";

    // Maximum messages from manager history included in each manager invocation context
    // (ledger evaluation, replanning, and final synthesis).
    // Retains the first ManagerHistoryBootstrapMessages (fact-gather prompt+response,
    // planning prompt+plan) and the most recent tail so sessions with many replan cycles
    // do not overflow the manager model's context.
    private const int ManagerHistoryWindow           = 12;
    private const int ManagerHistoryBootstrapMessages =  4;

    // Maximum shared-history messages included in ledger, replan, and final-answer prompts.
    private const int LedgerConversationWindow      = 30;
    private const int ReplanConversationWindow      = 20;
    private const int FinalAnswerConversationWindow = 30;

    private readonly MagenticManagerConfig _magConfig =
        config.Selection.Magentic ?? new MagenticManagerConfig();


    private string _sessionId = string.Empty;

    // Exposed so SessionRunner can snapshot to SessionCheckpoint.MagenticState after each yield.
    public MagenticCheckpointState? CurrentState { get; private set; }

    // IOrchestrator

    public event Action<string>? AgentStarting;

    /// <inheritdoc/>
    public event Action<string, string, string?>? ToolCalling;

    /// <inheritdoc/>
    public event Action<string, int, int>? TokenBudgetWarning;

    public void SetSessionId(string sessionId) => _sessionId = sessionId;

    /// <summary>
    /// Provides Magentic-specific loop-counter state when resuming a paused session.
    /// Called by <c>RunCommand</c> after loading a checkpoint that carries
    /// <see cref="SessionCheckpoint.MagenticState"/>.
    /// </summary>
    public void SetResumeState(MagenticCheckpointState state) => _resumeState = state;

    private MagenticCheckpointState? _resumeState;

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
        if (config.Agents.Count == 0)
            throw new InvalidOperationException("Magentic config has no participant agents defined.");

        // Build participant agents and lookup maps.
        var agents            = config.Agents
            .Select(a => agentFactory.Create(a, onToolCalling: (agent, tool, args) => ToolCalling?.Invoke(agent, tool, args)))
            .ToList();
        var agentsByName      = agents.ToDictionary(a => a.Name!, StringComparer.OrdinalIgnoreCase);
        var agentInstructions = config.Agents
            .Where(a => !string.IsNullOrWhiteSpace(a.Instructions))
            .ToDictionary(a => a.Name, a => a.Instructions, StringComparer.OrdinalIgnoreCase);
        var agentConfigs      = config.Agents
            .ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        // Shared history: participant agents see the task + prior participant responses.
        var sharedHistory  = new List<ChatMessage>();
        // Manager history: private context for fact-gathering, planning, and ledger evaluation.
        var managerHistory = new List<ChatMessage>();

        // Loop counters — capture all resume-state fields before nulling _resumeState so the
        // AwaitingPlanReview flag is available for the resume path below.
        int turn                = priorHistory is { Count: > 0 } ? priorHistory[^1].TurnIndex + 1 : 0;
        int roundIndex          = _resumeState?.RoundIndex         ?? 0;
        int stallCount          = _resumeState?.StallCount         ?? 0;
        int resetCount          = _resumeState?.ResetCount         ?? 0;
        bool awaitingPlanReview = _resumeState?.AwaitingPlanReview ?? false;
        string? currentPlan     = _resumeState?.CurrentPlan;
        _resumeState = null; // consumed; prevent stale re-application on subsequent StreamAsync calls

        int cumulativeTokens = priorHistory?.Sum(m => m.Usage?.TotalTokens ?? 0) ?? 0;

        bool isResume = priorHistory?.Count > 0;

        if (isResume)
        {
            sharedHistory.Add(new ChatMessage(ChatRole.User, task));

            // Resolve the current plan from persisted history if not already set by resume state.
            // Done before the foreach so we can inject the planning prompt in the right order.
            if (currentPlan is null)
            {
                currentPlan = priorHistory!
                    .LastOrDefault(m => m.AgentName is ManagerPlanTag or ManagerReplanTag)
                    ?.Content;
            }

            // Re-anchor manager history with the original fact-gathering prompt so the manager
            // model receives properly alternating User→Assistant turns.
            // The planning prompt and any replan bridging prompts are injected inline (below)
            // immediately before their corresponding assistant messages, preserving the correct
            // turn order: U:FactGather → A:Facts → U:Plan → A:Plan → (U:Replan → A:Replan)*.
            managerHistory.Add(new ChatMessage(ChatRole.User, BuildFactGatherPrompt(task, config.Agents)));

            bool planPromptInjected = false;

            // Reconstruct both histories from the persisted message stream.
            foreach (var prior in priorHistory!)
            {
                var role = prior.Role == "user" ? ChatRole.User : ChatRole.Assistant;

                if ((prior.AgentName ?? string.Empty).StartsWith("[MagenticManager:", StringComparison.Ordinal))
                {
                    // Inject user-side prompts immediately before the matching assistant response
                    // so that manager history maintains a valid User→Assistant alternation.
                    if (!planPromptInjected &&
                        prior.AgentName is ManagerPlanTag or ManagerReplanTag)
                    {
                        // First plan (or replan when no separate plan was ever emitted):
                        // inject the original planning prompt.
                        //
                        // Guard: if the last managerHistory entry is already a User message it
                        // means the Internal/facts response was compacted away — adding another
                        // User message would create two consecutive User turns which many
                        // providers reject. Inject a synthetic Assistant response first.
                        if (managerHistory.Count > 0 && managerHistory[^1].Role == ChatRole.User)
                        {
                            managerHistory.Add(new ChatMessage(ChatRole.Assistant,
                                "(Fact-gathering response not available in this compacted history window.)")
                            { AuthorName = ManagerInternalTag });
                        }
                        managerHistory.Add(new ChatMessage(ChatRole.User, BuildPlanningPrompt(config.Agents)));
                        planPromptInjected = true;
                    }
                    else if (planPromptInjected && prior.AgentName == ManagerReplanTag)
                    {
                        // Subsequent replans: the live replan prompt is not persisted in the
                        // checkpoint stream, so inject a synthetic bridging user turn.
                        managerHistory.Add(new ChatMessage(ChatRole.User,
                            "The team stalled. Please revise the plan based on recent progress."));
                    }

                    // Manager messages belong in manager history so it can re-orient.
                    var mgrMsg = new ChatMessage(role, ContextWindowFilter.TruncateReplayContent(prior));
                    if (role == ChatRole.Assistant) mgrMsg.AuthorName = prior.AgentName;
                    managerHistory.Add(mgrMsg);
                }
                else
                {
                    var sharedMsg = new ChatMessage(role, ContextWindowFilter.TruncateReplayContent(prior));
                    if (role == ChatRole.Assistant && prior.AgentName is not null)
                        sharedMsg.AuthorName = prior.AgentName;
                    sharedHistory.Add(sharedMsg);
                }
            }

            // Compaction may have dropped the original manager plan exchange. Detect this by
            // checking whether planPromptInjected is still false after the loop — meaning no
            // [MagenticManager:Plan] or [MagenticManager:Replan] message survived in the
            // retained history. Without correction, managerHistory contains only the bare
            // fact-gather User prompt. The first ledger call would then append another User
            // prompt, producing two consecutive User messages — which many providers reject.
            // Inject synthetic exchanges to restore valid User→Assistant alternation.
            if (!planPromptInjected)
            {
                managerHistory.Add(new ChatMessage(ChatRole.Assistant,
                    "(Prior context was compacted — original fact-gather response not available in this window.)")
                { AuthorName = ManagerInternalTag });

                if (currentPlan is not null)
                {
                    // Inject planning prompt + the recovered plan so the manager has context
                    // of its own prior plan before the first ledger evaluation prompt arrives.
                    managerHistory.Add(new ChatMessage(ChatRole.User, BuildPlanningPrompt(config.Agents)));
                    managerHistory.Add(new ChatMessage(ChatRole.Assistant, currentPlan) { AuthorName = ManagerPlanTag });
                }
            }

            // If the checkpoint says we were awaiting plan review, re-emit the plan prompt.
            if (awaitingPlanReview && currentPlan is not null && approvalService is not null)
            {
                var feedback = await approvalService.PromptPlanReviewAsync(currentPlan);
                while (feedback is not null)
                {
                    managerHistory.Add(new ChatMessage(ChatRole.User,
                        $"[Plan revision requested]: {feedback}"));
                    var (revisedPlan, revCost) = await InvokeManagerAsync(managerHistory, cancellationToken);
                    currentPlan = revisedPlan;
                    managerHistory.Add(new ChatMessage(ChatRole.Assistant, currentPlan) { AuthorName = ManagerPlanTag });
                    cumulativeTokens += revCost?.TotalTokens ?? 0;

                    UpdateState(currentPlan, roundIndex, stallCount, resetCount, awaitingReview: true);
                    yield return MakeMessage(ManagerPlanTag, currentPlan, turn++, revCost);

                    feedback = await approvalService.PromptPlanReviewAsync(currentPlan);
                }
                UpdateState(currentPlan, roundIndex, stallCount, resetCount, awaitingReview: false);
            }
        }
        else
        {
            // Phase 0: Fact Gathering

            sharedHistory.Add(new ChatMessage(ChatRole.User, task));

            var factPrompt = BuildFactGatherPrompt(task, config.Agents);
            managerHistory.Add(new ChatMessage(ChatRole.User, factPrompt));

            logger.LogDebug("[MagenticOrchestrator] Gathering facts...");
            var (facts, factCost) = await InvokeManagerAsync(managerHistory, cancellationToken);
            managerHistory.Add(new ChatMessage(ChatRole.Assistant, facts) { AuthorName = ManagerInternalTag });
            cumulativeTokens += factCost?.TotalTokens ?? 0;

            // Yield facts as an internal message so they appear in the session transcript.
            yield return MakeMessage(ManagerInternalTag, facts, turn++, factCost);

            // Phase 1: Planning

            managerHistory.Add(new ChatMessage(ChatRole.User, BuildPlanningPrompt(config.Agents)));

            logger.LogDebug("[MagenticOrchestrator] Generating initial plan...");
            var (initialPlan, planCost) = await InvokeManagerAsync(managerHistory, cancellationToken);
            currentPlan = initialPlan;
            managerHistory.Add(new ChatMessage(ChatRole.Assistant, currentPlan) { AuthorName = ManagerPlanTag });
            cumulativeTokens += planCost?.TotalTokens ?? 0;

            if (_magConfig.EnablePlanReview && approvalService is not null)
            {
                UpdateState(currentPlan, roundIndex, stallCount, resetCount, awaitingReview: true);
                yield return MakeMessage(ManagerPlanTag, currentPlan, turn++, planCost);

                var feedback = await approvalService.PromptPlanReviewAsync(currentPlan);
                while (feedback is not null)
                {
                    managerHistory.Add(new ChatMessage(ChatRole.User,
                        $"[Plan revision requested]: {feedback}"));
                    var (revisedPlan, revCost) = await InvokeManagerAsync(managerHistory, cancellationToken);
                    currentPlan = revisedPlan;
                    managerHistory.Add(new ChatMessage(ChatRole.Assistant, currentPlan) { AuthorName = ManagerPlanTag });
                    cumulativeTokens += revCost?.TotalTokens ?? 0;

                    UpdateState(currentPlan, roundIndex, stallCount, resetCount, awaitingReview: true);
                    yield return MakeMessage(ManagerPlanTag, currentPlan, turn++, revCost);

                    feedback = await approvalService.PromptPlanReviewAsync(currentPlan);
                }

                UpdateState(currentPlan, roundIndex, stallCount, resetCount, awaitingReview: false);
            }
            else
            {
                yield return MakeMessage(ManagerPlanTag, currentPlan, turn++, planCost);
                UpdateState(currentPlan, roundIndex, stallCount, resetCount, awaitingReview: false);
            }

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync("magentic_plan", agent: ManagerPlanTag, payload: new { plan = currentPlan });
        }

        // Phase 2: Inner Loop

        // Stable for the session lifetime — computed once rather than per-round.
        var participantNames = string.Join(", ", agents.Select(a => a.Name));

        bool emittedFinal = false;

        while (roundIndex < _magConfig.MaxRoundCount && !cancellationToken.IsCancellationRequested)
        {
            var ledgerPrompt = BuildLedgerPrompt(sharedHistory, currentPlan, participantNames);

            // Evaluate progress — use a windowed snapshot of manager history to prevent long
            // sessions with many replan cycles from overflowing the manager model's context.
            // Keeps the first ManagerHistoryBootstrapMessages (fact-gather + plan) plus the most recent tail.
            IEnumerable<ChatMessage> ledgerBase = managerHistory.Count <= ManagerHistoryWindow
                ? managerHistory
                : managerHistory.Take(ManagerHistoryBootstrapMessages).Concat(managerHistory.TakeLast(ManagerHistoryWindow - ManagerHistoryBootstrapMessages));

            var ledgerContext = new List<ChatMessage>(ledgerBase)
            {
                new(ChatRole.User, ledgerPrompt)
            };

            logger.LogDebug("[MagenticOrchestrator] Evaluating progress (round {Round})...", roundIndex);
            var (ledgerText, ledgerCost) = await InvokeManagerAsync(ledgerContext, cancellationToken);
            cumulativeTokens += ledgerCost?.TotalTokens ?? 0;
            var ledger = ParseLedger(ledgerText);

            if (ledger is null)
            {
                logger.LogWarning("[MagenticOrchestrator] Failed to parse progress ledger on round {Round}; counting as stall.", roundIndex);
                stallCount++;
            }
            else if (ledger.IsRequestSatisfied)
            {
                // Task complete — synthesize and yield the final answer.
                string finalContent;
                TokenUsage? finalCost = null;

                // Guard against models that output the string "null" instead of JSON null —
                // the prompt instructs JSON null but some models comply only partially.
                if (!string.IsNullOrWhiteSpace(ledger.FinalAnswer) &&
                    !string.Equals(ledger.FinalAnswer, "null", StringComparison.OrdinalIgnoreCase))
                {
                    finalContent = ledger.FinalAnswer;
                }
                else
                {
                    (finalContent, finalCost) = await SynthesizeFinalAnswerAsync(managerHistory, sharedHistory, cancellationToken);
                    cumulativeTokens += finalCost?.TotalTokens ?? 0;
                }

                UpdateState(currentPlan, roundIndex, stallCount, resetCount, awaitingReview: false);
                yield return MakeMessage(ManagerFinalTag, finalContent, turn++, finalCost);

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync("magentic_complete", agent: ManagerFinalTag,
                        payload: new { rounds = roundIndex });
                emittedFinal = true;
                break;
            }
            else
            {
                if (!ledger.IsProgressBeingMade || ledger.IsInLoop)
                    stallCount++;
                else
                    stallCount = 0;
            }

            // Stall handling

            if (stallCount >= _magConfig.MaxStallCount)
            {
                resetCount++;

                if (resetCount > _magConfig.MaxResetCount)
                {
                    logger.LogWarning("[MagenticOrchestrator] Max resets ({Max}) reached — terminating.", _magConfig.MaxResetCount);
                    UpdateState(currentPlan, roundIndex, stallCount, resetCount, awaitingReview: false);
                    yield return MakeMessage(ManagerFinalTag,
                        $"The session could not make further progress after {resetCount - 1} replanning cycles. " +
                        "Please review the conversation history and consider restarting with a more specific task.",
                        turn++, null);
                    emittedFinal = true;
                    break;
                }

                logger.LogInformation("[MagenticOrchestrator] Stall detected — replanning (cycle {Cycle}).", resetCount);
                stallCount = 0;
                roundIndex = 0;

                var replanPrompt = BuildReplanPrompt(sharedHistory, currentPlan);

                // Apply the same history window as ledger evaluation so a high MaxResetCount
                // cannot push the replan call past the manager model's context limit.
                IEnumerable<ChatMessage> replanBase = managerHistory.Count <= ManagerHistoryWindow
                    ? managerHistory
                    : managerHistory.Take(ManagerHistoryBootstrapMessages).Concat(managerHistory.TakeLast(ManagerHistoryWindow - ManagerHistoryBootstrapMessages));
                var replanContext = new List<ChatMessage>(replanBase) { new(ChatRole.User, replanPrompt) };

                var (newPlan, replanCost) = await InvokeManagerAsync(replanContext, cancellationToken);
                currentPlan = newPlan;
                // Record the full exchange in managerHistory for future reference.
                managerHistory.Add(new ChatMessage(ChatRole.User, replanPrompt));
                managerHistory.Add(new ChatMessage(ChatRole.Assistant, currentPlan) { AuthorName = ManagerReplanTag });
                cumulativeTokens += replanCost?.TotalTokens ?? 0;

                UpdateState(currentPlan, roundIndex, stallCount, resetCount, awaitingReview: false);
                yield return MakeMessage(ManagerReplanTag, currentPlan, turn++, replanCost);

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync("magentic_replan", agent: ManagerReplanTag,
                        payload: new { cycle = resetCount, plan = currentPlan });

                continue;
            }

            // Select next participant and invoke

            AIAgent? nextAgent = null;
            if (ledger?.NextSpeaker is { } speakerName && !agentsByName.TryGetValue(speakerName, out nextAgent))
                logger.LogWarning("[MagenticOrchestrator] Manager named unknown agent '{Speaker}'; defaulting to '{Default}'.",
                    speakerName, agents[0].Name);
            nextAgent ??= agents[0];

            AgentStarting?.Invoke(nextAgent.Name ?? "Unknown");
            agentFactory.OnAgentTurnStarting();
            changeTracker?.BeginTurn(nextAgent.Name ?? "Unknown", turn);

            var instruction = ledger is null
                ? "The orchestrator could not evaluate progress. Please summarize your work so far and describe your next steps."
                : ledger.InstructionOrQuestion ?? "Please continue working on the task.";

            // Participant context: system instructions + (filtered) shared history + manager instruction.
            // Apply per-agent ContextWindow filter so agents with ExcludeAgents / TextOnly / MaxTailMessages
            // configured receive the same filtered slice they would in AgentOrchestrator or GraphOrchestrator.
            bool hasInstructions = agentInstructions.TryGetValue(nextAgent.Name ?? "", out var sysInstructions);
            var agentCfg = agentConfigs.GetValueOrDefault(nextAgent.Name ?? "");
            var filteredHistory = ContextWindowFilter.Apply(sharedHistory, agentCfg?.ContextWindow);
            IEnumerable<ChatMessage> participantContext = hasInstructions
                ? [new ChatMessage(ChatRole.System, sysInstructions), .. filteredHistory, new ChatMessage(ChatRole.User, instruction)]
                : [.. filteredHistory, new ChatMessage(ChatRole.User, instruction)];

            logger.LogDebug("[MagenticOrchestrator] Invoking '{Agent}' (round {Round}): {Instruction}",
                nextAgent.Name, roundIndex, StringHelpers.Truncate(instruction, 120));

            AgentResponse response = governanceKernel?.CircuitBreaker is { } cb
                ? await cb.ExecuteAsync(() => nextAgent.RunAsync(participantContext, null, null, cancellationToken))
                : await nextAgent.RunAsync(participantContext, null, null, cancellationToken);

            // Append participant response to shared history.
            foreach (var msg in response.Messages)
            {
                if (msg.Role == ChatRole.Assistant && string.IsNullOrEmpty(msg.AuthorName))
                    msg.AuthorName = nextAgent.Name;
                sharedHistory.Add(msg);
            }

            var agentMsg = new AgentMessage
            {
                AgentName = nextAgent.Name ?? "Unknown",
                Content   = response.Text ?? string.Empty,
                Role      = "assistant",
                TurnIndex = turn++,
                Usage     = ExtractUsage(response),
                ToolCalls = ExtractToolCalls(response.Messages)
            };

            cumulativeTokens += agentMsg.Usage?.TotalTokens ?? 0;
            roundIndex++;

            var warnThreshold = config.WarnTurnTokens;
            if (warnThreshold > 0 && agentMsg.Usage?.InputTokens is { } inputToks && inputToks > warnThreshold)
                TokenBudgetWarning?.Invoke(agentMsg.AgentName, inputToks, warnThreshold);

            // Yield and snapshot state before checking the budget so the participant's response
            // is always visible in the transcript even if it was the turn that pushed over the
            // limit — the work was done and the tokens were already consumed regardless.
            UpdateState(currentPlan, roundIndex, stallCount, resetCount, awaitingReview: false);
            yield return agentMsg;

            if (config.MaxTotalTokens is { } limit && cumulativeTokens > limit)
                throw new BudgetExceededException(cumulativeTokens, limit);

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync("turn_end",
                    agent: agentMsg.AgentName,
                    turn:  agentMsg.TurnIndex,
                    payload: new
                    {
                        input_tokens  = agentMsg.Usage?.InputTokens,
                        output_tokens = agentMsg.Usage?.OutputTokens,
                    });

            if (changeTracker is not null)
            {
                try { await changeTracker.FlushTurnAsync(agentMsg.AgentName, agentMsg.TurnIndex, CancellationToken.None); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "ChangeTracker flush failed for turn {Turn} ({Agent}).", agentMsg.TurnIndex, agentMsg.AgentName);
                }
            }
        }

        // Emit a terminal message when the loop exhausted MaxRoundCount without self-terminating
        // (i.e. neither IsRequestSatisfied nor max-resets fired). Without this the session ends
        // at the last participant message with no synthesized answer and no explanation.
        if (!emittedFinal && !cancellationToken.IsCancellationRequested)
        {
            UpdateState(currentPlan, roundIndex, stallCount, resetCount, awaitingReview: false);
            yield return MakeMessage(ManagerFinalTag,
                $"The session reached the maximum of {_magConfig.MaxRoundCount} coordination rounds " +
                "without completing the task. Review the conversation history and consider restarting " +
                "with a more specific task or a higher MaxRoundCount.",
                turn, null);
        }
    }

    // Manager invocation

    private async Task<(string Text, TokenUsage? Usage)> InvokeManagerAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var systemInstruction = _magConfig.Instructions ?? BuildDefaultManagerInstructions();
        var context = new List<ChatMessage>
        {
            new(ChatRole.System, systemInstruction)
        };
        context.AddRange(messages);

        var response = await managerClient.GetResponseAsync(context, cancellationToken: cancellationToken);
        var text = response.Text?.Trim() ?? string.Empty;

        TokenUsage? usage = null;
        if (response.Usage is { } u)
        {
            var inputTokens  = (int)(u.InputTokenCount  ?? 0L);
            var outputTokens = (int)(u.OutputTokenCount ?? 0L);
            if (inputTokens > 0 || outputTokens > 0)
                usage = new TokenUsage(inputTokens, outputTokens);
        }

        return (text, usage);
    }

    private async Task<(string Text, TokenUsage? Usage)> SynthesizeFinalAnswerAsync(
        IReadOnlyList<ChatMessage> managerHistory,
        IReadOnlyList<ChatMessage> sharedHistory,
        CancellationToken cancellationToken)
    {
        // Apply the same window as ledger evaluation so a context-limit error cannot fire
        // at the exact moment the task completes.
        IEnumerable<ChatMessage> historyBase = managerHistory.Count <= ManagerHistoryWindow
            ? managerHistory
            : managerHistory.Take(ManagerHistoryBootstrapMessages).Concat(managerHistory.TakeLast(ManagerHistoryWindow - ManagerHistoryBootstrapMessages));

        var summaryContext = new List<ChatMessage>(historyBase);
        summaryContext.Add(new ChatMessage(ChatRole.User, BuildFinalAnswerPrompt(sharedHistory)));
        return await InvokeManagerAsync(summaryContext, cancellationToken);
    }

    // Ledger parsing

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        AllowTrailingCommas         = true,
    };

    // Compiled regex that extracts the first complete JSON object from free-form manager text.
    // Greedy quantifier is intentional: it captures the entire outermost JSON object (including
    // nested braces) when the manager prefixes its response with explanatory prose.
    private static readonly Regex _jsonExtractRegex = new(@"\{[\s\S]+\}", RegexOptions.Compiled);

    internal MagenticProgressLedger? ParseLedger(string text)
    {
        // Strip markdown fences if present.
        var json = text.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var start = json.IndexOf('\n') + 1;
            var end   = json.LastIndexOf("```", StringComparison.Ordinal);
            if (start > 0 && end > start)
                json = json[start..end].Trim();
        }

        // Find first complete JSON object if the LLM prefixed prose.
        if (!json.StartsWith('{'))
        {
            var match = _jsonExtractRegex.Match(json);
            if (match.Success) json = match.Value;
        }

        try
        {
            return JsonSerializer.Deserialize<MagenticProgressLedger>(json, _jsonOpts);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("[MagenticOrchestrator] Ledger parse failed: {Error}. Raw: {Raw}",
                ex.Message, StringHelpers.Truncate(text, 300));
            return null;
        }
    }

    // Prompt builders

    private static string BuildDefaultManagerInstructions() => """
        You are MagenticManager, the orchestrator of a multi-agent team.
        Your role is to coordinate participants, track progress, and ensure the task is completed
        efficiently. You do NOT directly perform tasks yourself — you direct the right agent to do
        the right work at the right time.
        Be concise, precise, and always keep the final goal in sight.
        """;

    private static string BuildFactGatherPrompt(string task, IList<AgentConfig> agentConfigs) => $"""
        TASK:
        {task}

        TEAM:
        {BuildTeamDescription(agentConfigs)}

        INSTRUCTIONS:
        Review the task carefully and answer the following:
        1. KNOWN FACTS — what do we know for certain about this task?
        2. CONSTRAINTS — what limitations or requirements must be respected?
        3. OPEN QUESTIONS — what needs to be clarified or discovered?

        Be concise. Focus only on information most relevant to completing the task.
        """;

    private static string BuildPlanningPrompt(IList<AgentConfig> agentConfigs) => $"""
        Based on the task, facts, and constraints above, create a STEP-BY-STEP PLAN.

        For each step specify:
        - Which team member handles it
        - What they must do
        - What the expected output or deliverable is

        TEAM:
        {BuildTeamDescription(agentConfigs)}

        Keep the plan realistic and achievable. Prefer fewer, larger steps over many tiny ones.
        """;

    private static string BuildTeamDescription(IList<AgentConfig> agentConfigs) =>
        string.Join("\n", agentConfigs.Select(a =>
            a.Description is not null
                ? $"  - {a.Name}: {a.Description}"
                : $"  - {a.Name}"));

    private static string BuildLedgerPrompt(
        IReadOnlyList<ChatMessage> sharedHistory,
        string? currentPlan,
        string participantNames)
    {
        var historyText = string.Join("\n\n", sharedHistory
            .Where(m => !string.IsNullOrEmpty(m.Text))
            .TakeLast(LedgerConversationWindow)
            .Select(m => $"[{m.AuthorName ?? m.Role.Value}]: {m.Text}"));

        return $$"""
            CURRENT PLAN:
            {{currentPlan ?? "(no plan yet)"}}

            CONVERSATION SO FAR:
            {{historyText}}

            AVAILABLE AGENTS: {{participantNames}}

            Evaluate the current state of progress and respond with ONLY a JSON object — no markdown, no explanation:
            {
              "is_request_satisfied": <true|false>,
              "is_in_loop": <true|false>,
              "is_progress_being_made": <true|false>,
              "next_speaker": "<exact agent name from available agents>",
              "instruction_or_question": "<clear, specific, actionable instruction>",
              "final_answer": null
            }

            RULES:
            - "is_request_satisfied": true ONLY when the task is fully complete and verified.
            - "is_in_loop": true when the team is repeating steps without new progress.
            - "is_progress_being_made": true when the last round moved the task forward.
            - "next_speaker": must be EXACTLY one of: {{participantNames}}
            - "final_answer": a comprehensive summary when is_request_satisfied is true; JSON null (not the string "null") otherwise.
            """;
    }

    private static string BuildReplanPrompt(IReadOnlyList<ChatMessage> sharedHistory, string? oldPlan)
    {
        var historyText = string.Join("\n\n", sharedHistory
            .Where(m => !string.IsNullOrEmpty(m.Text))
            .TakeLast(ReplanConversationWindow)
            .Select(m => $"[{m.AuthorName ?? m.Role.Value}]: {m.Text}"));

        return $"""
            The team has been unable to make progress following the current plan.

            PREVIOUS PLAN:
            {oldPlan ?? "(unknown)"}

            RECENT CONVERSATION:
            {historyText}

            Create a REVISED PLAN that takes a different approach to complete the task.
            Acknowledge what has been attempted and why it hasn't worked, then describe
            a concrete alternative strategy.
            """;
    }

    private static string BuildFinalAnswerPrompt(IReadOnlyList<ChatMessage> sharedHistory)
    {
        var historyText = string.Join("\n\n", sharedHistory
            .Where(m => !string.IsNullOrEmpty(m.Text))
            .TakeLast(FinalAnswerConversationWindow)
            .Select(m => $"[{m.AuthorName ?? m.Role.Value}]: {m.Text}"));

        return $"""
            The task has been completed. Synthesize a final, comprehensive answer that covers:
            1. What was accomplished
            2. Key results and outputs
            3. Any important notes or caveats

            CONVERSATION:
            {historyText}

            Be clear, concise, and useful to the person who submitted the original task.
            """;
    }

    // State helpers

    private void UpdateState(
        string? plan,
        int roundIndex,
        int stallCount,
        int resetCount,
        bool awaitingReview)
    {
        CurrentState = new MagenticCheckpointState
        {
            CurrentPlan        = plan,
            RoundIndex         = roundIndex,
            StallCount         = stallCount,
            ResetCount         = resetCount,
            AwaitingPlanReview = awaitingReview,
        };
    }

    private static AgentMessage MakeMessage(
        string agentName,
        string content,
        int turnIndex,
        TokenUsage? usage)
        => new()
        {
            AgentName = agentName,
            Content   = content,
            Role      = "assistant",
            TurnIndex = turnIndex,
            Usage     = usage,
        };

    // Usage extraction (mirrors AgentOrchestrator)

    private static TokenUsage? ExtractUsage(AgentResponse response)
    {
        if (response.Usage is null) return null;

        var inputTokens  = (int)(response.Usage.InputTokenCount  ?? 0L);
        var outputTokens = (int)(response.Usage.OutputTokenCount ?? 0L);
        if (inputTokens == 0 && outputTokens == 0) return null;

        return new TokenUsage(inputTokens, outputTokens);
    }

    private static IReadOnlyList<ToolCallRecord>? ExtractToolCalls(IList<ChatMessage> messages)
    {
        var calls   = new List<(string CallId, string Name, string? ArgsSummary)>();
        var results = new Dictionary<string, bool>(StringComparer.Ordinal);

        try
        {
            foreach (var msg in messages)
            {
                foreach (var content in msg.Contents)
                {
                    if (content is FunctionCallContent fc)
                        calls.Add((fc.CallId ?? fc.Name, fc.Name, ToolCallHelper.SummarizeArgs(fc.Arguments)));
                    else if (content is FunctionResultContent fr)
                    {
                        var key  = fr.CallId ?? string.Empty;
                        var text = fr.Result?.ToString() ?? string.Empty;
                        var ok   = !text.StartsWith("[ERROR]",     StringComparison.Ordinal)
                                && !text.StartsWith("[DENIED]",    StringComparison.Ordinal)
                                && !text.StartsWith("[TIMEOUT]",   StringComparison.Ordinal)
                                && !text.StartsWith("[NOT FOUND]", StringComparison.Ordinal)
                                && !text.StartsWith("[EXIT ",      StringComparison.Ordinal);
                        if (!string.IsNullOrEmpty(key)) results[key] = ok;
                    }
                }
            }
        }
        catch (Exception) { /* best-effort extraction; do not let parsing errors propagate */ }

        if (calls.Count == 0) return null;

        return calls
            .Select(c => new ToolCallRecord(c.Name, c.ArgsSummary, results.TryGetValue(c.CallId, out var s) ? s : true))
            .ToList();
    }

}
