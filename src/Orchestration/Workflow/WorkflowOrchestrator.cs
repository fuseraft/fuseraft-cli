using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using AgentGovernance;
using AgentGovernance.Audit;
using AgentGovernance.Sre;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using MafWorkflow = Microsoft.Agents.AI.Workflows.Workflow;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
using fuseraft.Core.Exceptions;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration.Strategies;
using fuseraft.Orchestration.Validation;

// Disambiguate from Microsoft.Agents.AI.AgentFactory
using fuseraft.Infrastructure;
using AgentFactory = fuseraft.Infrastructure.AgentFactory;

namespace fuseraft.Orchestration.Workflow;

/// <summary>
/// Event-driven multi-agent orchestrator built on the MAF <c>WorkflowBuilder</c> +
/// <c>InProcessExecution.RunStreamingAsync</c> + <c>WatchStreamAsync</c> pattern.
///
/// <para>
/// Execution is phase-based: each phase is a DAG workflow (Planner → Developer →
/// Tester → Reviewer). When an agent emits a cycle-triggering keyword (BUGS FOUND,
/// REVISION REQUIRED, REPLAN REQUIRED), the current phase terminates and the outer
/// loop restarts a new phase from the appropriate executor.  APPROVED terminates
/// the session.
/// </para>
///
/// <para>
/// Agent outputs are written to an unbounded <see cref="Channel{T}"/> from inside
/// each executor and yielded to the caller via the <see cref="IAsyncEnumerable{T}"/>
/// returned by <see cref="StreamAsync"/>.  This decouples the executor threads from
/// the caller and avoids blocking the MAF event loop.
/// </para>
/// </summary>
public sealed class WorkflowOrchestrator(
    OrchestrationConfig config,
    AgentFactory agentFactory,
    ILogger<WorkflowOrchestrator> logger,
    ChangeTracker? changeTracker = null,
    EventEmitter? eventEmitter = null,
    GovernanceKernel? governanceKernel = null) : IOrchestrator
{
    // How many retries an executor attempts before throwing ValidatorStuckException.
    // internal so CorrectionEngine can reference it in retry-count messages.
    internal const int MaxRetries = 4;

    // Canonical executor order for workflow phases. Used by BuildPhaseWorkflow to build
    // the DAG and by DetermineStartExecutorId to validate resume hints.
    private static readonly string[] ExecutorChain = ["planner", "developer", "tester", "reviewer"];

    // Accumulates AgentState snapshots as state advances across agent handoffs.
    // Seeded with the initial version-0 snapshot at the start of each StreamAsync call.
    // Written from the background RunPhasesAsync task; read by callers via StateHistory.
    // _stateHistoryLock guards all Add/Clear writes and the snapshot read in StateHistory.
    private readonly List<AgentState> _stateHistory = [];
    private readonly object _stateHistoryLock = new();

    /// <summary>
    /// Ordered list of <see cref="AgentState"/> snapshots produced during the most recent
    /// <see cref="StreamAsync"/> call. The first entry is the version-0 seed created at
    /// session start; each subsequent entry is produced by a successful agent handoff.
    /// </summary>
    public IReadOnlyList<AgentState> StateHistory { get { lock (_stateHistoryLock) return [.._stateHistory]; } }

    // Keywords that terminate the current phase. Used only to classify route type during
    // BuildRouteTables(). Routing destinations are config-derived and stored per-instance
    // in _phaseBreakDestinations so they can be updated without touching this set.
    // internal so CorrectionEngine can reference it when scanning for foreign keywords.
    internal static readonly HashSet<string> PhaseBreakKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "BUGS FOUND", "REVISION REQUIRED", "REPLAN REQUIRED", "APPROVED"
    };

    // Config-derived routing destinations for phase-break keywords. Populated by BuildRouteTables().
    // Value is the executor ID to start the next phase from (null = session ends / APPROVED).
    // A route whose SourceAgents includes its own Agent name is treated as terminal (null).
    private Dictionary<string, string?> _phaseBreakDestinations = new(StringComparer.OrdinalIgnoreCase);

    // Session ID / AgentStarting / ResumeExecutorId

    private string _sessionId = string.Empty;

    /// <inheritdoc/>
    public void SetSessionId(string sessionId) => _sessionId = sessionId;

    // Explicit executor ID to start the next StreamAsync from. Takes priority over
    // keyword scanning and agent-name inference. Cleared after first use so subsequent
    // phase-break restarts inside the same stream are determined by keyword output, not
    // this stale hint.
    private string? _resumeExecutorId;

    /// <inheritdoc/>
    public void SetResumeExecutorId(string? executorId) => _resumeExecutorId = executorId;

    /// <inheritdoc/>
    public event Action<string>? AgentStarting;

    /// <inheritdoc/>
    public event Action<string, string, string?>? ToolCalling;

    /// <inheritdoc/>
    public event Action<string, int, int>? TokenBudgetWarning;

    // IOrchestrator

    public async Task<OrchestrationResult> RunAsync(
        string task,
        IReadOnlyList<AgentMessage>? priorHistory = null,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<AgentMessage>();
        var start    = DateTime.UtcNow;

        logger.LogInformation(
            "Session {SessionId} | WorkflowOrchestrator starting '{Name}' | Task: {TaskPreview}",
            _sessionId, config.Name, StringHelpers.Truncate(task, 120));

        try
        {
            await foreach (var msg in StreamAsync(task, priorHistory, cancellationToken).ConfigureAwait(false))
                messages.Add(msg);

            return new OrchestrationResult
            {
                SessionId = _sessionId,
                Succeeded = true,
                Messages  = messages,
                Duration  = DateTime.UtcNow - start,
                TerminationReason = "Completed"
            };
        }
        catch (BudgetExceededException ex)
        {
            logger.LogWarning("Session {SessionId} | Token budget exceeded — {Actual:N0} > {Limit:N0}",
                _sessionId, ex.ActualTokens, ex.LimitTokens);
            return new OrchestrationResult
            {
                SessionId = _sessionId,
                Succeeded = false,
                Messages  = messages,
                Duration  = DateTime.UtcNow - start,
                TerminationReason = "BudgetExceeded",
                ErrorMessage      = ex.Message
            };
        }
        catch (OperationCanceledException)
        {
            return new OrchestrationResult
            {
                SessionId = _sessionId,
                Succeeded = false,
                Messages  = messages,
                Duration  = DateTime.UtcNow - start,
                TerminationReason = "Cancelled",
                ErrorMessage      = "Operation was cancelled."
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Session {SessionId} | Failed after {Turns} turns", _sessionId, messages.Count);
            return new OrchestrationResult
            {
                SessionId = _sessionId,
                Succeeded = false,
                Messages  = messages,
                Duration  = DateTime.UtcNow - start,
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
        var channel = Channel.CreateUnbounded<AgentMessage>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        // Seed state history for this stream; prior history from a resumed session is discarded
        // because state is re-derived from the live execution, not from persisted snapshots.
        lock (_stateHistoryLock)
        {
            _stateHistory.Clear();
            _stateHistory.Add(AgentState.Initial("session"));
        }

        // Pre-build all agents and executor bindings once per session.
        var agents            = config.Agents.ToDictionary(
            a => a.Name,
            a => agentFactory.Create(a, onToolCalling: (agent, tool, args) => ToolCalling?.Invoke(agent, tool, args)),
            StringComparer.OrdinalIgnoreCase);
        var agentInstructions = config.Agents
            .Where(a => !string.IsNullOrWhiteSpace(a.Instructions))
            .ToDictionary(a => a.Name, a => a.Instructions, StringComparer.OrdinalIgnoreCase);
        var agentConfigs = config.Agents.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        // Build per-executor route tables from configuration.
        var routeTables = BuildRouteTables();

        // Build executor bindings (reused across all phases).
        var bindings = BuildExecutorBindings(agents, agentInstructions, agentConfigs, routeTables);

        // Set up the shared agent context.
        int seedTurn   = priorHistory is { Count: > 0 } ? priorHistory[^1].TurnIndex + 1 : 0;
        int seedTokens = priorHistory?.Sum(m => m.Usage?.TotalTokens ?? 0) ?? 0;

        var agentCtx = new AgentContext
        {
            MessageSink      = channel.Writer,
            TurnIndex        = seedTurn,
            CumulativeTokens = seedTokens,
        };

        // Inject task and prior history into shared history.
        agentCtx.History.Add(new ChatMessage(ChatRole.User, task));
        if (priorHistory?.Count > 0)
        {
            logger.LogInformation("Resuming session — replaying {Turns} prior turns.", priorHistory.Count);
            foreach (var prior in priorHistory)
            {
                var role    = prior.Role == "user" ? ChatRole.User : ChatRole.Assistant;
                var content = ContextWindowFilter.TruncateReplayContent(prior);
                var msg     = new ChatMessage(role, content);
                if (role == ChatRole.Assistant && prior.AgentName is not null)
                    msg.AuthorName = prior.AgentName;
                agentCtx.History.Add(msg);
            }
        }

        // Determine the starting executor.
        // Consume _resumeExecutorId (if set) as the highest-priority hint, then clear it
        // so that phase-break restarts triggered inside this same stream aren't affected.
        var resumeHint = _resumeExecutorId;
        _resumeExecutorId = null;
        string startExecutorId = DetermineStartExecutorId(priorHistory, resumeHint);

        // Use an inner linked CTS so the background RunPhasesAsync is always cancelled
        // when the consumer abandons this IAsyncEnumerable (e.g. the RunCommand foreach
        // breaks out early for compaction).  Without this, the old RunPhasesAsync task
        // keeps executing — making LLM calls, running tools, writing events — while a new
        // StreamAsync/RunPhasesAsync starts after compaction, producing two concurrent
        // phase runners that interleave turns and corrupt shared filesystem state.
        using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync("session_start",
                payload: new { start_executor = startExecutorId, resume = priorHistory is { Count: > 0 } });

        var phaseTask = Task.Run(() =>
            RunPhasesAsync(bindings, agentCtx, startExecutorId, phaseCts.Token), phaseCts.Token);

        try
        {
            await foreach (var msg in channel.Reader.ReadAllAsync(phaseCts.Token).ConfigureAwait(false))
                yield return msg;
        }
        finally
        {
            // Cancel the background task whether the consumer read all messages normally
            // or broke out early.  If RunPhasesAsync already finished, this is a no-op.
            await phaseCts.CancelAsync().ConfigureAwait(false);
        }

        // Propagate real exceptions from the background phase runner.
        // Suppress OperationCanceledException that originated from our own phaseCts
        // (i.e., the consumer broke early) — that is expected and not an error.
        string sessionEndReason = "completed";
        Exception? sessionError = null;
        try
        {
            await phaseTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (phaseCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Clean internal cancellation from compaction break — not an error.
            sessionEndReason = "compaction";
        }
        catch (Exception ex)
        {
            sessionEndReason = "error";
            sessionError     = ex;
            throw;
        }
        finally
        {
            // Always emit session_end — even on errors — so the log is always bookended.
            if (eventEmitter is not null)
                await eventEmitter.EmitAsync("session_end",
                    payload: new
                    {
                        reason   = sessionEndReason,
                        turns    = agentCtx.TurnIndex,
                        total_tokens = agentCtx.CumulativeTokens,
                        error        = sessionError?.GetType().Name
                    });
        }
    }

    // Phase loop

    private async Task RunPhasesAsync(
        Dictionary<string, ExecutorBinding> bindings,
        AgentContext agentCtx,
        string startExecutorId,
        CancellationToken ct)
    {
        try
        {
            string currentStart = startExecutorId;
            int phaseCount = 0;
            int maxPhases  = config.Termination?.ResolveMaxIterations() is > 0 and var mp
                ? mp
                : int.MaxValue;

            while (phaseCount < maxPhases)
            {
                phaseCount++;
                logger.LogDebug(
                    "[WorkflowOrchestrator] Phase {Phase}: starting from executor '{Start}'",
                    phaseCount, currentStart);

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync("phase_start",
                        payload: new { phase = phaseCount, from = currentStart });

                MafWorkflow workflow = BuildPhaseWorkflow(bindings, currentStart);
                var sessionId = string.IsNullOrEmpty(_sessionId)
                    ? Guid.NewGuid().ToString("N")[..8]
                    : _sessionId;

                ExceptionDispatchInfo? phaseException = null;

                await using var run = await InProcessExecution.Default
                    .RunStreamingAsync<AgentContext>(workflow, agentCtx, sessionId, ct)
                    .ConfigureAwait(false);

                await foreach (var evt in run.WatchStreamAsync(ct).ConfigureAwait(false))
                {
                    if (evt is WorkflowOutputEvent)
                        break;

                    if (evt is WorkflowErrorEvent error && error.Exception is not null)
                    {
                        // The MAF reflection dispatcher wraps handler exceptions in
                        // TargetInvocationException. Unwrap so that typed catches
                        // (ValidatorStuckException, BudgetExceededException, etc.) in
                        // RunCommand work correctly against the actual exception type.
                        var actual = error.Exception is TargetInvocationException tie
                                     && tie.InnerException is not null
                            ? tie.InnerException
                            : error.Exception;
                        phaseException = ExceptionDispatchInfo.Capture(actual);
                        break;
                    }
                }

                phaseException?.Throw();

                var lastKeyword = agentCtx.LastKeyword;

                logger.LogDebug(
                    "[WorkflowOrchestrator] Phase {Phase} ended — LastKeyword='{Keyword}'",
                    phaseCount, lastKeyword ?? "(none)");

                // Determine whether to continue and where to restart.
                if (lastKeyword is null)
                    break; // Unexpected — no keyword emitted; stop to avoid infinite loop.

                if (!_phaseBreakDestinations.TryGetValue(lastKeyword, out var nextStart))
                    break; // Unknown terminal keyword — stop.

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync("phase_end",
                        payload: new { phase = phaseCount, keyword = lastKeyword, next = nextStart ?? "terminal" });

                if (nextStart is null)
                    break; // APPROVED — session complete.

                // Inject a phase-transition marker so the next executor has explicit context
                // that a new phase is starting. Without this, the Developer sees only the
                // Tester's "BUGS FOUND" message at the end of history and mimics that format
                // (writing "BUGS FOUND" as a section header) instead of implementing fixes.
                agentCtx.History.Add(new ChatMessage(ChatRole.User,
                    $"[fuseraft: {lastKeyword} → {nextStart} — new phase. {nextStart}: implement, don't describe.]"));

                currentStart = nextStart;
            }
        }
        finally
        {
            agentCtx.MessageSink.TryComplete();
        }
    }

    // Workflow construction

    /// <summary>
    /// Builds the linear phase workflow DAG.
    /// The chain is always: (start) → developer? → tester → reviewer
    /// depending on which executor is the start.
    /// </summary>
    private MafWorkflow BuildPhaseWorkflow(
        Dictionary<string, ExecutorBinding> bindings,
        string startExecutorId)
    {
        int startIdx = Array.IndexOf(ExecutorChain, startExecutorId.ToLowerInvariant());
        if (startIdx < 0) startIdx = 0;

        var activeChain = ExecutorChain[startIdx..].Where(id => bindings.ContainsKey(id)).ToArray();

        var start = bindings[activeChain[0]];
        var builder = new WorkflowBuilder(start);

        for (int i = 0; i < activeChain.Length - 1; i++)
        {
            var src  = bindings[activeChain[i]];
            var sink = bindings[activeChain[i + 1]];
            builder.AddEdge(src, sink);
        }

        // Mark tester and reviewer as potential output sources (they can YieldOutput).
        var outputSources = activeChain
            .Where(id => id is "tester" or "reviewer")
            .Select(id => bindings[id])
            .ToArray();

        if (outputSources.Length > 0)
            builder.WithOutputFrom(outputSources);

        return builder.Build(false);
    }

    // Executor binding factory

    private Dictionary<string, ExecutorBinding> BuildExecutorBindings(
        Dictionary<string, AIAgent> agents,
        Dictionary<string, string> agentInstructions,
        Dictionary<string, AgentConfig> agentConfigs,
        Dictionary<string, AgentRouteTable> routeTables)
    {
        return config.Agents
            .Where(a => agents.ContainsKey(a.Name))
            .ToDictionary(
                a => a.Name.ToLowerInvariant(),
                a => CreateExecutorBinding(
                    a.Name,
                    agents[a.Name],
                    agentInstructions.GetValueOrDefault(a.Name, string.Empty),
                    agentConfigs[a.Name],
                    routeTables.GetValueOrDefault(a.Name, new AgentRouteTable())),
                StringComparer.OrdinalIgnoreCase);
    }

    private ExecutorBinding CreateExecutorBinding(
        string agentName,
        AIAgent agent,
        string instructions,
        AgentConfig agentCfg,
        AgentRouteTable routeTable)
    {
        Func<AgentContext, IWorkflowContext, CancellationToken, ValueTask> handler =
            async (ctx, wfCtx, ct) =>
                await RunExecutorAsync(
                    agentName, agent, instructions, agentCfg,
                    routeTable, ctx, wfCtx, ct).ConfigureAwait(false);

        // Declare AgentContext as both the send and yield type so the MAF runtime
        // allows this executor to call SendMessageAsync and YieldOutputAsync with it.
        var executor = new FunctionExecutor<AgentContext>(
            agentName.ToLowerInvariant(),
            handler,
            ExecutorOptions.Default,
            [typeof(AgentContext)],   // sends
            [typeof(AgentContext)],   // yields
            false);                   // declareCrossRunShareable

        return executor;  // implicit conversion Executor → ExecutorBinding
    }

    // Per-executor logic

    private async Task RunExecutorAsync(
        string agentName,
        AIAgent agent,
        string instructions,
        AgentConfig agentCfg,
        AgentRouteTable routeTable,
        AgentContext ctx,
        IWorkflowContext wfCtx,
        CancellationToken ct)
    {
        AgentStarting?.Invoke(agentName);
        agentFactory.OnAgentTurnStarting();
        changeTracker?.BeginTurn(agentName, ctx.TurnIndex);

        // Unified consecutive-failure counter. Increments whenever any correction is injected
        // (validator fail, no keyword, foreign keyword, or multi-keyword). Resets only on a
        // clean routing turn. A single counter prevents alternating failure modes from evading
        // the stuck detection threshold (previously two independent counters each reset the other).
        int consecutiveFailures = 0;

        // Total-turn guard — prevents runaway executors regardless of keyword/validator state.
        // Sized generously (10×) so normal multi-step work never hits this; only true infinite
        // loops will.  Individual stuck conditions (consecutive validator fails, no-keyword) are
        // caught earlier at MaxRetries (3).
        int totalTurns    = 0;
        int maxTotalTurns = MaxRetries * 10;

        while (true)
        {
            if (totalTurns++ >= maxTotalTurns)
                throw new ValidatorStuckException(agentName, "total-turns", totalTurns,
                    $"{agentName} exceeded {maxTotalTurns} total turns without completing.");

            // Apply context window filter (e.g. Reviewer strips tool-call noise).
            var filtered = ContextWindowFilter.Apply(ctx.History, agentCfg.ContextWindow);

            // Emit a soft context-cap warning when the filtered message count approaches
            // the configured fraction of MaxTailMessages. This gives the orchestrator an
            // early signal to trigger inline compaction rather than waiting for the hard limit.
            if (eventEmitter is not null
                && agentCfg.ContextWindow is { ContextCapFraction: > 0, MaxTailMessages: > 0 } cw
                && filtered.Count > (int)(cw.MaxTailMessages * cw.ContextCapFraction))
            {
                await eventEmitter.EmitAsync("context_cap_warning",
                    agent: agentName,
                    turn:  ctx.TurnIndex,
                    payload: new
                    {
                        messages  = filtered.Count,
                        cap       = cw.MaxTailMessages,
                        fraction  = cw.ContextCapFraction,
                        threshold = (int)(cw.MaxTailMessages * cw.ContextCapFraction)
                    });
            }

            IEnumerable<ChatMessage> context = !string.IsNullOrWhiteSpace(instructions)
                ? [new ChatMessage(ChatRole.System, instructions), .. filtered]
                : filtered;

            logger.LogDebug(
                "[{Agent}] Turn {Turn} — invoking with {Count} context messages",
                agentName, totalTurns, !string.IsNullOrWhiteSpace(instructions)
                    ? filtered.Count + 1 : filtered.Count);

            // Emit turn_start so observers can see when a turn begins, not just when it ends.
            // This bounds the "is it stuck?" window: if you see turn_start with no subsequent
            // tool_call or turn_end events for >5 min, the streaming connection may be hung.
            if (eventEmitter is not null)
                await eventEmitter.EmitAsync("turn_start",
                    agent: agentName,
                    turn:  ctx.TurnIndex);

            AgentResponse response;
            try
            {
                response = governanceKernel?.CircuitBreaker is { } cb
                    ? await cb.ExecuteAsync(() => agent.RunAsync(context, null, null, ct)).ConfigureAwait(false)
                    : await agent.RunAsync(context, null, null, ct).ConfigureAwait(false);
            }
            catch (TimeoutException tex)
            {
                // The SSE stream stalled (model stopped generating while keep-alive pings continued).
                // Treat this like a stuck-validator: inject a correction and retry the turn.
                consecutiveFailures++;

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync("turn_timeout",
                        agent: agentName,
                        payload: new { message = tex.Message, consecutive = consecutiveFailures });

                if (consecutiveFailures >= MaxRetries)
                    throw new ValidatorStuckException(agentName, "streaming-timeout",
                        consecutiveFailures, tex.Message);

                var validKeywordsOnTimeout = CorrectionEngine.BuildValidKeywordList(routeTable);

                ctx.History.Add(new ChatMessage(ChatRole.User,
                    "TIMEOUT: Response timed out. Resume from where you left off — prior tool results are in context. " +
                    "Do not re-research. Call write_file or shell_run now, or emit the handoff keyword if all work is complete.\n\n" +
                    $"Valid keywords: {validKeywordsOnTimeout}"));
                continue;
            }

            logger.LogDebug(
                "[{Agent}] Response: {Preview}",
                agentName,
                StringHelpers.Truncate((response.Text ?? "").Replace('\n', ' '), 200));

            var agentMsg = await RecordAndEmitAsync(response, agentName, ctx, ct);

            var responseText = response.Text ?? string.Empty;

            // Step 1: detect routing keywords.
            // HandoffPlugin typed argument takes priority over free-text line matching:
            // when the agent calls handoff(route_keyword: "...") the argument is used
            // directly and text scanning is skipped for the turn.
            var handoffArgKeyword = KeywordDetector.ExtractHandoffToolCallKeyword(response.Messages, routeTable);

            var allKeywords = handoffArgKeyword is not null
                ? [handoffArgKeyword]
                : KeywordDetector.DetectKeywords(responseText, routeTable);

            // Step 1b: ambiguous response — multiple keywords found.

            if (allKeywords.Count > 1)
            {
                consecutiveFailures++;

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync("multi_keyword",
                        agent:   agentName,
                        turn:    agentMsg.TurnIndex,
                        payload: new { keywords = allKeywords, consecutive = consecutiveFailures });

                if (consecutiveFailures >= MaxRetries)
                    throw new ValidatorStuckException(agentName, "multi-keyword", consecutiveFailures,
                        $"{agentName} emitted multiple routing keywords " +
                        $"({string.Join(", ", allKeywords.Select(k => $"'{k}'"))}) " +
                        $"for {consecutiveFailures} consecutive turns.");

                var listed = string.Join(", ", allKeywords.Select(k => $"'{k}'"));
                ctx.History.Add(new ChatMessage(ChatRole.User,
                    $"MULTI-KEYWORD: Response contained {allKeywords.Count} routing keywords: {listed}. " +
                    $"Emit exactly one — remove the others.\n\nValid keywords: {CorrectionEngine.BuildValidKeywordList(routeTable)}"));
                continue;
            }

            string? foundKeyword = allKeywords.Count == 1 ? allKeywords[0] : null;

            if (foundKeyword is not null && eventEmitter is not null)
                await eventEmitter.EmitAsync("keyword_detected",
                    agent:   agentName,
                    turn:    agentMsg.TurnIndex,
                    payload: new { keyword = foundKeyword });

            // Step 2: handle phase-break keywords (BUGS FOUND, APPROVED, etc.)

            if (foundKeyword is not null && PhaseBreakKeywords.Contains(foundKeyword))
            {
                // Validate APPROVED before yielding (RequireShellPass + RequireReviewJudgement).
                if (string.Equals(foundKeyword, "APPROVED", StringComparison.OrdinalIgnoreCase)
                    && routeTable.TerminalValidators.Count > 0)
                {
                    var (termOk, termErr, termValidator) = await RunValidatorsAsync(
                        routeTable.TerminalValidators, ctx.History, ct).ConfigureAwait(false);

                    if (!termOk)
                    {
                        consecutiveFailures++;
                        RecordGovernanceViolation(agentName, termValidator!, consecutiveFailures);

                        if (consecutiveFailures >= MaxRetries)
                            throw new ValidatorStuckException(agentName, termValidator!, consecutiveFailures, termErr!);

                        int histBefore0 = ctx.History.Count;
                        await CorrectionEngine.InjectValidationError(ctx.History, termErr!, consecutiveFailures, responseText, foundKeyword, eventEmitter);
                        await PersistCorrectionsAsync(ctx, histBefore0, ct).ConfigureAwait(false);
                        continue;
                    }
                }

                consecutiveFailures = 0;
                ctx.LastKeyword = foundKeyword;

                // Advance immutable state snapshot on phase-break handoff.
                var phaseBreakDest = _phaseBreakDestinations.TryGetValue(foundKeyword, out var pbd) ? pbd : null;
                ctx.CurrentState = StateHandoff.Advance(ctx.CurrentState, phaseBreakDest ?? agentName);
                lock (_stateHistoryLock) _stateHistory.Add(ctx.CurrentState);
                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync("state_advanced",
                        agent: agentName,
                        turn:  agentMsg.TurnIndex,
                        payload: new { version = ctx.CurrentState.Version, phase_break = foundKeyword, next = phaseBreakDest ?? "(terminal)" });

                await wfCtx.YieldOutputAsync(ctx, ct).ConfigureAwait(false);
                return;
            }

            // Step 3: handle send-forward keywords

            if (foundKeyword is not null && routeTable.Routes.TryGetValue(foundKeyword, out var route))
            {
                // Run validators (AND semantics).
                var (ok, errMsg, failingValidator) = await RunValidatorsAsync(
                    route.Validators, ctx.History, ct).ConfigureAwait(false);

                if (ok)
                {
                    // Record SLO success for each validator that passed.
                    if (route.Validators.Count > 0)
                        governanceKernel?.SloEngine.Get("policy-compliance")?.Record(1.0);

                    consecutiveFailures = 0;
                    ctx.LastKeyword = foundKeyword;

                    if (eventEmitter is not null)
                        await eventEmitter.EmitAsync("agent_routed",
                            agent:   agentName,
                            turn:    agentMsg.TurnIndex,
                            payload: new { keyword = foundKeyword, to = route.NextExecutorName });

                    // Advance immutable state snapshot on send-forward handoff.
                    ctx.CurrentState = StateHandoff.Advance(ctx.CurrentState, route.NextExecutorName);
                    lock (_stateHistoryLock) _stateHistory.Add(ctx.CurrentState);
                    if (eventEmitter is not null)
                        await eventEmitter.EmitAsync("state_advanced",
                            agent: agentName,
                            turn:  agentMsg.TurnIndex,
                            payload: new { version = ctx.CurrentState.Version, to = route.NextExecutorName });

                    // Inject turn-boundary marker so the keyword isn't re-matched next turn.
                    ctx.History.Add(new ChatMessage(ChatRole.User,
                        $"[fuseraft: {agentName} → {route.NextExecutorName}]"));

                    await wfCtx.SendMessageAsync(ctx, route.NextExecutorId, ct).ConfigureAwait(false);
                    return;
                }

                // Validator failed — inject correction and retry.
                // When the agent correctly identifies the routing keyword but the validator
                // blocks it, clamp the counter to at most (MaxRetries - 1) before checking
                // the threshold. This guarantees the agent always gets at least one more
                // attempt to satisfy the validator, even if prior no-keyword turns consumed
                // most of the budget. Finding the right keyword is meaningful progress and
                // should not be penalised the same as producing no keyword at all.
                consecutiveFailures = Math.Min(consecutiveFailures + 1, MaxRetries - 1);
                RecordGovernanceViolation(agentName, failingValidator!, consecutiveFailures);

                if (consecutiveFailures >= MaxRetries)
                    throw new ValidatorStuckException(agentName, failingValidator!, consecutiveFailures, errMsg!);

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync("validation_fail",
                        agent: agentName,
                        payload: new { validator = failingValidator, keyword = foundKeyword, consecutive = consecutiveFailures, message = errMsg });

                int histBefore1 = ctx.History.Count;
                await CorrectionEngine.InjectValidationError(ctx.History, errMsg!, consecutiveFailures, responseText, foundKeyword, eventEmitter);
                await PersistCorrectionsAsync(ctx, histBefore1, ct).ConfigureAwait(false);
                continue;
            }

            // Step 4: no keyword matched

            consecutiveFailures++;

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync("no_keyword",
                    agent:   agentName,
                    turn:    agentMsg.TurnIndex,
                    payload: new { consecutive = consecutiveFailures });

            // Inject a correction message BEFORE checking the HITL threshold so the agent
            // always gets one turn with targeted feedback (stagnation/build-failure/generic)
            // before being escalated. The HITL check runs after injection — if the agent
            // ignored the last correction and still has no keyword, escalate.
            int histBefore2 = ctx.History.Count;
            await CorrectionEngine.InjectNoKeywordCorrection(ctx.History, responseText, agentName, consecutiveFailures, routeTable, eventEmitter);
            await PersistCorrectionsAsync(ctx, histBefore2, ct).ConfigureAwait(false);

            if (consecutiveFailures >= MaxRetries)
                throw new ValidatorStuckException(agentName, "no-keyword", consecutiveFailures,
                    $"{agentName} emitted no routing keyword for {consecutiveFailures} consecutive turns.");
        }
    }

    // Appends response messages to history, creates and streams the AgentMessage, enforces
    // the token budget, and emits turn_end / reasoning / change-tracker flush events.
    // Extracted from RunExecutorAsync to keep that method focused on routing logic.
    private async Task<AgentMessage> RecordAndEmitAsync(
        AgentResponse response,
        string agentName,
        AgentContext ctx,
        CancellationToken ct)
    {
        foreach (var msg in response.Messages)
        {
            if (msg.Role == ChatRole.Assistant && string.IsNullOrEmpty(msg.AuthorName))
                msg.AuthorName = agentName;
            ctx.History.Add(msg);
        }

        var agentMsg = new AgentMessage
        {
            AgentName = agentName,
            Content   = response.Text ?? string.Empty,
            Role      = "assistant",
            TurnIndex = ctx.TurnIndex++,
            Usage     = ExtractUsage(response),
            ToolCalls = ExtractToolCalls(response.Messages)
        };

        ctx.CumulativeTokens += agentMsg.Usage?.TotalTokens ?? 0;

        var warnThreshold = config.WarnTurnTokens;
        if (warnThreshold > 0 && agentMsg.Usage?.InputTokens is { } inputToks && inputToks > warnThreshold)
            TokenBudgetWarning?.Invoke(agentName, inputToks, warnThreshold);

        // Stream before budget check — the work is done and tokens already consumed.
        await ctx.MessageSink.WriteAsync(agentMsg, ct).ConfigureAwait(false);

        if (config.MaxTotalTokens is { } limit && ctx.CumulativeTokens > limit)
            throw new BudgetExceededException(ctx.CumulativeTokens, limit);

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync("turn_end",
                agent: agentName,
                turn:  agentMsg.TurnIndex,
                payload: new
                {
                    input_tokens  = agentMsg.Usage?.InputTokens,
                    output_tokens = agentMsg.Usage?.OutputTokens,
                }).ConfigureAwait(false);

        // Emit reasoning content if the model produced any (e.g. xAI reasoning models).
        // TextReasoningContent items appear in response.Messages alongside TextContent.
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
                    agent:   agentName,
                    turn:    agentMsg.TurnIndex,
                    payload: new { text = truncated }).ConfigureAwait(false);
            }
        }

        if (changeTracker is not null)
        {
            // Use CancellationToken.None so a Ctrl-C doesn't abort the tiny JSON write
            // and produce a spurious WRN on every cancellation.
            try { await changeTracker.FlushTurnAsync(agentName, agentMsg.TurnIndex, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "ChangeTracker flush failed for turn {Turn} ({Agent})",
                    agentMsg.TurnIndex, agentName);
            }
        }

        return agentMsg;
    }

    // Persists any correction messages added by an inject method to the message sink so
    // they are saved to the checkpoint and survive session resume. Each new ChatRole.User
    // message added between historyCountBefore and the current end of history is written
    // as an AgentMessage with Role="user" and AgentName="orchestrator". On resume, the
    // replay loop converts Role="user" messages back to ChatRole.User — exactly what we
    // want so corrections are re-injected into the model's context transparently.
    private static async ValueTask PersistCorrectionsAsync(
        AgentContext ctx,
        int historyCountBefore,
        CancellationToken ct)
    {
        for (int i = historyCountBefore; i < ctx.History.Count; i++)
        {
            var injected = ctx.History[i];
            if (injected.Role != ChatRole.User) continue;

            var correctionText = string.Concat(injected.Contents.OfType<TextContent>().Select(t => t.Text));
            if (string.IsNullOrWhiteSpace(correctionText)) continue;

            var corrMsg = new AgentMessage
            {
                AgentName = "orchestrator",
                Content   = correctionText,
                Role      = "user",
                // TurnIndex: use the preceding agent's index (ctx.TurnIndex - 1) so this
                // correction is logically attached to the failed turn, not the next one.
                // ctx.TurnIndex was post-incremented when the agent message was written,
                // so subtracting 1 recovers the failed turn's index without colliding with
                // the next agent turn that will also use ctx.TurnIndex.
                TurnIndex = Math.Max(0, ctx.TurnIndex - 1),
            };
            await ctx.MessageSink.WriteAsync(corrMsg, ct).ConfigureAwait(false);
        }
    }

    // Validators

    private static async Task<(bool ok, string? error, string? validatorName)> RunValidatorsAsync(
        IReadOnlyList<IRoutingValidator> validators,
        IList<ChatMessage> history,
        CancellationToken ct)
    {
        for (int i = 0; i < validators.Count; i++)
        {
            var result = await validators[i].ValidateAsync(history, ct).ConfigureAwait(false);
            if (!result.IsValid)
            {
                var name = validators[i].GetType().Name;
                return (false, result.ErrorMessage, name);
            }
        }
        return (true, null, null);
    }

    private void RecordGovernanceViolation(string agentName, string validatorName, int consecutiveCount)
    {
        if (governanceKernel is null) return;

        var agentDid = agentFactory.GetDid(agentName);
        governanceKernel.AuditEmitter.Emit(
            GovernanceEventType.PolicyViolation,
            agentId:   agentDid,
            sessionId: _sessionId,
            data: new Dictionary<string, object>
            {
                ["agent_name"]  = agentName,
                ["validator"]   = validatorName,
                ["consecutive"] = consecutiveCount,
            });

        var rlKey = $"{agentDid}:validation:fail";
        if (!governanceKernel.RateLimiter.TryAcquire(rlKey, maxCalls: MaxRetries, window: TimeSpan.FromMinutes(10)))
            throw new ValidatorStuckException(agentName, validatorName, consecutiveCount,
                $"Rate limit exceeded for validator failures on agent '{agentName}'.");

        governanceKernel.SloEngine.Get("policy-compliance")?.Record(0.0);
    }

    // Route table construction

    /// <summary>
    /// Builds per-agent routing tables from the <c>Selection.Routes</c> configuration.
    /// Each table contains:
    /// <list type="bullet">
    ///   <item>Routes: keyword → (nextExecutorId, validators[])</item>
    ///   <item>PhaseBreakKeywords: keywords that terminate the current phase</item>
    ///   <item>TerminalValidators: validators required before APPROVED fires</item>
    /// </list>
    /// </summary>
    private Dictionary<string, AgentRouteTable> BuildRouteTables()
    {
        var tables = new Dictionary<string, AgentRouteTable>(StringComparer.OrdinalIgnoreCase);

        // Reset instance-level destinations so repeated StreamAsync calls on the same
        // orchestrator instance don't accumulate stale entries.
        _phaseBreakDestinations = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (config.Selection.Routes is not { Count: > 0 })
            return tables;

        foreach (var route in config.Selection.Routes)
        {
            // Determine which source agents own this route.
            var sources = route.SourceAgents is { Count: > 0 }
                ? route.SourceAgents
                : null;

            var validatorNames = route.Validators is { Count: > 0 }
                ? route.Validators
                : (route.Validator is not null ? [route.Validator] : (IReadOnlyList<string>)[]);

            var validators = BuildValidatorsFromNames(validatorNames, route);

            foreach (var sourceAgent in sources ?? config.Agents.Select(a => a.Name))
            {
                if (!tables.TryGetValue(sourceAgent, out var table))
                    tables[sourceAgent] = table = new AgentRouteTable();

                if (PhaseBreakKeywords.Contains(route.Keyword))
                {
                    // Phase-break keywords: if they also need validators (like APPROVED),
                    // store as terminal validators on the source agent's table.
                    table.PhaseBreakKeywords.Add(route.Keyword);

                    if (string.Equals(route.Keyword, "APPROVED", StringComparison.OrdinalIgnoreCase)
                        && validators.Count > 0)
                    {
                        table.TerminalValidators = validators;
                    }

                    // Derive routing destination from config. A route that lists its own
                    // agent as a source (e.g. APPROVED: SourceAgents ["Reviewer"], Agent "Reviewer")
                    // routes to itself — treat as terminal (null = session ends).
                    if (!_phaseBreakDestinations.ContainsKey(route.Keyword))
                    {
                        bool isTerminal = sources?.Any(s =>
                            string.Equals(s, route.Agent, StringComparison.OrdinalIgnoreCase)) == true;
                        _phaseBreakDestinations[route.Keyword] = isTerminal
                            ? null
                            : route.Agent.ToLowerInvariant();
                    }
                }
                else
                {
                    // Send-forward routes.
                    var nextExecutorId   = route.Agent.ToLowerInvariant();
                    var nextExecutorName = route.Agent;
                    table.Routes[route.Keyword] = new RouteInfo(nextExecutorId, nextExecutorName, validators);
                }
            }
        }

        // Populate ForeignSendForwardKeywords on each table: the send-forward keywords that
        // belong to OTHER agents' route tables. This lets InjectNoKeywordCorrection produce a
        // targeted "WRONG KEYWORD" message (e.g. "HANDOFF TO DEVELOPER is not valid for
        // Developer") instead of a generic "no keyword found" correction that the model can
        // easily ignore — which is the root cause of Developer infinite-loop sessions.
        var allSendForward = tables.Values
            .SelectMany(t => t.Routes.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (agent, table) in tables)
        {
            foreach (var kw in allSendForward)
                if (!table.Routes.ContainsKey(kw))
                    table.ForeignSendForwardKeywords.Add(kw);
        }

        return tables;
    }

    private IReadOnlyList<IRoutingValidator> BuildValidatorsFromNames(
        IReadOnlyList<string> names,
        KeywordRoute route)
    {
        var result = new List<IRoutingValidator>();

        foreach (var name in names)
        {
            IRoutingValidator? v = name.ToLowerInvariant() switch
            {
                "requireshellpass"       => new RequireShellPassValidator(
                                               route.RequiredCommandPattern,
                                               config.Validation?.ChangeLogPath),
                "requirewritefile"       => new HandoffToTesterValidator(
                                               shellFallbackPattern: route.ShellFallbackPattern,
                                               changeLogPath: config.Validation?.ChangeLogPath),
                // Checks that every file listed in brief.json's files_to_change was actually
                // written this session (session-scoped, not just the current turn).
                "requireallfileswritten" => config.Validation is not null
                                               ? new RequireAllFilesWrittenValidator(
                                                     config.Validation.BriefPath,
                                                     config.Validation.ChangeLogPath)
                                               : null,
                "requirebrief"           => config.Validation is not null
                                               ? new RequireBriefValidator(config.Validation.BriefPath)
                                               : null,
                "testreportvalid"        => config.Validation is not null
                                               ? new HandoffToReviewerValidator(config.Validation)
                                               : null,
                "requirereviewjudgement" => new RequireReviewJudgementValidator(),
                _ => null
            };

            if (v is not null)
                result.Add(v);
        }

        return result;
    }

    // Start executor resolution

    private string DetermineStartExecutorId(
        IReadOnlyList<AgentMessage>? priorHistory,
        string? resumeHint = null)
    {
        var defaultExecutor = config.Selection.DefaultAgent?.ToLowerInvariant() ?? "planner";

        // Priority 1: explicit hint (set by caller before compaction / resume)
        // Most accurate — the caller captured exactly which agent was active.
        if (!string.IsNullOrWhiteSpace(resumeHint))
        {
            logger.LogDebug(
                "[WorkflowOrchestrator] DetermineStartExecutorId: using explicit hint '{Hint}'",
                resumeHint);
            return resumeHint.ToLowerInvariant();
        }

        if (priorHistory is not { Count: > 0 })
            return defaultExecutor;

        // Priority 2: keyword scan (newest-first)
        // A routing keyword on its own line tells us unambiguously where to go next.
        for (int i = priorHistory.Count - 1; i >= 0; i--)
        {
            var msg = priorHistory[i];
            if (msg.Role != "assistant" || string.IsNullOrEmpty(msg.Content)) continue;

            // Phase-break keywords (BUGS FOUND → developer, REPLAN REQUIRED → planner, etc.)
            // Use strict matching and config-derived destinations (_phaseBreakDestinations).
            foreach (var keyword in PhaseBreakKeywords)
            {
                if (KeywordDetector.IsKeywordOnOwnLineStrict(msg.Content, keyword) &&
                    _phaseBreakDestinations.TryGetValue(keyword, out var nextStart) &&
                    nextStart is not null)
                {
                    logger.LogDebug(
                        "[WorkflowOrchestrator] DetermineStartExecutorId: phase-break keyword '{Keyword}' → '{Next}'",
                        keyword, nextStart);
                    return nextStart;
                }
            }

            // Send-forward route keywords (HANDOFF TO DEVELOPER, HANDOFF TO TESTER, etc.)
            // When a handoff keyword is the last thing in history, start from its RECIPIENT
            // so compaction-triggered restarts continue from the right executor rather than
            // always resetting to the default (Planner).
            if (config.Selection.Routes is { Count: > 0 })
            {
                foreach (var route in config.Selection.Routes)
                {
                    if (!PhaseBreakKeywords.Contains(route.Keyword) &&
                        KeywordDetector.IsKeywordOnOwnLineStrict(msg.Content, route.Keyword))
                    {
                        logger.LogDebug(
                            "[WorkflowOrchestrator] DetermineStartExecutorId: route keyword '{Keyword}' → '{Next}'",
                            route.Keyword, route.Agent);
                        return route.Agent.ToLowerInvariant();
                    }
                }
            }
        }

        // Priority 3: last active agent name
        // When compaction fires mid-agent-turn (the agent is doing tool calls with no
        // handoff keyword yet), the retained messages carry no keyword but their
        // AgentName tells us exactly which executor was running.  Resume from there
        // instead of falling back to Planner and re-executing work that's already done.
        for (int i = priorHistory.Count - 1; i >= 0; i--)
        {
            var msg = priorHistory[i];
            if (msg.Role != "assistant" || string.IsNullOrWhiteSpace(msg.AgentName)) continue;

            var agentId = msg.AgentName.ToLowerInvariant();

            // Only resume from known workflow executors; ignore System/Human/compaction messages.
            if (Array.IndexOf(ExecutorChain, agentId) >= 0)
            {
                logger.LogDebug(
                    "[WorkflowOrchestrator] DetermineStartExecutorId: agent-name fallback → '{Agent}' " +
                    "(no keyword found in retained history; likely compaction mid-turn)",
                    agentId);
                return agentId;
            }
        }

        // Priority 4: configured default
        return defaultExecutor;
    }

    // Token/cost helpers

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

}

// Route table DTOs

/// <summary>Per-executor routing metadata.</summary>
internal sealed class AgentRouteTable
{
    /// <summary>Send-forward routes: keyword → next executor info + validators.</summary>
    public Dictionary<string, RouteInfo> Routes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keywords that break the current phase and trigger an outer-loop restart.</summary>
    public HashSet<string> PhaseBreakKeywords { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Validators run before APPROVED is accepted (RequireShellPass + RequireReviewJudgement).</summary>
    public IReadOnlyList<IRoutingValidator> TerminalValidators { get; set; } = [];

    /// <summary>
    /// Send-forward keywords that belong to OTHER agents' route tables.
    /// Populated by <see cref="WorkflowOrchestrator.BuildRouteTables"/> so that
    /// <see cref="CorrectionEngine.InjectNoKeywordCorrection"/> can produce a specific
    /// "wrong keyword" error instead of a generic "no keyword" correction when an agent
    /// emits e.g. "HANDOFF TO DEVELOPER" (a Planner→Developer route) from Developer.
    /// </summary>
    public HashSet<string> ForeignSendForwardKeywords { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Information about a single send-forward route.</summary>
internal sealed record RouteInfo(
    string NextExecutorId,
    string NextExecutorName,
    IReadOnlyList<IRoutingValidator> Validators);
