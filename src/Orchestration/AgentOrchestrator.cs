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
using fuseraft.Orchestration.Strategies;

// Disambiguate from Microsoft.Agents.AI.AgentFactory
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Plugins;
using AgentFactory = fuseraft.Infrastructure.Agents.AgentFactory;

namespace fuseraft.Orchestration;

/// <summary>
/// Main orchestration engine. Builds agents from configuration and drives a custom
/// multi-agent loop to completion, emitting messages as they arrive.
/// </summary>
public sealed class AgentOrchestrator(
    OrchestrationConfig config,
    AgentFactory agentFactory,
    StrategyFactory strategyFactory,
    ILogger<AgentOrchestrator> logger,
    ChangeTracker? changeTracker = null,
    EventEmitter? eventEmitter = null,
    GovernanceKernel? governanceKernel = null,
    fuseraft.Infrastructure.Memory.MemoryManager? memoryManager = null,
    ContextAssembler? contextAssembler = null,
    DependencyPlanner? dependencyPlanner = null,
    fuseraft.Core.Interfaces.IContextAssemblyPipeline? contextPipeline = null,
    fuseraft.Infrastructure.Repository.RepositoryKnowledgeStore? repositoryKnowledgeStore = null) : IOrchestrator
{
    // IOrchestrator

    public async Task<OrchestrationResult> RunAsync(
        string task,
        IReadOnlyList<AgentMessage>? priorHistory = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = StringHelpers.NewSessionId();
        var messages = new List<AgentMessage>();
        var start = DateTime.UtcNow;

        logger.LogInformation(
            "Session {SessionId} | Starting orchestration '{Name}' | Task: {TaskPreview}",
            sessionId, config.Name, StringHelpers.Truncate(task, 120));

        try
        {
            await foreach (var msg in StreamAsync(task, priorHistory, cancellationToken).ConfigureAwait(false))
                messages.Add(msg);

            logger.LogInformation(
                "Session {SessionId} | Completed - {Turns} turns in {Elapsed:0.0}s",
                sessionId, messages.Count, (DateTime.UtcNow - start).TotalSeconds);

            return new OrchestrationResult
            {
                SessionId = sessionId,
                Succeeded = true,
                Messages = messages,
                Duration = DateTime.UtcNow - start,
                TerminationReason = "Completed"
            };
        }
        catch (BudgetExceededException ex)
        {
            logger.LogWarning("Session {SessionId} | Token budget exceeded — {Actual:N0} > {Limit:N0}",
                sessionId, ex.ActualTokens, ex.LimitTokens);
            return new OrchestrationResult
            {
                SessionId = sessionId,
                Succeeded = false,
                Messages = messages,
                Duration = DateTime.UtcNow - start,
                TerminationReason = "BudgetExceeded",
                ErrorMessage = ex.Message
            };
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Session {SessionId} | Cancelled after {Turns} turns", sessionId, messages.Count);
            return new OrchestrationResult
            {
                SessionId = sessionId,
                Succeeded = false,
                Messages = messages,
                Duration = DateTime.UtcNow - start,
                TerminationReason = "Cancelled",
                ErrorMessage = "Operation was cancelled by the caller."
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Session {SessionId} | Failed after {Turns} turns", sessionId, messages.Count);
            return new OrchestrationResult
            {
                SessionId = sessionId,
                Succeeded = false,
                Messages = messages,
                Duration = DateTime.UtcNow - start,
                TerminationReason = "Error",
                ErrorMessage = ex.Message
            };
        }
    }

    // volatile: written by SetSessionId (may be called from outside) and read across
    // async continuations that may run on different thread-pool threads.
    private volatile string _sessionId = string.Empty;

    // Points to the OrchestrationSession for the currently running StreamAsync call.
    // volatile: the diagnostic hook callback reads this field from whatever thread the
    // emitter fires on; the assignment in StreamAsync must be visible immediately.
    private volatile OrchestrationSession? _activeSession;

    /// <summary>
    /// The active selection strategy's snapshot capability for the current session, or null
    /// when the current strategy does not support snapshotting (e.g. keyword or LLM strategy).
    /// </summary>
    public fuseraft.Core.Interfaces.IContextSnapshotter? CurrentSnapshotter => _activeSession?.Snapshotter;

    // Guards single hook registration across multiple StreamAsync calls on the same instance.
    // 0 = unregistered, 1 = registered. Written with Interlocked.CompareExchange to prevent
    // double-registration when two concurrent StreamAsync calls race to register.
    private int _hookRegistered;

    /// <summary>
    /// Stamps the session ID onto routing/termination strategies so governance audit events
    /// carry a correlation ID. Called from the CLI after the checkpoint session ID is known.
    /// </summary>
    public void SetSessionId(string sessionId)
    {
        _sessionId = sessionId;
        agentFactory.SetSessionId(sessionId);
        contextAssembler?.SetSessionId(sessionId);
        contextPipeline?.SetSessionId(sessionId);
    }

    private TaskModel? _structuredTask;

    /// <summary>
    /// Sets the structured task model injected into history at session start.
    /// Call before <see cref="StreamAsync"/> to provide goal, constraints, and active targets.
    /// When null (default), no task model block is injected.
    /// </summary>
    public void SetStructuredTask(TaskModel? model) => _structuredTask = model;

    // State machine state name to restore on the next StreamAsync call after compaction.
    // Consumed once and cleared so subsequent phase restarts infer state from signals normally.
    private volatile string? _resumeStateName;

    // Full failure-tracking snapshot to restore alongside the state name. Populated by
    // SessionRunner.ApplyCompactionAsync when a StateMachineSelectionStrategy is active.
    private volatile StateMachineCheckpointState? _resumeSnapshot;

    /// <inheritdoc/>
    public void SetResumeStateName(string? stateName) => _resumeStateName = stateName;

    /// <summary>
    /// Stores the failure-tracking counters to restore on the next <c>StreamAsync</c> call.
    /// Called by <see cref="fuseraft.Cli.SessionRunner"/> after compaction so counters such as
    /// <c>_transitionFailure</c> and <c>_visitedStates</c> survive across restarts.
    /// </summary>
    public void SetResumeSnapshot(StateMachineCheckpointState? snap) => _resumeSnapshot = snap;


    /// <summary>
    /// Fires synchronously when an agent is selected but before its <c>RunAsync</c> is called.
    /// Useful for updating UI status before a potentially long-running turn begins.
    /// </summary>
    public event Action<string>? AgentStarting;

    /// <inheritdoc/>
    public event Action<string, string, string?>? ToolCalling;

    /// <inheritdoc/>
    public event Action<string, int, int>? TokenBudgetWarning;

    public async IAsyncEnumerable<AgentMessage> StreamAsync(
        string task,
        IReadOnlyList<AgentMessage>? priorHistory = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (config.Agents.Count == 0)
            throw new InvalidOperationException("Orchestration config has no agents defined.");

        // Capture pre-session configuration into a session object, consuming the one-shot
        // resume fields immediately so a subsequent StreamAsync call cannot re-apply them.
        var session = new OrchestrationSession(_sessionId, _resumeStateName, _resumeSnapshot);
        _resumeStateName = null;
        _resumeSnapshot  = null;
        _activeSession   = session;

        // Build fresh agents and strategies per session to avoid state bleed.
        var agents = config.Agents
            .Select(a => agentFactory.Create(a, config.ContextBudget, onToolCalling: (agent, tool, args) => ToolCalling?.Invoke(agent, tool, args)))
            .ToList();
        if (!string.IsNullOrEmpty(_sessionId))
            strategyFactory.SetSessionId(_sessionId);
        var selection = strategyFactory.CreateSelection(config.Selection, agents, config.Validation, config.FailureHandling, config.Contracts, config.Verifier);
        session.Snapshotter = selection as fuseraft.Core.Interfaces.IContextSnapshotter;
        var termination = strategyFactory.CreateTermination(config.Termination ?? new(), agents, config.Validation);

        // Resolve the optional verifier agent once per session.
        AIAgent? verifierAgent = config.Verifier?.AgentName is { Length: > 0 } verifierName
            ? agents.FirstOrDefault(a => string.Equals(a.Name, verifierName, StringComparison.OrdinalIgnoreCase))
            : null;

        // Shared history — all agents read from and write to this list.
        var history = session.History;

        // Register the validation diagnostic hook once per orchestrator instance.
        // The hook watches for validation_fail events and injects change-log context
        // into history on repeated failures so the re-invoked agent has ground-truth
        // data rather than only the validator's error message.
        if (Interlocked.CompareExchange(ref _hookRegistered, 1, 0) == 0
            && eventEmitter is not null && config.ChangeTracking is { } ctCfg)
        {
            eventEmitter.RegisterHook(
                new ValidationDiagnosticHook(ctCfg.Path, msg => _activeSession?.History.Add(msg)));
        }

        // Give selection and termination strategies a reference to the shared history
        // so they can inject correction messages when routing validators block a handoff.
        if (selection is KeywordSelectionStrategy kss)
        {
            kss.SetHistory(history);
            if (!string.IsNullOrEmpty(_sessionId))
                kss.SetSessionId(_sessionId);
            kss.SetDidResolver(agentFactory.GetDid);
        }
        else if (selection is StructuredSelectionStrategy sss)
        {
            sss.SetHistory(history);
        }
        else if (selection is StateMachineSelectionStrategy smss)
        {
            smss.SetHistory(history);
            if (!string.IsNullOrEmpty(_sessionId))
                smss.SetSessionId(_sessionId);

            // Restore state after compaction so the machine resumes from e.g. "Testing"
            // rather than resetting to its initial state ("Planning").
            if (!string.IsNullOrWhiteSpace(session.ResumeStateName))
                smss.SetCurrentState(session.ResumeStateName);

            // Restore failure-tracking counters so MaxConsecutiveContractFailures and
            // the REPLAN BLOCKED guard survive across compaction cycles.
            smss.RestoreFromSnapshot(session.ResumeSnapshot);
        }
        WireHistory(termination, history);
        if (!string.IsNullOrEmpty(_sessionId))
            WireSessionId(termination, _sessionId);
        WireDidResolver(termination, agentFactory.GetDid);

        // Inject the initial user task, optionally preceded by a structured task model
        // block so agents know the goal, constraints, and active file targets up front.
        if (_structuredTask is { } taskModel)
            history.Add(new ChatMessage(ChatRole.System, taskModel.FormatForContext()));

        history.Add(new ChatMessage(ChatRole.User, task));

        // Re-inject prior history so agents continue where they left off.
        if (priorHistory?.Count > 0)
        {
            logger.LogDebug("Resuming session... replaying {Turns} prior turns.", priorHistory.Count);

            // Build a set of signals that are valid exits for the current state so that
            // wrong-signal handoff calls from a prior stuck run are not reconstructed as
            // plain text. Surfacing them would mislead the resumed agent into copying the
            // bad signal rather than emitting the correct one.
            var validSignalsForCurrentState = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var currentStateAgentName = string.Empty;
            if (!string.IsNullOrWhiteSpace(session.ResumeStateName)
                && config.Selection.StateMachine?.States.TryGetValue(
                    session.ResumeStateName, out var resumeStateConfig) == true)
            {
                currentStateAgentName = resumeStateConfig.Agent ?? string.Empty;
                foreach (var t in resumeStateConfig.Transitions.Where(t => !string.IsNullOrWhiteSpace(t.Signal)))
                    validSignalsForCurrentState.Add(t.Signal!);
            }

            foreach (var prior in priorHistory)
            {
                var role    = prior.Role == MessageRole.User ? ChatRole.User : ChatRole.Assistant;
                var content = ContextWindowFilter.TruncateReplayContent(prior);

                // FunctionCallContent is not preserved in AgentMessage, so tool-call-only turns
                // (zero text, finish_reason=tool_calls) replay as empty messages. Recover the
                // handoff keyword so IsSignalOnOwnLine can detect it without FunctionCallContent.
                // Only inject signals that are valid for the current state when the message
                // is from the current state's agent — a wrong signal from a prior stuck run
                // would appear as an in-context example and confuse the resumed model.
                if (role == ChatRole.Assistant && string.IsNullOrEmpty(content))
                {
                    var handoff = prior.ToolCalls?.FirstOrDefault(tc =>
                        string.Equals(tc.Name, HandoffPlugin.FunctionName, StringComparison.OrdinalIgnoreCase));
                    if (handoff?.ArgsSummary is { } s &&
                        s.StartsWith($"{HandoffPlugin.ArgumentName}=", StringComparison.OrdinalIgnoreCase))
                    {
                        var routeKeyword = s[(HandoffPlugin.ArgumentName.Length + 1)..].Trim();
                        bool isCurrentAgent = string.Equals(
                            prior.AgentName, currentStateAgentName, StringComparison.OrdinalIgnoreCase);
                        bool isValidSignal = validSignalsForCurrentState.Count == 0
                            || validSignalsForCurrentState.Contains(routeKeyword);
                        if (!isCurrentAgent || isValidSignal)
                            content = routeKeyword;
                    }
                }

                var msg = new ChatMessage(role, content);
                if (role == ChatRole.Assistant && prior.AgentName is not null)
                    msg.AuthorName = prior.AgentName;
                history.Add(msg);
            }
        }

        // Build a lookup of agent name → system instruction.
        // Instructions are injected manually here because AgentFactory.MergeOptions strips
        // ChatOptions.Instructions before the request reaches the OpenAI SDK — the agent stores
        // them, but they are not forwarded. Manual prepend is the only path that reaches the model.
        var agentInstructions = config.Agents
            .Where(a => !string.IsNullOrWhiteSpace(a.Instructions))
            .ToDictionary(a => a.Name, a => a.Instructions, StringComparer.OrdinalIgnoreCase);

        // Build a lookup of agent name → full agent config for per-agent options (e.g. ContextWindow).
        var agentConfigs = config.Agents
            .ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

        // Seed turn index from the last prior message so compacted histories produce correct indices.
        int turn = priorHistory is { Count: > 0 }
            ? priorHistory[^1].TurnIndex + 1
            : 0;
        int cumulativeTokens = priorHistory?
            .Sum(m => m.Usage?.TotalTokens ?? 0) ?? 0;

        // ChatMessage carries no per-message usage, so a tokenbudget termination condition
        // can't compute its own total from history the way regex/structured do — wire in a
        // live reader over this closure-captured counter instead.
        WireTokenBudget(termination, () => cumulativeTokens);

        while (true)
        {
            // Hard iteration cap — takes effect regardless of the termination strategy.
            if (config.Termination?.ResolveMaxIterations() is > 0 and var maxIter && turn >= maxIter)
            {
                if (eventEmitter is not null)
                {
                    _ = eventEmitter.EmitAsync(EventTypes.MaxTurnsExceeded,
                        payload: new { turn, max = maxIter });
                    _ = eventEmitter.EmitAsync(EventTypes.TerminationForced,
                        payload: new { reason = "max_turns_exceeded", turn, max = maxIter });
                }
                break;
            }

            // Parallel fan-out: check before the normal sequential SelectAsync path.
            if (selection is IParallelAgentSelector psel)
            {
                var batch = await psel.TrySelectParallelAsync(agents, history, cancellationToken);
                if (batch is not null)
                {
                    // Build one run-task per branch, each with an isolated history snapshot.
                    var branchTasks = batch.Branches.Select(async branch =>
                    {
                        var (branchAgent, _) = branch;
                        var snapshot = new List<ChatMessage>(history);

                        AgentStarting?.Invoke(branchAgent.Name ?? "Unknown");
                        agentFactory.OnAgentTurnStarting();
                        changeTracker?.BeginTurn(branchAgent.Name ?? "Unknown", turn);

                        IEnumerable<ChatMessage> context;
                        if (contextPipeline is not null)
                        {
                            var bAssembled = await contextPipeline.AssembleAsync(
                                new AgentExecutionRequest
                                {
                                    AgentName  = branchAgent.Name ?? string.Empty,
                                    Task       = task,
                                    SharedHistory = snapshot,
                                    AgentConfig   = agentConfigs.GetValueOrDefault(branchAgent.Name ?? ""),
                                    SessionId     = _sessionId,
                                },
                                cancellationToken);
                            context = bAssembled.Messages;
                            if (eventEmitter is not null)
                                await EmitContextAssemblyAsync(eventEmitter, bAssembled.Metrics, turn,
                                    agentFactory.GetToolCount(branchAgent.Name ?? ""));
                        }
                        else
                        {
                            // Legacy fallback when no pipeline is wired (non-AgentOrchestrator paths).
                            bool hasInstr = agentInstructions.TryGetValue(branchAgent.Name ?? "", out var instr);
                            if (memoryManager is not null)
                                instr = await memoryManager.AugmentInstructionsAsync(branchAgent.Name ?? "", instr, cancellationToken);
                            var bAgentCfg = agentConfigs.GetValueOrDefault(branchAgent.Name ?? "");
                            var filtered  = ContextWindowFilter.Apply(snapshot, bAgentCfg?.ContextWindow);
                            context = (hasInstr || memoryManager is not null) && instr is not null
                                ? [new ChatMessage(ChatRole.System, instr), .. filtered]
                                : filtered;
                        }

                        if (eventEmitter is not null)
                            await eventEmitter.EmitAsync(EventTypes.ParallelBranchStart,
                                agent: branchAgent.Name ?? "Unknown",
                                payload: new { turn });

                        AgentResponse response;
                        try
                        {
                            response = governanceKernel?.CircuitBreaker is { } cb
                                ? await cb.ExecuteAsync(() => branchAgent.RunAsync(context, null, null, cancellationToken))
                                : await branchAgent.RunAsync(context, null, null, cancellationToken);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception branchEx)
                        {
                            if (eventEmitter is not null)
                                _ = eventEmitter.EmitAsync(EventTypes.ParallelBranchError,
                                    agent:   branchAgent.Name ?? "Unknown",
                                    payload: new { turn, error = branchEx.Message });
                            throw;
                        }

                        if (eventEmitter is not null)
                            _ = eventEmitter.EmitAsync(EventTypes.ParallelBranchEnd,
                                agent: branchAgent.Name ?? "Unknown",
                                payload: new { turn });

                        return (branchAgent, response);
                    }).ToList();

                    var branchResults = await Task.WhenAll(branchTasks);

                    // Merge branch outputs into the shared history.
                    var mergeInputs = branchResults
                        .Select(r => (r.branchAgent.Name ?? "Unknown", r.response.Text ?? string.Empty))
                        .ToList();

                    // Build an agent-runner delegate for Ranked / SemanticDiff strategies.
                    // Looks up the named merge agent and runs it with the provided context.
                    Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<string>>? mergeAgentRunner = null;
                    if (batch.Merge.Agent is { Length: > 0 } mergeAgentName)
                    {
                        var mergeAgent = agents.FirstOrDefault(a =>
                            string.Equals(a.Name, mergeAgentName, StringComparison.OrdinalIgnoreCase));

                        if (mergeAgent is not null)
                        {
                            bool mHasInstr = agentInstructions.TryGetValue(mergeAgentName, out var mInstr);
                            mergeAgentRunner = async (ctx, ct) =>
                            {
                                IEnumerable<ChatMessage> mContext = mHasInstr && mInstr is not null
                                    ? [new ChatMessage(ChatRole.System, mInstr), .. ctx]
                                    : ctx;

                                AgentResponse mr = governanceKernel?.CircuitBreaker is { } cb
                                    ? await cb.ExecuteAsync(() => mergeAgent.RunAsync(mContext, null, null, ct))
                                    : await mergeAgent.RunAsync(mContext, null, null, ct);

                                return mr.Text ?? string.Empty;
                            };
                        }
                        else
                        {
                            logger.LogWarning(
                                "[Orchestrator] Merge agent '{Agent}' not found in agent pool — " +
                                "Ranked/SemanticDiff will fall back to union.",
                                mergeAgentName);
                        }
                    }

                    var mergedMessages = await MergeEngine.MergeAsync(
                        batch.Merge, mergeInputs, mergeAgentRunner, logger, cancellationToken);
                    foreach (var m in mergedMessages)
                        history.Add(m);

                    // Yield an AgentMessage per branch and accumulate token usage.
                    foreach (var (branchAgent, branchResponse) in branchResults)
                    {
                        var branchMsg = new AgentMessage
                        {
                            AgentName = branchAgent.Name ?? AgentNames.Unknown,
                            Content   = branchResponse.Text ?? string.Empty,
                            Role      = "assistant",
                            TurnIndex = turn++,
                            Usage     = OrchestratorHelpers.ExtractUsage(branchResponse),
                            ToolCalls = ExtractToolCalls(branchResponse.Messages, branchAgent.Name ?? AgentNames.Unknown),
                        };

                        cumulativeTokens += branchMsg.Usage?.TotalTokens ?? 0;
                        eventEmitter?.SetTurn(branchMsg.TurnIndex);

                        if (eventEmitter is not null)
                            await eventEmitter.EmitAsync(EventTypes.TurnEnd,
                                agent:   branchMsg.AgentName,
                                turn:    branchMsg.TurnIndex,
                                payload: new
                                {
                                    input_tokens  = branchMsg.Usage?.InputTokens,
                                    output_tokens = branchMsg.Usage?.OutputTokens,
                                    parallel = true,
                                });

                        if (changeTracker is not null)
                        {
                            try { await changeTracker.FlushTurnAsync(branchMsg.AgentName, branchMsg.TurnIndex, CancellationToken.None); }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex,
                                    "ChangeTracker flush failed for parallel turn {Turn} ({Agent}).",
                                    branchMsg.TurnIndex, branchMsg.AgentName);
                            }
                        }

                        yield return branchMsg;
                    }

                    if (config.MaxTotalTokens is { } pLimit && cumulativeTokens > pLimit)
                        throw new BudgetExceededException(cumulativeTokens, pLimit);

                    if (await termination.ShouldTerminateAsync(history, cancellationToken))
                        break;

                    continue;
                }
            }

            // Select the next agent.
            // Capture the history count before selection so correction messages injected by
            // the strategy (ConflictingEvidence / NoProgress) can be identified afterwards.
            int preSelectCount = history.Count;
            var agent = await selection.SelectAsync(agents, history, cancellationToken);
            int postSelectCount = history.Count;
            if (agent is null) break;

            if (eventEmitter is not null)
                _ = eventEmitter.EmitAsync(EventTypes.SelectionEvaluated,
                    agent: agent.Name ?? "Unknown",
                    turn:  turn,
                    payload: new { selected = agent.Name, strategy = selection.GetType().Name });

            // Prerequisite enforcement: if DependencyPlanner is active and the selected agent
            // has unmet Requires tokens, inject a blocker message into history so the selector
            // knows to route elsewhere, then skip this turn.
            if (dependencyPlanner is { HasDependencies: true } &&
                !dependencyPlanner.CanExecute(agent.Name ?? string.Empty))
            {
                var unmet = dependencyPlanner.GetUnmetRequirements(agent.Name ?? string.Empty);
                var blockerText =
                    $"[DependencyPlanner] Agent '{agent.Name}' is blocked — waiting for prerequisites: " +
                    string.Join(", ", unmet.Select(t => $"'{t}'")) + ". " +
                    "Route to an agent that can produce these tokens first.";

                logger.LogInformation(
                    "[Orchestrator] Prerequisite block: agent '{Agent}' waiting for [{Tokens}].",
                    agent.Name, string.Join(", ", unmet));

                history.Add(new ChatMessage(ChatRole.User, blockerText));
                continue;
            }

            logger.LogDebug(
                "[Orchestrator] Turn {Turn}: selected agent '{Agent}' (Name property='{NameProp}') | history={HistCount} msgs",
                turn, agent.Name, agent.Name, history.Count);

            AgentStarting?.Invoke(agent.Name ?? "Unknown");
            agentFactory.OnAgentTurnStarting();
            changeTracker?.BeginTurn(agent.Name ?? "Unknown", turn);

            var agentCfg = agentConfigs.GetValueOrDefault(agent.Name ?? "");
            var contextList = await BuildContextAsync(
                agent.Name ?? string.Empty, task, history, agentCfg, agentInstructions, turn, cancellationToken);

            logger.LogDebug(
                "[Orchestrator] Invoking '{Agent}' with {ContextCount} context messages " +
                "(history={HistCount})",
                agent.Name,
                contextList.Count,
                history.Count);

            // Pre-turn budget guard: estimate the input token cost of this context slice and
            // abort before the LLM call if cumulative + estimated input would exceed the limit.
            // Prevents the one-turn overshoot that occurs when the post-yield check fires too
            // late (e.g. a file-read turn that consumes tens of thousands of tokens).
            if (config.MaxTotalTokens is { } preTurnLimit)
            {
                var estimatedInputTokens = EstimateContextTokens(contextList);
                if (cumulativeTokens + estimatedInputTokens > preTurnLimit)
                {
                    logger.LogWarning(
                        "[Orchestrator] Pre-turn budget guard: cumulative {Cumulative:N0} + estimated input {Estimated:N0} > limit {Limit:N0} — aborting before turn.",
                        cumulativeTokens, estimatedInputTokens, preTurnLimit);
                    throw new BudgetExceededException(cumulativeTokens + estimatedInputTokens, preTurnLimit);
                }
            }

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync(EventTypes.AgentStart,
                    agent: agent.Name ?? "Unknown",
                    turn:  turn);

            AgentResponse response;
            try
            {
                response = governanceKernel?.CircuitBreaker is { } cb
                    ? await cb.ExecuteAsync(() => agent.RunAsync(contextList, null, null, cancellationToken))
                    : await agent.RunAsync(contextList, null, null, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception agentEx)
            {
                if (eventEmitter is not null)
                    _ = eventEmitter.EmitAsync(EventTypes.AgentError,
                        agent:   agent.Name ?? "Unknown",
                        turn:    turn,
                        payload: new { error = agentEx.GetType().Name, message = agentEx.Message });
                throw;
            }

            logger.LogDebug(
                "[Orchestrator] '{Agent}' returned {MsgCount} message(s). Text='{Preview}'",
                agent.Name, response.Messages.Count,
                (response.Text ?? "").Length > 100
                    ? response.Text![..100].Replace('\n', ' ') + "…"
                    : (response.Text ?? "").Replace('\n', ' '));

            // Append all new messages (tool calls, results, final reply) to shared history.
            // Ensure each assistant message carries the author name for routing strategies.
            foreach (var msg in response.Messages)
            {
                if (msg.Role == ChatRole.Assistant && string.IsNullOrEmpty(msg.AuthorName))
                    msg.AuthorName = agent.Name;
                history.Add(msg);

                logger.LogDebug(
                    "[Orchestrator]   → history[{Idx}] role={Role} author='{Author}' text='{Preview}'",
                    history.Count - 1, msg.Role.Value, msg.AuthorName ?? "(null)",
                    (msg.Text ?? "").Length > 60
                        ? msg.Text![..60].Replace('\n', ' ') + "…"
                        : (msg.Text ?? "(no text)").Replace('\n', ' '));
            }

            var agentMessage = new AgentMessage
            {
                AgentName = agent.Name ?? AgentNames.Unknown,
                Content   = response.Text ?? string.Empty,
                Role      = "assistant",
                TurnIndex = turn++,
                Usage     = OrchestratorHelpers.ExtractUsage(response),
                ToolCalls = ExtractToolCalls(response.Messages, agent.Name ?? AgentNames.Unknown)
            };

            eventEmitter?.SetTurn(agentMessage.TurnIndex);

            // Fulfill this agent's produced tokens now that its turn is complete.
            dependencyPlanner?.Fulfill(agent.Name ?? string.Empty);

            cumulativeTokens += agentMessage.Usage?.TotalTokens ?? 0;

            logger.LogDebug(
                "[{Agent}] Turn {Turn} — in:{InputTokens} out:{OutputTokens}: {Preview}",
                agentMessage.AgentName,
                agentMessage.TurnIndex,
                agentMessage.Usage?.InputTokens ?? 0,
                agentMessage.Usage?.OutputTokens ?? 0,
                StringHelpers.Truncate(agentMessage.Content, 200));

            var warnThreshold = config.WarnTurnTokens;
            if (warnThreshold > 0 && agentMessage.Usage?.InputTokens is { } inputToks && inputToks > warnThreshold)
                TokenBudgetWarning?.Invoke(agentMessage.AgentName, inputToks, warnThreshold);

            // Yield before checking the budget so the agent's response is always visible
            // in the transcript even when this turn pushed over the limit — the work was
            // done and the tokens were already consumed regardless.
            yield return agentMessage;

            if (config.MaxTotalTokens is { } limit && cumulativeTokens > limit)
                throw new BudgetExceededException(cumulativeTokens, limit);

            await PostTurnSideEffectsAsync(agentMessage, response, history, cancellationToken);

            // Periodic verifier: run the meta-agent every N turns to audit evidence, OR
            // immediately when a ConflictingEvidence / NoProgress correction was injected this
            // turn (evidence-driven trigger). Skipped when the verifier itself just ran.
            if (config.Verifier is { } verCfg
                && verifierAgent is not null
                && !string.Equals(agentMessage.AgentName, verCfg.AgentName, StringComparison.OrdinalIgnoreCase)
                && (
                    (verCfg.EveryNTurns > 0 && agentMessage.TurnIndex > 0 && agentMessage.TurnIndex % verCfg.EveryNTurns == 0)
                    || (verCfg.TriggerOnSuspiciousTransition && HasSuspiciousTransitionSignal(history, preSelectCount, postSelectCount))
                ))
            {
                var vAgentCfg = agentConfigs.GetValueOrDefault(verifierAgent.Name ?? "");
                var verifierMessage = await RunVerifierAsync(
                    verifierAgent, verCfg, history, task, vAgentCfg, agentInstructions, turn, cancellationToken);

                eventEmitter?.SetTurn(verifierMessage.TurnIndex);
                cumulativeTokens += verifierMessage.Usage?.TotalTokens ?? 0;
                turn++;

                var vWarnThreshold = config.WarnTurnTokens;
                if (vWarnThreshold > 0 && verifierMessage.Usage?.InputTokens is { } vInputToks && vInputToks > vWarnThreshold)
                    TokenBudgetWarning?.Invoke(verifierMessage.AgentName, vInputToks, vWarnThreshold);

                yield return verifierMessage;

                if (config.MaxTotalTokens is { } vLimit && cumulativeTokens > vLimit)
                    throw new BudgetExceededException(cumulativeTokens, vLimit);
            }

            // Check whether any termination condition has been satisfied.
            if (await termination.ShouldTerminateAsync(history, cancellationToken))
            {
                if (eventEmitter is not null)
                    _ = eventEmitter.EmitAsync(EventTypes.TerminationSatisfied,
                        agent: agentMessage.AgentName,
                        turn:  agentMessage.TurnIndex,
                        payload: new { turn });
                break;
            }
        }
    }

    // Helpers

    // Average tokens per tool schema definition — used to estimate the tool-schema overhead
    // that is counted in the LLM's input_tokens but absent from context_chars. Fuseraft tools
    // have detailed descriptions and multi-parameter schemas; 450 tokens/tool is a conservative
    // mid-point calibrated against observed grok/claude session data.
    private const int AvgToolSchemaTokens = 450;

    private static Task EmitContextAssemblyAsync(
        EventEmitter emitter,
        ContextAssemblyMetrics metrics,
        int turn,
        int toolCount = 0) =>
        emitter.EmitAsync(EventTypes.ContextAssembly,
            agent: metrics.AgentName,
            turn:  turn,
            payload: new
            {
                knowledge_retrieved      = metrics.KnowledgeItemsRetrieved,
                knowledge_included       = metrics.KnowledgeItemsIncluded,
                memory_loaded            = metrics.MemoryEntriesLoaded,
                memory_included          = metrics.MemoryEntriesIncluded,
                artifacts                = metrics.ArtifactsAssembled,
                context_chars            = metrics.TotalContextChars,
                system_prompt_chars      = metrics.SystemPromptChars,
                assembly_ms              = (int)metrics.AssemblyDuration.TotalMilliseconds,
                // Which path built this context, and — for Context: spec agents — which
                // declared sources resolved vs. which came back empty (missing artifact).
                context_strategy         = metrics.ContextStrategy,
                declared_sources         = metrics.DeclaredSources,
                empty_sources            = metrics.EmptySources,
                // Per-source char breakdown — shows which source dominates startup context.
                context_chars_breakdown  = new
                {
                    system_prompt    = metrics.SystemPromptChars,
                    memory           = metrics.MemoryChars,
                    session_context  = metrics.SessionContextChars,
                    knowledge        = metrics.KnowledgeChars,
                    history          = metrics.HistoryChars,
                    history_breakdown = new
                    {
                        msgs                   = metrics.HistoryMessageCount,
                        user                   = metrics.HistoryUserCount,
                        assistant              = metrics.HistoryAssistantCount,
                        tool                   = metrics.HistoryToolCount,
                        has_compaction_summary = metrics.HistoryHasCompactionSummary,
                    },
                },
                // Tool-schema tokens are sent as the API `tools` parameter, not as messages,
                // so they are invisible to context_chars. This estimate fills the gap so
                // total input_tokens ≈ context_chars/4 + tool_schema_est_tokens.
                tool_count               = toolCount,
                tool_schema_est_tokens   = toolCount * AvgToolSchemaTokens,
            });

    /// <summary>
    /// Recursively walks the termination strategy tree and calls
    /// <see cref="ValidatedTerminationStrategy.SetHistory"/> on each node that needs it.
    /// </summary>
    private static void WireHistory(ITerminationCondition condition, IList<ChatMessage> history)
    {
        if (condition is ValidatedTerminationStrategy vts)
            vts.SetHistory(history);

        if (condition is CompositeTerminationStrategy composite)
            foreach (var child in composite.Strategies)
                WireHistory(child, history);
    }

    /// <summary>
    /// Recursively walks the termination strategy tree and calls
    /// <see cref="ValidatedTerminationStrategy.SetSessionId"/> on each node that needs it.
    /// </summary>
    private static void WireSessionId(ITerminationCondition condition, string sessionId)
    {
        if (condition is ValidatedTerminationStrategy vts)
            vts.SetSessionId(sessionId);

        if (condition is CompositeTerminationStrategy composite)
            foreach (var child in composite.Strategies)
                WireSessionId(child, sessionId);
    }

    /// <summary>
    /// Recursively walks the termination strategy tree and calls
    /// <see cref="ValidatedTerminationStrategy.SetDidResolver"/> on each node that needs it.
    /// </summary>
    private static void WireDidResolver(ITerminationCondition condition, Func<string, string> resolver)
    {
        if (condition is ValidatedTerminationStrategy vts)
            vts.SetDidResolver(resolver);

        if (condition is CompositeTerminationStrategy composite)
            foreach (var child in composite.Strategies)
                WireDidResolver(child, resolver);
    }

    /// <summary>
    /// Recursively walks the termination strategy tree and calls
    /// <see cref="TokenBudgetTerminationCondition.SetTokenReader"/> on each node that needs it.
    /// </summary>
    private static void WireTokenBudget(ITerminationCondition condition, Func<int> tokenReader)
    {
        if (condition is TokenBudgetTerminationCondition tbc)
            tbc.SetTokenReader(tokenReader);

        if (condition is CompositeTerminationStrategy composite)
            foreach (var child in composite.Strategies)
                WireTokenBudget(child, tokenReader);

        // A tokenbudget node with its own Validators is wrapped in ValidatedTerminationStrategy
        // (see StrategyFactory.CreateTermination) — unwrap it to reach the decorated condition.
        if (condition is ValidatedTerminationStrategy vts)
            WireTokenBudget(vts.Inner, tokenReader);
    }

    private IReadOnlyList<ToolCallRecord>? ExtractToolCalls(IList<ChatMessage> messages, string agentName = AgentNames.Unknown)
        => OrchestratorHelpers.ExtractToolCalls(messages, logger, agentName);

    // Scans messages at indices [from, to) for ConflictingEvidence or NoProgress correction
    // signals injected by the selection strategy. Returns true when any such signal is found,
    // indicating the verifier should audit the current turn's output.
    private static bool HasSuspiciousTransitionSignal(IList<ChatMessage> history, int from, int to)
    {
        for (int i = from; i < to && i < history.Count; i++)
        {
            var msg = history[i];
            if (msg.Role != ChatRole.User) continue;
            var text = msg.Text ?? string.Empty;
            if (text.StartsWith("NO TOOL CALLS",         StringComparison.Ordinal) ||
                text.StartsWith("CRITICAL:",              StringComparison.Ordinal) ||
                text.Contains("EVIDENCE INCONSISTENCY",  StringComparison.Ordinal) ||
                text.Contains("EVIDENCE AUDIT REQUIRED", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // Estimates the input token cost of a context slice by summing all content chars across
    // message types. Used for the pre-turn budget guard; TokenEstimator's default ratio is
    // intentionally conservative (actual tokenisation may differ but is rarely smaller).
    private static int EstimateContextTokens(IEnumerable<ChatMessage> messages)
    {
        int chars = 0;
        foreach (var msg in messages)
            foreach (var content in msg.Contents)
                chars += content switch
                {
                    TextContent tc        => tc.Text?.Length ?? 0,
                    FunctionCallContent fc => (fc.Name?.Length ?? 0) +
                        (fc.Arguments?.Values.Sum(v => v?.ToString()?.Length ?? 0) ?? 0),
                    FunctionResultContent fr => fr.Result?.ToString()?.Length ?? 0,
                    _ => 0,
                };
        return TokenEstimator.EstimateTokens(chars);
    }

    // Assembles the trimmed context list for a single sequential agent turn (or verifier turn).
    // Handles the unified pipeline path and the full legacy fallback (instructions + memory +
    // context-source assembly + session-context injection + history filtering).
    // Tool-result trimming is applied before returning so callers receive an invocation-ready slice.
    private async Task<IList<ChatMessage>> BuildContextAsync(
        string agentName,
        string task,
        IList<ChatMessage> history,
        AgentConfig? agentCfg,
        IReadOnlyDictionary<string, string> agentInstructions,
        int turn,
        CancellationToken cancellationToken)
    {
        IEnumerable<ChatMessage> context;

        if (contextPipeline is not null)
        {
            var assembled = await contextPipeline.AssembleAsync(
                new AgentExecutionRequest
                {
                    AgentName     = agentName,
                    Task          = task,
                    SharedHistory = (IReadOnlyList<ChatMessage>)history,
                    AgentConfig   = agentCfg,
                    SessionId     = _sessionId,
                },
                cancellationToken);
            context = assembled.Messages;
            if (eventEmitter is not null)
                await EmitContextAssemblyAsync(eventEmitter, assembled.Metrics, turn,
                    agentFactory.GetToolCount(agentName));
        }
        else
        {
            bool hasInstructions = agentInstructions.TryGetValue(agentName, out var instructions);
            if (memoryManager is not null)
                instructions = await memoryManager.AugmentInstructionsAsync(agentName, instructions, cancellationToken);

            var isolation = agentCfg?.Isolation ?? AgentIsolation.Fresh;
            var directive = isolation is AgentIsolation.Fresh or AgentIsolation.Fork
                ? OrchestratorHelpers.FindLastDirective((IReadOnlyList<ChatMessage>)history)
                : null;

            IReadOnlyList<ChatMessage> filtered;
            if (isolation == AgentIsolation.Fresh && contextAssembler is not null)
            {
                filtered = (await contextAssembler.AssembleForAgentAsync(
                    agentName, task, (IReadOnlyList<ContextSource>?)agentCfg?.Context ?? [],
                    history, directive, cancellationToken)).Messages;
            }
            else if (isolation == AgentIsolation.Fresh)
            {
                // No assembler configured — degrade to the directive/task alone rather than
                // falling back to the shared transcript.
                filtered = [new ChatMessage(ChatRole.User, directive?.Format() ?? task)];
            }
            else if (agentCfg?.Context is { Count: > 0 } agentContextSources && contextAssembler is not null)
            {
                filtered = (await contextAssembler.AssembleForAgentAsync(
                    agentName, task, agentContextSources, history,
                    isolation == AgentIsolation.Fork ? directive : null, cancellationToken)).Messages;
            }
            else
            {
                var raw = ContextWindowFilter.Apply(history, agentCfg?.ContextWindow);
                if (contextAssembler is not null)
                {
                    var sessionCtx = await contextAssembler.ReadSessionContextAsync(cancellationToken);
                    if (sessionCtx is not null)
                    {
                        var withCtx = new List<ChatMessage>(raw.Count + 1);
                        if (raw.Count > 0) withCtx.Add(raw[0]);
                        withCtx.Add(new ChatMessage(ChatRole.User, $"[Session Context]\n\n{sessionCtx.Trim()}"));
                        withCtx.AddRange(raw.Skip(1));
                        filtered = withCtx;
                    }
                    else filtered = raw;
                }
                else filtered = raw;

                // Fork: layer the synthesized directive on top of the full shared transcript,
                // matching ContextAssemblyPipeline.AssembleAsync's equivalent branch.
                if (isolation == AgentIsolation.Fork && directive is not null)
                    filtered = [.. filtered, new ChatMessage(ChatRole.User, directive.Format())];
            }

            context = (hasInstructions || memoryManager is not null) && instructions is not null
                ? [new ChatMessage(ChatRole.System, instructions), .. filtered]
                : filtered;
        }

        var contextList = context as IList<ChatMessage> ?? context.ToList();
        if (config.ContextBudget is { MaxToolResultTokens: > 0 } toolBudget)
        {
            var (trimmed, manifest) = ToolResultWindowTrimmer.ApplyWithManifest(contextList, toolBudget);
            if (manifest is not null)
            {
                var withManifest = new List<ChatMessage>(trimmed)
                {
                    new ChatMessage(ChatRole.User, manifest)
                };
                return withManifest;
            }
            return trimmed;
        }
        return contextList;
    }

    // Runs all post-yield side effects for a completed sequential agent turn:
    // turn_end and reasoning telemetry, change-tracker flush, memory persistence,
    // and repository knowledge store observations. Never throws — knowledge/change-tracker
    // failures are logged and swallowed so session output is never disrupted.
    private async Task PostTurnSideEffectsAsync(
        AgentMessage msg,
        AgentResponse response,
        IList<ChatMessage> history,
        CancellationToken cancellationToken)
    {
        if (eventEmitter is not null)
        {
            await eventEmitter.EmitAsync(EventTypes.TurnEnd,
                agent: msg.AgentName,
                turn:  msg.TurnIndex,
                payload: new
                {
                    input_tokens  = msg.Usage?.InputTokens,
                    output_tokens = msg.Usage?.OutputTokens,
                });

            await eventEmitter.EmitAsync(EventTypes.AgentEnd,
                agent: msg.AgentName,
                turn:  msg.TurnIndex,
                payload: new
                {
                    input_tokens  = msg.Usage?.InputTokens,
                    output_tokens = msg.Usage?.OutputTokens,
                });

            // Emit reasoning content when the model produced any (e.g. xAI reasoning models).
            // Capped at 8 000 chars to keep events.jsonl compact for long reasoning traces.
            const int MaxReasoningChars = 8_000;
            var reasoningText = string.Concat(
                response.Messages
                    .SelectMany(m => m.Contents.OfType<TextReasoningContent>())
                    .Select(r => r.Text));
            if (!string.IsNullOrWhiteSpace(reasoningText))
            {
                var truncated = reasoningText.Length > MaxReasoningChars
                    ? reasoningText[..MaxReasoningChars] + $"\n[TRUNCATED — {reasoningText.Length:N0} chars total]"
                    : reasoningText;
                await eventEmitter.EmitAsync(EventTypes.Reasoning,
                    agent:   msg.AgentName,
                    turn:    msg.TurnIndex,
                    payload: new { text = truncated });
            }
        }

        if (changeTracker is not null)
        {
            try { await changeTracker.FlushTurnAsync(msg.AgentName, msg.TurnIndex, CancellationToken.None); }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "ChangeTracker flush failed for turn {Turn} ({Agent}) — changes.json may be incomplete.",
                    msg.TurnIndex, msg.AgentName);
            }
        }

        if (memoryManager is not null)
            await memoryManager.PostTurnAsync(msg.AgentName, [..history], cancellationToken);

        if (repositoryKnowledgeStore is not null && !string.IsNullOrEmpty(_sessionId))
        {
            try
            {
                var observations = ObservationExtractor.Extract(
                    (IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>)response.Messages,
                    msg.AgentName, msg.TurnIndex);
                foreach (var obs in observations)
                {
                    if (string.IsNullOrWhiteSpace(obs.Entity)) continue;
                    await repositoryKnowledgeStore.AddAsync(new RepositoryKnowledgeFinding
                    {
                        Entity     = obs.Entity!,
                        Finding    = obs.Finding,
                        Source     = _sessionId,
                        Confidence = obs.Confidence,
                        AgentName  = obs.AgentName,
                        Kind       = obs.Source is "write_file" or "patch_file" or "delete_file"
                                     ? "change" : "observation",
                    }, CancellationToken.None);
                }
            }
            catch { /* best-effort — never disrupt the session */ }
        }
    }

    // Executes a single verifier turn: fires lifecycle hooks, assembles context via BuildContextAsync,
    // invokes the agent, appends messages to shared history, and injects a finding correction message
    // when the verifier reports an issue. Returns the AgentMessage with TurnIndex = currentTurn;
    // the caller is responsible for incrementing the turn counter after yielding.
    private async Task<AgentMessage> RunVerifierAsync(
        AIAgent verifierAgent,
        VerifierConfig verCfg,
        IList<ChatMessage> history,
        string task,
        AgentConfig? verifierAgentCfg,
        IReadOnlyDictionary<string, string> agentInstructions,
        int currentTurn,
        CancellationToken cancellationToken)
    {
        AgentStarting?.Invoke(verifierAgent.Name ?? "Verifier");
        agentFactory.OnAgentTurnStarting();
        changeTracker?.BeginTurn(verifierAgent.Name ?? "Verifier", currentTurn);

        var vContextList = await BuildContextAsync(
            verifierAgent.Name ?? string.Empty, task, history, verifierAgentCfg,
            agentInstructions, currentTurn, cancellationToken);

        AgentResponse vResponse = governanceKernel?.CircuitBreaker is { } vcb
            ? await vcb.ExecuteAsync(() => verifierAgent.RunAsync(vContextList, null, null, cancellationToken))
            : await verifierAgent.RunAsync(vContextList, null, null, cancellationToken);

        foreach (var vMsg in vResponse.Messages)
        {
            if (vMsg.Role == ChatRole.Assistant && string.IsNullOrEmpty(vMsg.AuthorName))
                vMsg.AuthorName = verifierAgent.Name;
            history.Add(vMsg);
        }

        // When the verifier reports a finding, inject an explicit correction message
        // so the next primary agent turn has the finding as visible context.
        if (vResponse.Text?.Contains(verCfg.FindingsKeyword, StringComparison.OrdinalIgnoreCase) == true)
        {
            history.Add(new ChatMessage(ChatRole.User,
                $"VERIFICATION FINDING [{verifierAgent.Name}]: An inconsistency was detected. " +
                $"Review the verifier's output and reconcile any discrepancies before continuing:\n\n" +
                vResponse.Text));
        }

        return new AgentMessage
        {
            AgentName = verifierAgent.Name ?? AgentNames.Verifier,
            Content   = vResponse.Text ?? string.Empty,
            Role      = "assistant",
            TurnIndex = currentTurn,
            Usage     = OrchestratorHelpers.ExtractUsage(vResponse),
            ToolCalls = ExtractToolCalls(vResponse.Messages, verifierAgent.Name ?? AgentNames.Verifier)
        };
    }
}
