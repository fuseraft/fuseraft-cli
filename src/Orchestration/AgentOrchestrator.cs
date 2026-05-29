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
    fuseraft.Infrastructure.MemoryManager? memoryManager = null) : IOrchestrator
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
    public void SetSessionId(string sessionId) => _sessionId = sessionId;

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

    /// <inheritdoc/>
    public void SetResumeStateName(string? stateName) => _resumeStateName = stateName;


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
            .Select(a => agentFactory.Create(a, onToolCalling: (agent, tool, args) => ToolCalling?.Invoke(agent, tool, args)))
            .ToList();
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

            // Restore state after compaction so the machine resumes from e.g. "Testing"
            // rather than resetting to its initial state ("Planning").
            var stateName = _resumeStateName;
            _resumeStateName = null; // consume before applying — prevents re-application if SetCurrentState throws
            if (!string.IsNullOrWhiteSpace(stateName))
                smss.SetCurrentState(stateName);
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

                        bool hasInstr = agentInstructions.TryGetValue(branchAgent.Name ?? "", out var instr);
                        if (memoryManager is not null)
                            instr = await memoryManager.AugmentInstructionsAsync(branchAgent.Name ?? "", instr, cancellationToken);

                        var bAgentCfg = agentConfigs.GetValueOrDefault(branchAgent.Name ?? "");
                        var filtered  = ContextWindowFilter.Apply(snapshot, bAgentCfg?.ContextWindow);
                        IEnumerable<ChatMessage> context = (hasInstr || memoryManager is not null) && instr is not null
                            ? [new ChatMessage(ChatRole.System, instr), .. filtered]
                            : filtered;

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
                            Usage     = ExtractUsage(branchResponse),
                            ToolCalls = ExtractToolCalls(branchResponse.Messages),
                        };

                        cumulativeTokens += branchMsg.Usage?.TotalTokens ?? 0;

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
            var agent = await selection.SelectAsync(agents, history, cancellationToken);
            if (agent is null) break;

            logger.LogDebug(
                "[Orchestrator] Turn {Turn}: selected agent '{Agent}' (Name property='{NameProp}') | history={HistCount} msgs",
                turn, agent.Name, agent.Name, history.Count);

            AgentStarting?.Invoke(agent.Name ?? "Unknown");
            agentFactory.OnAgentTurnStarting();
            changeTracker?.BeginTurn(agent.Name ?? "Unknown", turn);

            // Run the selected agent against the (possibly filtered) shared history.
            // Passing null session means the agent does not maintain internal state —
            // the full history IS the context for every call.
            // Prepend this agent's system instruction so the LLM knows its role and routing keywords.
            bool hasInstructions = agentInstructions.TryGetValue(agent.Name ?? "", out var instructions);

            // Augment system instructions with the memory block for this agent (if any).
            if (memoryManager is not null)
                instructions = await memoryManager.AugmentInstructionsAsync(agent.Name ?? "", instructions, cancellationToken);

            // Apply the agent's ContextWindow filter before building the context slice.
            // This lets downstream agents (e.g. Reviewer) strip tool-call noise accumulated
            // by earlier agents, dramatically reducing input-token count without changing the
            // shared history that routing/termination strategies read.
            var agentCfg = agentConfigs.GetValueOrDefault(agent.Name ?? "");
            var filtered = ContextWindowFilter.Apply(history, agentCfg?.ContextWindow);

            IEnumerable<ChatMessage> context = (hasInstructions || memoryManager is not null) && instructions is not null
                ? [new ChatMessage(ChatRole.System, instructions), .. filtered]
                : filtered;

            logger.LogDebug(
                "[Orchestrator] Invoking '{Agent}' with {ContextCount} context messages " +
                "(system={HasSystem}, history={HistCount}, filtered={FilteredCount})",
                agent.Name,
                hasInstructions ? filtered.Count + 1 : filtered.Count,
                hasInstructions,
                history.Count,
                filtered.Count);

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
                Usage     = ExtractUsage(response),
                ToolCalls = ExtractToolCalls(response.Messages)
            };

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

            // Periodic verifier: run the meta-agent every N turns to audit evidence.
            // Skipped when the verifier itself just ran to prevent self-loops.
            if (config.Verifier is { EveryNTurns: > 0 } verCfg
                && verifierAgent is not null
                && agentMessage.TurnIndex > 0
                && agentMessage.TurnIndex % verCfg.EveryNTurns == 0
                && !string.Equals(agentMessage.AgentName, verCfg.AgentName, StringComparison.OrdinalIgnoreCase))
            {
                AgentStarting?.Invoke(verifierAgent.Name ?? "Verifier");
                agentFactory.OnAgentTurnStarting();
                changeTracker?.BeginTurn(verifierAgent.Name ?? "Verifier", turn);

                var vAgentCfg = agentConfigs.GetValueOrDefault(verifierAgent.Name ?? "");
                var vFiltered = ContextWindowFilter.Apply(history, vAgentCfg?.ContextWindow);
                bool vHasInstr = agentInstructions.TryGetValue(verifierAgent.Name ?? "", out var vInstr);
                if (memoryManager is not null)
                    vInstr = await memoryManager.AugmentInstructionsAsync(verifierAgent.Name ?? "", vInstr, cancellationToken);
                IEnumerable<ChatMessage> vContext = (vHasInstr || memoryManager is not null) && vInstr is not null
                    ? [new ChatMessage(ChatRole.System, vInstr), .. vFiltered]
                    : vFiltered;

                AgentResponse vResponse = governanceKernel?.CircuitBreaker is { } vcb
                    ? await vcb.ExecuteAsync(() => verifierAgent.RunAsync(vContext, null, null, cancellationToken))
                    : await verifierAgent.RunAsync(vContext, null, null, cancellationToken);

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
                    Usage     = ExtractUsage(vResponse),
                    ToolCalls = ExtractToolCalls(vResponse.Messages)
                };

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

    private static TokenUsage? ExtractUsage(AgentResponse response)
    {
        if (response.Usage is null) return null;

        var inputTokens  = (int)(response.Usage.InputTokenCount  ?? 0L);
        var outputTokens = (int)(response.Usage.OutputTokenCount ?? 0L);

        if (inputTokens == 0 && outputTokens == 0) return null;

        return new TokenUsage(inputTokens, outputTokens);
    }

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
    /// Scans the raw response messages for function call / result pairs and returns a
    /// slim summary list suitable for terminal display. Fails gracefully on any parse error.
    /// </summary>
    private static IReadOnlyList<ToolCallRecord>? ExtractToolCalls(IList<ChatMessage> messages)
    {
        var calls   = new List<(string CallId, string Name, string? ArgsSummary)>();
        var results = new Dictionary<string, bool>(StringComparer.Ordinal); // callId → succeeded

        try
        {
            foreach (var msg in messages)
            {
                foreach (var content in msg.Contents)
                {
                    if (content is FunctionCallContent fc)
                    {
                        calls.Add((fc.CallId ?? fc.Name, fc.Name, ToolCallHelper.SummarizeArgs(fc.Arguments)));
                    }
                    else if (content is FunctionResultContent fr)
                    {
                        var key     = fr.CallId ?? string.Empty;
                        var text    = fr.Result?.ToString() ?? string.Empty;
                        var success = !text.StartsWith("[ERROR]",     StringComparison.Ordinal)
                                   && !text.StartsWith("[DENIED]",    StringComparison.Ordinal)
                                   && !text.StartsWith("[TIMEOUT]",   StringComparison.Ordinal)
                                   && !text.StartsWith("[NOT FOUND]", StringComparison.Ordinal)
                                   && !text.StartsWith("[EXIT ",      StringComparison.Ordinal);
                        if (!string.IsNullOrEmpty(key))
                            results[key] = success;
                    }
                }
            }
        }
        catch (Exception) { /* best-effort — return null on any parse error */ }

        if (calls.Count == 0) return null;

        return calls
            .Select(c => new ToolCallRecord(
                c.Name,
                c.ArgsSummary,
                results.TryGetValue(c.CallId, out var ok) ? ok : true))
            .ToList();
    }

    private static string GenerateSessionId() => Guid.NewGuid().ToString("N")[..8];
}
