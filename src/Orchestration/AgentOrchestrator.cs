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
using AgentFactory = fuseraft.Infrastructure.AgentFactory;

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
    fuseraft.Infrastructure.MemoryManager? memoryManager = null,
    ContextAssembler? contextAssembler = null,
    DependencyPlanner? dependencyPlanner = null,
    fuseraft.Core.Interfaces.IContextAssemblyPipeline? contextPipeline = null,
    fuseraft.Infrastructure.RepositoryKnowledgeStore? repositoryKnowledgeStore = null) : IOrchestrator
{
    // IOrchestrator

    public async Task<OrchestrationResult> RunAsync(
        string task,
        IReadOnlyList<AgentMessage>? priorHistory = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = GenerateSessionId();
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

    // Mutable reference to the live shared history for the current StreamAsync invocation.
    // Updated at the start of each call so session-scoped hooks registered once at
    // initialization always target the current session's history, not a stale one.
    // volatile: the hook callback closure reads this field on whatever thread the emitter
    // fires on; the assignment in StreamAsync must be visible immediately.
    private volatile IList<ChatMessage>? _activeHistory;

    /// <summary>
    /// The active selection strategy cast to <see cref="IContextSnapshotter"/>, or null
    /// when the current strategy does not support snapshotting (e.g. keyword or LLM strategy).
    /// Updated at the start of each <see cref="StreamAsync"/> call.
    /// </summary>
    public fuseraft.Core.Interfaces.IContextSnapshotter? CurrentSnapshotter { get; private set; }

    // Guards single hook registration across multiple StreamAsync calls on the same instance.
    // volatile: the check-then-set happens across async boundaries; the flag only ever
    // transitions false → true so no CAS is needed, but the write must be visible to
    // future async continuations on any thread.
    private volatile bool _diagnosticHookRegistered;

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

    private fuseraft.Core.Models.TaskModel? _structuredTask;

    /// <summary>
    /// Sets the structured task model injected into history at session start.
    /// Call before <see cref="StreamAsync"/> to provide goal, constraints, and active targets.
    /// When null (default), no task model block is injected.
    /// </summary>
    public void SetStructuredTask(fuseraft.Core.Models.TaskModel? model) => _structuredTask = model;

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

        // Build fresh agents and strategies per session to avoid state bleed.
        var agents = config.Agents
            .Select(a => agentFactory.Create(a, config.ContextBudget, onToolCalling: (agent, tool, args) => ToolCalling?.Invoke(agent, tool, args)))
            .ToList();
        if (!string.IsNullOrEmpty(_sessionId))
            strategyFactory.SetSessionId(_sessionId);
        var selection = strategyFactory.CreateSelection(config.Selection, agents, config.Validation, config.FailureHandling, config.Contracts, config.Verifier);
        CurrentSnapshotter = selection as fuseraft.Core.Interfaces.IContextSnapshotter;
        var termination = strategyFactory.CreateTermination(config.Termination ?? new(), agents, config.Validation);

        // Resolve the optional verifier agent once per session.
        AIAgent? verifierAgent = config.Verifier?.AgentName is { Length: > 0 } verifierName
            ? agents.FirstOrDefault(a => string.Equals(a.Name, verifierName, StringComparison.OrdinalIgnoreCase))
            : null;

        // Shared history — all agents read from and write to this list.
        var history = new List<ChatMessage>();

        // Point the active-history cell at this session's list. Any hooks registered
        // below via _activeHistory will automatically target the current invocation.
        _activeHistory = history;

        // Register the validation diagnostic hook once per orchestrator instance.
        // The hook watches for validation_fail events and injects change-log context
        // into history on repeated failures so the re-invoked agent has ground-truth
        // data rather than only the validator's error message.
        if (!_diagnosticHookRegistered && eventEmitter is not null && config.ChangeTracking is { } ctCfg)
        {
            _diagnosticHookRegistered = true;
            eventEmitter.RegisterHook(
                new ValidationDiagnosticHook(ctCfg.Path, msg => _activeHistory?.Add(msg)));
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
            var stateName = _resumeStateName;
            _resumeStateName = null; // consume before applying — prevents re-application if SetCurrentState throws
            if (!string.IsNullOrWhiteSpace(stateName))
                smss.SetCurrentState(stateName);

            // Restore failure-tracking counters so MaxConsecutiveContractFailures and
            // the REPLAN BLOCKED guard survive across compaction cycles.
            var snap = _resumeSnapshot;
            _resumeSnapshot = null; // consume once, same discipline as _resumeStateName
            smss.RestoreFromSnapshot(snap);
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
            logger.LogInformation("Resuming session... replaying {Turns} prior turns.", priorHistory.Count);

            foreach (var prior in priorHistory)
            {
                var role    = prior.Role == "user" ? ChatRole.User : ChatRole.Assistant;
                var content = ContextWindowFilter.TruncateReplayContent(prior);
                var msg     = new ChatMessage(role, content);
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

        while (true)
        {
            // Hard iteration cap — takes effect regardless of the termination strategy.
            if (config.Termination?.ResolveMaxIterations() is > 0 and var maxIter && turn >= maxIter)
                break;

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

                        AgentResponse response = governanceKernel?.CircuitBreaker is { } cb
                            ? await cb.ExecuteAsync(() => branchAgent.RunAsync(context, null, null, cancellationToken))
                            : await branchAgent.RunAsync(context, null, null, cancellationToken);

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
                            AgentName = branchAgent.Name ?? "Unknown",
                            Content   = branchResponse.Text ?? string.Empty,
                            Role      = "assistant",
                            TurnIndex = turn++,
                            Usage     = OrchestratorHelpers.ExtractUsage(branchResponse),
                            ToolCalls = ExtractToolCalls(branchResponse.Messages, branchAgent.Name ?? "Unknown"),
                        };

                        cumulativeTokens += branchMsg.Usage?.TotalTokens ?? 0;
                        eventEmitter?.SetTurn(branchMsg.TurnIndex);

                        if (eventEmitter is not null)
                            await eventEmitter.EmitAsync("turn_end",
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

            // Build the full context for this agent through the unified assembly pipeline.
            // The pipeline handles: system prompt (instructions + ranked memory),
            // intent-based knowledge retrieval, session context injection, history
            // filtering, and artifact assembly — all through one code path for both
            // sequential and parallel execution.
            var agentCfg = agentConfigs.GetValueOrDefault(agent.Name ?? "");
            IEnumerable<ChatMessage> context;

            if (contextPipeline is not null)
            {
                var assembled = await contextPipeline.AssembleAsync(
                    new AgentExecutionRequest
                    {
                        AgentName     = agent.Name ?? string.Empty,
                        Task          = task,
                        SharedHistory = history,
                        AgentConfig   = agentCfg,
                        SessionId     = _sessionId,
                    },
                    cancellationToken);
                context = assembled.Messages;
                if (eventEmitter is not null)
                    await EmitContextAssemblyAsync(eventEmitter, assembled.Metrics, turn,
                        agentFactory.GetToolCount(agent.Name ?? ""));
            }
            else
            {
                // Legacy fallback — identical to the pre-pipeline behavior.
                bool hasInstructions = agentInstructions.TryGetValue(agent.Name ?? "", out var instructions);
                if (memoryManager is not null)
                    instructions = await memoryManager.AugmentInstructionsAsync(agent.Name ?? "", instructions, cancellationToken);

                IReadOnlyList<ChatMessage> filtered;
                if (agentCfg?.Context is { Count: > 0 } agentContextSources && contextAssembler is not null)
                {
                    filtered = await contextAssembler.AssembleForAgentAsync(
                        agent.Name ?? string.Empty, task, agentContextSources, history, cancellationToken);
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
                }

                context = (hasInstructions || memoryManager is not null) && instructions is not null
                    ? [new ChatMessage(ChatRole.System, instructions), .. filtered]
                    : filtered;
            }

            var contextList = context as IList<ChatMessage> ?? context.ToList();

            // Sliding tool-result window: replace oldest tool results with tombstones
            // when the estimated token cost exceeds MaxToolResultTokens. Applied to the
            // context slice only — shared history is never modified.
            if (config.ContextBudget is { MaxToolResultTokens: > 0 } toolBudget)
                contextList = ToolResultWindowTrimmer.Apply(contextList, toolBudget);

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
                var estimatedInputTokens = EstimateContextTokens(context);
                if (cumulativeTokens + estimatedInputTokens > preTurnLimit)
                {
                    logger.LogWarning(
                        "[Orchestrator] Pre-turn budget guard: cumulative {Cumulative:N0} + estimated input {Estimated:N0} > limit {Limit:N0} — aborting before turn.",
                        cumulativeTokens, estimatedInputTokens, preTurnLimit);
                    throw new BudgetExceededException(cumulativeTokens + estimatedInputTokens, preTurnLimit);
                }
            }

            AgentResponse response = governanceKernel?.CircuitBreaker is { } cb
                ? await cb.ExecuteAsync(() => agent.RunAsync(context, null, null, cancellationToken))
                : await agent.RunAsync(context, null, null, cancellationToken);

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
                AgentName = agent.Name ?? "Unknown",
                Content   = response.Text ?? string.Empty,
                Role      = "assistant",
                TurnIndex = turn++,
                Usage     = OrchestratorHelpers.ExtractUsage(response),
                ToolCalls = ExtractToolCalls(response.Messages, agent.Name ?? "Unknown")
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

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync("turn_end",
                    agent: agentMessage.AgentName,
                    turn:  agentMessage.TurnIndex,
                    payload: new
                    {
                        input_tokens  = agentMessage.Usage?.InputTokens,
                        output_tokens = agentMessage.Usage?.OutputTokens,
                    });

            // Emit reasoning content when the model produced any (e.g. xAI reasoning models).
            // Capped at 8 000 chars to keep events.jsonl compact for long reasoning traces.
            if (eventEmitter is not null)
            {
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
                    await eventEmitter.EmitAsync("reasoning",
                        agent:   agentMessage.AgentName,
                        turn:    agentMessage.TurnIndex,
                        payload: new { text = truncated });
                }
            }

            // Flush change-tracking middleware queue for this turn to disk.
            if (changeTracker is not null)
            {
                try { await changeTracker.FlushTurnAsync(agentMessage.AgentName, agentMessage.TurnIndex, CancellationToken.None); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "ChangeTracker flush failed for turn {Turn} ({Agent}) — changes.json may be incomplete.",
                        agentMessage.TurnIndex, agentMessage.AgentName);
                }
            }

            // Offer the accumulated history to the memory provider for persistence.
            if (memoryManager is not null)
                await memoryManager.PostTurnAsync(agentMessage.AgentName, [..history], cancellationToken);

            // Persist entity-scoped findings from this turn's tool calls for future session retrieval.
            if (repositoryKnowledgeStore is not null && !string.IsNullOrEmpty(_sessionId))
            {
                try
                {
                    var observations = ObservationExtractor.Extract(
                        (IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>)response.Messages,
                        agentMessage.AgentName, agentMessage.TurnIndex);
                    foreach (var obs in observations)
                    {
                        if (string.IsNullOrWhiteSpace(obs.Entity)) continue;
                        await repositoryKnowledgeStore.AddAsync(new fuseraft.Core.Models.RepositoryKnowledgeFinding
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
                AgentStarting?.Invoke(verifierAgent.Name ?? "Verifier");
                agentFactory.OnAgentTurnStarting();
                changeTracker?.BeginTurn(verifierAgent.Name ?? "Verifier", turn);

                var vAgentCfg = agentConfigs.GetValueOrDefault(verifierAgent.Name ?? "");
                IEnumerable<ChatMessage> vContext;
                if (contextPipeline is not null)
                {
                    var vAssembled = await contextPipeline.AssembleAsync(
                        new AgentExecutionRequest
                        {
                            AgentName     = verifierAgent.Name ?? string.Empty,
                            Task          = task,
                            SharedHistory = history,
                            AgentConfig   = vAgentCfg,
                            SessionId     = _sessionId,
                        },
                        cancellationToken);
                    vContext = vAssembled.Messages;
                    if (eventEmitter is not null)
                        await EmitContextAssemblyAsync(eventEmitter, vAssembled.Metrics, turn,
                            agentFactory.GetToolCount(verifierAgent.Name ?? ""));
                }
                else
                {
                    var vFiltered  = ContextWindowFilter.Apply(history, vAgentCfg?.ContextWindow);
                    bool vHasInstr = agentInstructions.TryGetValue(verifierAgent.Name ?? "", out var vInstr);
                    if (memoryManager is not null)
                        vInstr = await memoryManager.AugmentInstructionsAsync(verifierAgent.Name ?? "", vInstr, cancellationToken);
                    vContext = (vHasInstr || memoryManager is not null) && vInstr is not null
                        ? [new ChatMessage(ChatRole.System, vInstr), .. vFiltered]
                        : vFiltered;
                }

                var vContextList = vContext as IList<ChatMessage> ?? vContext.ToList();
                if (config.ContextBudget is { MaxToolResultTokens: > 0 } vToolBudget)
                    vContextList = ToolResultWindowTrimmer.Apply(vContextList, vToolBudget);

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

                var verifierMessage = new AgentMessage
                {
                    AgentName = verifierAgent.Name ?? "Verifier",
                    Content   = vResponse.Text ?? string.Empty,
                    Role      = "assistant",
                    TurnIndex = turn++,
                    Usage     = OrchestratorHelpers.ExtractUsage(vResponse),
                    ToolCalls = ExtractToolCalls(vResponse.Messages, verifierAgent.Name ?? "Verifier")
                };

                eventEmitter?.SetTurn(verifierMessage.TurnIndex);
                cumulativeTokens += verifierMessage.Usage?.TotalTokens ?? 0;

                var vWarnThreshold = config.WarnTurnTokens;
                if (vWarnThreshold > 0 && verifierMessage.Usage?.InputTokens is { } vInputToks && vInputToks > vWarnThreshold)
                    TokenBudgetWarning?.Invoke(verifierMessage.AgentName, vInputToks, vWarnThreshold);

                yield return verifierMessage;

                if (config.MaxTotalTokens is { } vLimit && cumulativeTokens > vLimit)
                    throw new BudgetExceededException(cumulativeTokens, vLimit);
            }

            // Check whether any termination condition has been satisfied.
            if (await termination.ShouldTerminateAsync(history, cancellationToken))
                break;
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
        fuseraft.Core.Models.ContextAssemblyMetrics metrics,
        int turn,
        int toolCount = 0) =>
        emitter.EmitAsync("context_assembly",
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

    private IReadOnlyList<ToolCallRecord>? ExtractToolCalls(IList<ChatMessage> messages, string agentName = "Unknown")
        => OrchestratorHelpers.ExtractToolCalls(messages, logger, agentName);

    private static string GenerateSessionId() => Guid.NewGuid().ToString("N")[..8];

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
    // message types and dividing by 4. Used for the pre-turn budget guard; intentionally
    // conservative (actual tokenisation may differ but is rarely smaller than chars/4).
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
        return chars / 4;
    }
}
