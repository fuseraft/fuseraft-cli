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
using fuseraft.Orchestration.Validation;
using fuseraft.Orchestration.Workflow;

// Disambiguate from Microsoft.Agents.AI.AgentFactory
using fuseraft.Infrastructure;
using AgentFactory = fuseraft.Infrastructure.Agents.AgentFactory;

namespace fuseraft.Orchestration;

/// <summary>
/// Cycle-native sibling of <see cref="GraphOrchestrator"/>. Executes the exact same
/// <c>Selection.Graph</c> config shape, activated by <c>Selection.Type: "workflow"</c>, but
/// compiles the entire graph — including edges that loop back to an earlier node — into a
/// single, persistent MAF <see cref="WorkflowBuilder"/> graph built once per session, instead
/// of <see cref="GraphOrchestrator"/>'s per-cycle phase-restart loop. There is no forward/back
/// edge distinction: every routing decision is a uniform <c>SendMessageAsync</c> to the
/// keyword-matched target executor, made by fuseraft's own routing code (not a MAF conditional
/// edge) — MAF's <c>AddEdge</c> calls only register the static topology so that the target
/// send is legal.
///
/// <para>
/// <b>v1 scope</b>: <c>Parallel: true</c> nodes, <c>SubGraphId</c> nodes,
/// <c>RequireHumanApproval</c>, <c>RecoveryAgent</c>, and no-keyword (unconditional) edges are
/// rejected at config-validation time (see <c>OrchestratorBuilder</c>) rather than silently
/// ignored — so, unlike <see cref="GraphOrchestrator"/>, there is no human-approval gate or
/// recovery-agent invocation to wire here. Resume-from-a-specific-node after compaction is not
/// wired up — sessions always start from <c>EntryNode</c>. See <c>docs/strategies.md</c> for the
/// full list of differences from <see cref="GraphOrchestrator"/>.
/// </para>
/// </summary>
public sealed class WorkflowOrchestrator(
    OrchestrationConfig config,
    AgentFactory agentFactory,
    ILogger<WorkflowOrchestrator> logger,
    ChangeTracker? changeTracker = null,
    EventEmitter? eventEmitter = null,
    GovernanceKernel? governanceKernel = null,
    IContextAssemblyPipeline? contextPipeline = null) : IOrchestrator
{
    // Mirrors GraphOrchestrator.DefaultMaxRetries — CorrectionEngine.InjectValidationError's
    // default parameter references that constant, not this one, so the two are independent
    // values that happen to share the same default; pass maxRetries explicitly everywhere here.
    internal const int DefaultMaxRetries = 4;

    private string _sessionId = string.Empty;
    private string _task = string.Empty;

    // Shared mutable counter for the total number of node executions across the whole
    // session, captured by every node executor's closure. Stands in for GraphOrchestrator's
    // per-phase iteration cap, since there are no phases here to count.
    private sealed class NodeExecutionCounter
    {
        public int Value;
    }

    // IOrchestrator

    public void SetSessionId(string sessionId)
    {
        _sessionId = sessionId;
        agentFactory.SetSessionId(sessionId);
        contextPipeline?.SetSessionId(sessionId);
    }

    public event Action<string>? AgentStarting;
    public event Action<string, string, string?>? ToolCalling;
    public event Action<string, int, int>? TokenBudgetWarning;

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
                SessionId         = _sessionId,
                Succeeded         = true,
                Messages          = messages,
                Duration          = DateTime.UtcNow - start,
                TerminationReason = "Completed"
            };
        }
        catch (BudgetExceededException ex)
        {
            logger.LogWarning("Session {SessionId} | Token budget exceeded — {Actual:N0} > {Limit:N0}",
                _sessionId, ex.ActualTokens, ex.LimitTokens);
            return new OrchestrationResult
            {
                SessionId         = _sessionId,
                Succeeded         = false,
                Messages          = messages,
                Duration          = DateTime.UtcNow - start,
                TerminationReason = "BudgetExceeded",
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
            logger.LogError(ex, "Session {SessionId} | Failed after {Turns} turns", _sessionId, messages.Count);
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
        var wfCfg = config.Selection.Graph
            ?? throw new InvalidOperationException(
                "Selection.Graph must be configured when Selection.Type is 'workflow'.");

        if (wfCfg.Nodes.Count == 0)
            throw new InvalidOperationException("Selection.Graph.Nodes must contain at least one node.");

        var channel = Channel.CreateUnbounded<AgentMessage>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        var agents = config.Agents.ToDictionary(
            a => a.Name,
            a => agentFactory.Create(a, onToolCalling: (agent, tool, args) => ToolCalling?.Invoke(agent, tool, args)),
            StringComparer.OrdinalIgnoreCase);
        var agentInstructions = config.Agents
            .Where(a => !string.IsNullOrWhiteSpace(a.Instructions))
            .ToDictionary(a => a.Name, a => a.Instructions, StringComparer.OrdinalIgnoreCase);
        var agentConfigs = config.Agents.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        var nodeById = wfCfg.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);

        var entryNodeId = !string.IsNullOrEmpty(wfCfg.EntryNode)
            ? wfCfg.EntryNode
            : wfCfg.Nodes[0].Id;

        var routeTables = BuildNodeRouteTables(wfCfg, nodeById);

        int maxNodeExecutions = config.Termination?.ResolveMaxIterations() is > 0 and var mi ? mi : int.MaxValue;
        var nodeExecutions = new NodeExecutionCounter();

        var bindings = BuildExecutorBindings(
            agents, agentInstructions, agentConfigs, routeTables, wfCfg, nodeExecutions, maxNodeExecutions);

        MafWorkflow workflow = BuildWorkflow(bindings, wfCfg, entryNodeId);

        int seedTurn   = priorHistory is { Count: > 0 } ? priorHistory[^1].TurnIndex + 1 : 0;
        int seedTokens = priorHistory?.Sum(m => m.Usage?.TotalTokens ?? 0) ?? 0;

        var agentCtx = new AgentContext
        {
            MessageSink      = channel.Writer,
            TurnIndex        = seedTurn,
            CumulativeTokens = seedTokens,
        };

        agentCtx.History.Add(new ChatMessage(ChatRole.User, task));
        if (priorHistory?.Count > 0)
        {
            logger.LogDebug("Resuming session — replaying {Turns} prior turns.", priorHistory.Count);
            foreach (var prior in priorHistory)
            {
                var role    = prior.Role == MessageRole.User ? ChatRole.User : ChatRole.Assistant;
                var content = ContextWindowFilter.TruncateReplayContent(prior);
                var msg     = new ChatMessage(role, content);
                if (role == ChatRole.Assistant && prior.AgentName is not null)
                    msg.AuthorName = prior.AgentName;
                agentCtx.History.Add(msg);
            }
        }

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync(EventTypes.SessionStart,
                payload: new { task, start_node = entryNodeId, resume = priorHistory is { Count: > 0 } });

        var runTask = Task.Run(
            () => RunWorkflowAsync(workflow, agentCtx, runCts.Token),
            runCts.Token);

        try
        {
            await foreach (var msg in channel.Reader.ReadAllAsync(runCts.Token).ConfigureAwait(false))
                yield return msg;
        }
        finally
        {
            await runCts.CancelAsync().ConfigureAwait(false);
        }

        string sessionEndReason = "completed";
        Exception? sessionError = null;
        try
        {
            await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (runCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
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
            if (eventEmitter is not null)
                await eventEmitter.EmitAsync(EventTypes.SessionEnd,
                    payload: new
                    {
                        reason       = sessionEndReason,
                        turns        = agentCtx.TurnIndex,
                        total_tokens = agentCtx.CumulativeTokens,
                        error        = sessionError?.GetType().Name
                    });
        }
    }

    // -------------------------------------------------------------------------
    // Single persistent workflow run (replaces GraphOrchestrator's phase-restart loop)
    // -------------------------------------------------------------------------

    private async Task RunWorkflowAsync(
        MafWorkflow workflow,
        AgentContext agentCtx,
        CancellationToken ct)
    {
        try
        {
            var sessionId = string.IsNullOrEmpty(_sessionId)
                ? Guid.NewGuid().ToString("N")[..8]
                : _sessionId;

            ExceptionDispatchInfo? runException = null;

            await using var run = await InProcessExecution.Default
                .RunStreamingAsync<AgentContext>(workflow, agentCtx, sessionId, ct)
                .ConfigureAwait(false);

            await foreach (var evt in run.WatchStreamAsync(ct).ConfigureAwait(false))
            {
                if (evt is WorkflowOutputEvent)
                    break;

                if (evt is WorkflowErrorEvent error && error.Exception is not null)
                {
                    var actual = error.Exception is TargetInvocationException tie
                                 && tie.InnerException is not null
                        ? tie.InnerException
                        : error.Exception;
                    runException = ExceptionDispatchInfo.Capture(actual);
                    break;
                }
            }

            runException?.Throw();

            if (eventEmitter is not null)
                _ = eventEmitter.EmitAsync(EventTypes.TerminationSatisfied, payload: new { });
        }
        finally
        {
            agentCtx.MessageSink.TryComplete();
        }
    }

    // -------------------------------------------------------------------------
    // Workflow construction — every edge, cyclic or not, is registered once.
    // -------------------------------------------------------------------------

    private static MafWorkflow BuildWorkflow(
        Dictionary<string, ExecutorBinding> bindings,
        GraphConfig wfCfg,
        string entryNodeId)
    {
        if (!bindings.ContainsKey(entryNodeId))
            throw new InvalidOperationException(
                $"No executor binding for workflow node '{entryNodeId}'. " +
                $"Verify that the node's Agent references a defined agent.");

        var addedEdgePairs = new HashSet<(string From, string To)>();
        foreach (var edge in wfCfg.Edges)
            addedEdgePairs.Add((edge.From.ToLowerInvariant(), edge.To.ToLowerInvariant()));

        var builder = new WorkflowBuilder(bindings[entryNodeId]);

        foreach (var (from, to) in addedEdgePairs)
            if (bindings.TryGetValue(from, out var fb) && bindings.TryGetValue(to, out var tb))
                builder.AddEdge(fb, tb);

        builder.WithOutputFrom(bindings.Values.ToArray());

        return builder.Build(false);
    }

    private Dictionary<string, ExecutorBinding> BuildExecutorBindings(
        Dictionary<string, AIAgent> agents,
        Dictionary<string, string> agentInstructions,
        Dictionary<string, AgentConfig> agentConfigs,
        Dictionary<string, AgentRouteTable> routeTables,
        GraphConfig wfCfg,
        NodeExecutionCounter nodeExecutions,
        int maxNodeExecutions)
    {
        var bindings = new Dictionary<string, ExecutorBinding>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in wfCfg.Nodes)
        {
            if (!agents.ContainsKey(node.Agent))
            {
                logger.LogWarning(
                    "[WorkflowOrchestrator] Node '{NodeId}' references unknown agent '{Agent}' — skipping.",
                    node.Id, node.Agent);
                continue;
            }

            var routeTable   = routeTables.GetValueOrDefault(node.Id, new AgentRouteTable());
            var agentName    = node.Agent;
            var isTerminal   = node.Terminal;
            var agent        = agents[agentName];
            var instructions = agentInstructions.GetValueOrDefault(agentName, string.Empty);
            var agentCfg     = agentConfigs.GetValueOrDefault(agentName) ?? new AgentConfig();

            Func<AgentContext, IWorkflowContext, CancellationToken, ValueTask> handler =
                async (ctx, wfCtx, ct) =>
                    await RunNodeExecutorAsync(
                        node.Id, agentName, agent, instructions, agentCfg,
                        isTerminal, routeTable, ctx, wfCtx, ct,
                        nodeExecutions, maxNodeExecutions).ConfigureAwait(false);

            var executor = new FunctionExecutor<AgentContext>(
                node.Id.ToLowerInvariant(),
                handler,
                ExecutorOptions.Default,
                [typeof(AgentContext)],
                [typeof(AgentContext)],
                false);

            bindings[node.Id] = executor;
        }

        return bindings;
    }

    // -------------------------------------------------------------------------
    // Per-node execution — uniform routing, no forward/back distinction.
    // -------------------------------------------------------------------------

    private async Task RunNodeExecutorAsync(
        string nodeId,
        string agentName,
        AIAgent agent,
        string instructions,
        AgentConfig agentCfg,
        bool isTerminal,
        AgentRouteTable routeTable,
        AgentContext ctx,
        IWorkflowContext wfCtx,
        CancellationToken ct,
        NodeExecutionCounter nodeExecutions,
        int maxNodeExecutions)
    {
        if (Interlocked.Increment(ref nodeExecutions.Value) > maxNodeExecutions)
        {
            logger.LogWarning(
                "[WorkflowOrchestrator] Session reached the maximum of {Max} node executions — terminating.",
                maxNodeExecutions);
            if (eventEmitter is not null)
                _ = eventEmitter.EmitAsync(EventTypes.MaxTurnsExceeded,
                    payload: new { executions = nodeExecutions.Value, max = maxNodeExecutions });
            await ctx.MessageSink.WriteAsync(new AgentMessage
            {
                AgentName = AgentNames.Orchestrator,
                Content   =
                    $"The session reached the maximum of {maxNodeExecutions} node executions " +
                    "without completing the task. Review the conversation history and consider " +
                    "restarting with a more specific task or a higher Termination.MaxIterations.",
                Role      = "assistant",
                TurnIndex = ctx.TurnIndex++,
            }, ct).ConfigureAwait(false);
            await wfCtx.YieldOutputAsync(ctx, ct).ConfigureAwait(false);
            return;
        }

        AgentStarting?.Invoke(agentName);
        agentFactory.OnAgentTurnStarting();
        changeTracker?.BeginTurn(agentName, ctx.TurnIndex);

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync(EventTypes.AgentStart, agent: agentName, turn: ctx.TurnIndex);

        int maxRetries       = config.Selection.Graph?.MaxRetries ?? DefaultMaxRetries;
        int maxTotalTurns    = maxRetries * (config.Selection.Graph?.MaxTotalTurnsMultiplier ?? 10);
        int consecutiveFails = 0;
        int totalTurns       = 0;

        while (true)
        {
            if (totalTurns++ >= maxTotalTurns)
            {
                if (eventEmitter is not null)
                    _ = eventEmitter.EmitAsync(EventTypes.RetryExhausted,
                        agent:   agentName,
                        turn:    ctx.TurnIndex,
                        payload: new { reason = "total-turns", turns = totalTurns, max = maxTotalTurns });
                throw new ValidatorStuckException(agentName, "total-turns", totalTurns,
                    $"Node '{nodeId}' ({agentName}) exceeded {maxTotalTurns} total turns without completing.");
            }

            if (totalTurns > 1 && eventEmitter is not null)
                await eventEmitter.EmitAsync(EventTypes.RetryAttempt,
                    agent:   agentName,
                    turn:    ctx.TurnIndex,
                    payload: new { attempt = totalTurns, consecutive_fails = consecutiveFails });

            var (response, agentMsg, updatedFails, shouldContinue) =
                await RunSingleNodeTurnAsync(
                    nodeId, agentName, agent, routeTable, agentCfg, instructions,
                    ctx, consecutiveFails, maxRetries, totalTurns, ct);
            consecutiveFails = updatedFails;
            if (shouldContinue) continue;

            var responseText = response!.Text ?? string.Empty;

            // Terminal node: validate then end the session.
            if (isTerminal)
            {
                if (routeTable.TerminalValidators.Count > 0)
                {
                    var (termOk, termErr, termValidator) = await RunValidatorsAsync(
                        routeTable.TerminalValidators, ctx.History, ct).ConfigureAwait(false);

                    if (!termOk)
                    {
                        consecutiveFails++;
                        RecordGovernanceViolation(agentName, termValidator!, consecutiveFails, maxRetries);

                        if (consecutiveFails >= maxRetries)
                            throw new ValidatorStuckException(agentName, termValidator!, consecutiveFails, termErr!);

                        await EmitAndInjectValidationFailureAsync(
                            agentName, "(terminal)", termValidator!, termErr!, responseText, consecutiveFails, maxRetries, ctx, ct);
                        continue;
                    }
                }

                ctx.LastKeyword = "__WORKFLOW_TERMINAL__";
                await wfCtx.YieldOutputAsync(ctx, ct).ConfigureAwait(false);
                return;
            }

            // Keyword detection — routing is tool-call-only (handoff(route_keyword: ...)).
            // Unlike GraphOrchestrator, there is no text-on-its-own-line fallback: every node's
            // agent is required (config-validation time, in OrchestratorBuilder) to have the
            // Handoff plugin enabled, so ExtractHandoffToolCallKeyword is the sole signal.
            // Because a single route_keyword tool argument can never produce more than one
            // candidate, there is no "ambiguous multi-keyword" case to handle here (unlike
            // GraphOrchestrator, which also scans free text and can find several matches).
            string? foundKeyword = KeywordDetector.ExtractHandoffToolCallKeyword(response!.Messages, routeTable);

            if (foundKeyword is not null && eventEmitter is not null)
                await eventEmitter.EmitAsync(EventTypes.KeywordDetected,
                    agent:   agentName,
                    turn:    agentMsg!.TurnIndex,
                    payload: new { keyword = foundKeyword });

            if (foundKeyword is not null && routeTable.Routes.TryGetValue(foundKeyword, out var route))
            {
                var (ok, err, validatorName) = await RunValidatorsAsync(
                    route.Validators, ctx.History, ct).ConfigureAwait(false);

                if (ok)
                {
                    if (route.Validators.Count > 0)
                        governanceKernel?.SloEngine.Get("policy-compliance")?.Record(1.0);

                    consecutiveFails = 0;
                    ctx.LastKeyword  = foundKeyword;

                    if (eventEmitter is not null)
                        await eventEmitter.EmitAsync(EventTypes.AgentRouted,
                            agent:   agentName,
                            turn:    agentMsg!.TurnIndex,
                            payload: new { keyword = foundKeyword, to = route.NextExecutorName });

                    ctx.History.Add(new ChatMessage(ChatRole.User,
                        $"[fuseraft: {agentName} → {route.NextExecutorName}]"));

                    await wfCtx.SendMessageAsync(ctx, route.NextExecutorId, ct).ConfigureAwait(false);
                    return;
                }

                consecutiveFails = Math.Min(consecutiveFails + 1, maxRetries - 1);
                RecordGovernanceViolation(agentName, validatorName!, consecutiveFails, maxRetries);

                if (consecutiveFails >= maxRetries)
                    throw new ValidatorStuckException(agentName, validatorName!, consecutiveFails, err!);

                await EmitAndInjectValidationFailureAsync(
                    agentName, foundKeyword, validatorName!, err!, responseText, consecutiveFails, maxRetries, ctx, ct);
                continue;
            }

            // BLOCKED: agent declared an unrecoverable blocker — halt immediately, no retry.
            if (foundKeyword is null && KeywordDetector.IsBlocked(responseText))
                throw new AgentBlockedException(agentName, responseText);

            // No keyword matched.
            consecutiveFails++;

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync(EventTypes.KeywordNotFound,
                    agent:   agentName,
                    turn:    agentMsg!.TurnIndex,
                    payload: new { consecutive = consecutiveFails, source = "workflow_orchestrator" });

            int histBefore = ctx.History.Count;
            await CorrectionEngine.InjectNoKeywordCorrection(
                ctx.History, responseText, agentName, consecutiveFails, routeTable, eventEmitter, agentMsg!.ToolCalls);
            await PersistCorrectionsAsync(ctx, histBefore, ct).ConfigureAwait(false);

            if (consecutiveFails >= maxRetries)
            {
                if (eventEmitter is not null)
                    _ = eventEmitter.EmitAsync(EventTypes.RetryExhausted,
                        agent:   agentName,
                        turn:    agentMsg!.TurnIndex,
                        payload: new { reason = "no-keyword", consecutive = consecutiveFails, max = maxRetries });
                throw new ValidatorStuckException(agentName, "no-keyword", consecutiveFails,
                    $"Node '{nodeId}' ({agentName}) emitted no routing keyword for {consecutiveFails} consecutive turns.");
            }

            if (eventEmitter is not null)
                _ = eventEmitter.EmitAsync(EventTypes.RetryScheduled,
                    agent:   agentName,
                    turn:    agentMsg!.TurnIndex,
                    payload: new { reason = "no-keyword", attempt = consecutiveFails + 1, max = maxRetries });
        }
    }

    private async Task<(AgentResponse? Response, AgentMessage? AgentMsg, int ConsecutiveFails, bool ShouldContinue)>
        RunSingleNodeTurnAsync(
            string nodeId,
            string agentName,
            AIAgent agent,
            AgentRouteTable routeTable,
            AgentConfig agentCfg,
            string instructions,
            AgentContext ctx,
            int consecutiveFails,
            int maxRetries,
            int totalTurns,
            CancellationToken ct)
    {
        var context = await HandleContextOverflowAsync(agentName, agentCfg, instructions, ctx, ct)
            .ConfigureAwait(false);

        if (eventEmitter is not null)
        {
            eventEmitter.SetTurn(ctx.TurnIndex);
            await eventEmitter.EmitAsync(EventTypes.TurnStart, agent: agentName, turn: ctx.TurnIndex);
        }

        AgentResponse response;
        try
        {
            response = governanceKernel?.CircuitBreaker is { } cb
                ? await cb.ExecuteAsync(() => agent.RunAsync(context, null, null, ct)).ConfigureAwait(false)
                : await agent.RunAsync(context, null, null, ct).ConfigureAwait(false);
        }
        catch (TimeoutException tex)
        {
            consecutiveFails++;

            if (eventEmitter is not null)
            {
                await eventEmitter.EmitAsync(EventTypes.ModelTimeout,
                    agent:   agentName,
                    payload: new { message = tex.Message, consecutive = consecutiveFails });
                await eventEmitter.EmitAsync(EventTypes.TurnTimeout,
                    agent:   agentName,
                    payload: new { message = tex.Message, consecutive = consecutiveFails });
                await eventEmitter.EmitAsync(EventTypes.AgentTimeout,
                    agent:   agentName,
                    payload: new { message = tex.Message, consecutive = consecutiveFails });
            }

            if (consecutiveFails >= maxRetries)
                throw new ValidatorStuckException(agentName, "streaming-timeout", consecutiveFails, tex.Message);

            if (eventEmitter is not null)
                _ = eventEmitter.EmitAsync(EventTypes.RetryScheduled,
                    agent:   agentName,
                    payload: new { reason = "streaming-timeout", attempt = consecutiveFails + 1, max = maxRetries });

            ctx.History.Add(new ChatMessage(ChatRole.User,
                "TIMEOUT: Response timed out. Resume from where you left off — prior tool results are in context. " +
                "Do not re-research. Call write_file or shell_run now, or emit the handoff keyword if all work is complete.\n\n" +
                $"Valid keywords: {CorrectionEngine.BuildValidKeywordList(routeTable)}"));
            return (null, null, consecutiveFails, true);
        }

        logger.LogDebug(
            "[{Agent}] Node '{NodeId}' turn {Turn} — response: {Preview}",
            agentName, nodeId, totalTurns,
            StringHelpers.Truncate((response.Text ?? "").Replace('\n', ' '), 200));

        var agentMsg = await RecordAndEmitAsync(response, agentName, ctx, ct);
        return (response, agentMsg, consecutiveFails, false);
    }

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
            Usage     = OrchestratorHelpers.ExtractUsage(response),
            ToolCalls = OrchestratorHelpers.ExtractToolCalls(response.Messages)
        };

        ctx.CumulativeTokens += agentMsg.Usage?.TotalTokens ?? 0;

        var warnThreshold = config.WarnTurnTokens;
        if (warnThreshold > 0 && agentMsg.Usage?.InputTokens is { } inputToks && inputToks > warnThreshold)
            TokenBudgetWarning?.Invoke(agentName, inputToks, warnThreshold);

        // Stream before budget check — work was done and tokens already consumed.
        await ctx.MessageSink.WriteAsync(agentMsg, ct).ConfigureAwait(false);

        if (config.MaxTotalTokens is { } limit && ctx.CumulativeTokens > limit)
            throw new BudgetExceededException(ctx.CumulativeTokens, limit);

        if (eventEmitter is not null)
        {
            await eventEmitter.EmitAsync(EventTypes.TurnEnd,
                agent: agentName,
                turn:  agentMsg.TurnIndex,
                payload: new
                {
                    input_tokens  = agentMsg.Usage?.InputTokens,
                    output_tokens = agentMsg.Usage?.OutputTokens,
                }).ConfigureAwait(false);

            await eventEmitter.EmitAsync(EventTypes.AgentEnd,
                agent: agentName,
                turn:  agentMsg.TurnIndex,
                payload: new
                {
                    input_tokens  = agentMsg.Usage?.InputTokens,
                    output_tokens = agentMsg.Usage?.OutputTokens,
                }).ConfigureAwait(false);
        }

        if (changeTracker is not null)
        {
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

    // -------------------------------------------------------------------------
    // Context assembly and governance helpers — same shape as GraphOrchestrator's,
    // independently implemented (no shared/extracted helper) per the established codebase
    // convention of each orchestrator owning its own validator-resolution logic (see also
    // StrategyFactory.BuildValidators). Unlike GraphOrchestrator, there is no recovery-agent
    // invocation or human-approval gate here — v1 scope rejects RequireHumanApproval and
    // RecoveryAgent at config-validation time (see the class doc comment), so governance
    // integration is limited to the circuit breaker and per-validator-failure audit/rate-limit/SLO
    // recording below.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Assembles the per-turn message list via the unified context pipeline (when configured)
    /// or the legacy <see cref="ContextWindowFilter"/>, emitting <c>context_window_warn</c> /
    /// <c>context_assembly</c> events as appropriate.
    /// </summary>
    private async Task<IEnumerable<ChatMessage>> HandleContextOverflowAsync(
        string agentName,
        AgentConfig agentCfg,
        string instructions,
        AgentContext ctx,
        CancellationToken ct)
    {
        IEnumerable<ChatMessage> context;
        if (contextPipeline is not null)
        {
            var assembled = await contextPipeline.AssembleAsync(
                new AgentExecutionRequest
                {
                    AgentName     = agentName,
                    Task          = _task,
                    SharedHistory = ctx.History,
                    AgentConfig   = agentCfg,
                    SessionId     = _sessionId,
                }, ct);
            context = assembled.Messages;
            await EmitContextWindowWarnAsync(agentName, agentCfg, assembled.Messages, ctx);
            if (eventEmitter is not null)
                await EmitContextAssemblyAsync(eventEmitter, assembled.Metrics, ctx.TurnIndex);
        }
        else
        {
            var filtered = ContextWindowFilter.Apply(ctx.History, agentCfg.ContextWindow);
            await EmitContextWindowWarnAsync(agentName, agentCfg, filtered, ctx);
            context = !string.IsNullOrWhiteSpace(instructions)
                ? [new ChatMessage(ChatRole.System, instructions), .. filtered]
                : filtered;
        }
        return context;
    }

    private async Task EmitContextWindowWarnAsync(
        string agentName, AgentConfig agentCfg, IReadOnlyList<ChatMessage> filtered, AgentContext ctx)
    {
        if (eventEmitter is null) return;
        if (agentCfg.ContextWindow is not { ContextCapFraction: > 0, MaxTailMessages: > 0 } cw) return;
        if (filtered.Count <= (int)(cw.MaxTailMessages * cw.ContextCapFraction)) return;

        await eventEmitter.EmitAsync(EventTypes.ContextWindowWarn,
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

    private static Task EmitContextAssemblyAsync(
        EventEmitter emitter,
        ContextAssemblyMetrics metrics,
        int turn) =>
        emitter.EmitAsync(EventTypes.ContextAssembly,
            agent: metrics.AgentName,
            turn:  turn,
            payload: new
            {
                knowledge_retrieved  = metrics.KnowledgeItemsRetrieved,
                knowledge_included   = metrics.KnowledgeItemsIncluded,
                memory_loaded        = metrics.MemoryEntriesLoaded,
                memory_included      = metrics.MemoryEntriesIncluded,
                artifacts            = metrics.ArtifactsAssembled,
                context_chars        = metrics.TotalContextChars,
                system_prompt_chars  = metrics.SystemPromptChars,
                assembly_ms          = (int)metrics.AssemblyDuration.TotalMilliseconds,
                context_strategy     = metrics.ContextStrategy,
                declared_sources     = metrics.DeclaredSources,
                empty_sources        = metrics.EmptySources,
            });

    private void RecordGovernanceViolation(
        string agentName,
        string validatorName,
        int consecutiveCount,
        int maxRetries)
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
        if (!governanceKernel.RateLimiter.TryAcquire(rlKey, maxCalls: maxRetries, window: TimeSpan.FromMinutes(10)))
            throw new ValidatorStuckException(agentName, validatorName, consecutiveCount,
                $"Rate limit exceeded for validator failures on agent '{agentName}'.");

        governanceKernel.SloEngine.Get("policy-compliance")?.Record(0.0);
    }

    private async Task EmitAndInjectValidationFailureAsync(
        string agentName,
        string keyword,
        string validatorName,
        string errMsg,
        string responseText,
        int consecutiveFails,
        int maxRetries,
        AgentContext ctx,
        CancellationToken ct)
    {
        if (eventEmitter is not null)
            await eventEmitter.EmitAsync(EventTypes.ValidationFail,
                agent:   agentName,
                payload: new
                {
                    validator   = validatorName,
                    keyword,
                    consecutive = consecutiveFails,
                    message     = errMsg,
                });

        int histBefore = ctx.History.Count;
        await CorrectionEngine.InjectValidationError(ctx.History, errMsg, consecutiveFails, responseText, keyword, eventEmitter, maxRetries);
        await PersistCorrectionsAsync(ctx, histBefore, ct).ConfigureAwait(false);
    }

    private static async Task<(bool ok, string? error, string? validatorName)> RunValidatorsAsync(
        IReadOnlyList<IRoutingValidator> validators,
        IList<ChatMessage> history,
        CancellationToken ct)
    {
        for (int i = 0; i < validators.Count; i++)
        {
            var result = await validators[i].ValidateAsync(history, ct).ConfigureAwait(false);
            if (!result.IsValid)
                return (false, result.ErrorMessage, validators[i].GetType().Name);
        }
        return (true, null, null);
    }

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

            await ctx.MessageSink.WriteAsync(new AgentMessage
            {
                AgentName = AgentNames.Orchestrator,
                Content   = correctionText,
                Role      = "user",
                TurnIndex = Math.Max(0, ctx.TurnIndex - 1),
            }, ct).ConfigureAwait(false);
        }
    }

    // -------------------------------------------------------------------------
    // Route table construction — every edge becomes a Route; no forward/back split.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds per-node route tables from every edge in <paramref name="wfCfg"/>. Unlike
    /// <see cref="GraphOrchestrator.BuildNodeRouteTables"/>, there is no back-edge / phase-break
    /// classification — every edge becomes an ordinary entry in <see cref="AgentRouteTable.Routes"/>,
    /// cyclic or not. Config validation (in <c>OrchestratorBuilder</c>) guarantees every edge has
    /// a non-empty <see cref="GraphEdgeConfig.Keyword"/> before this runs.
    /// </summary>
    internal Dictionary<string, AgentRouteTable> BuildNodeRouteTables(
        GraphConfig wfCfg,
        Dictionary<string, GraphNodeConfig> nodeById)
    {
        var tables = new Dictionary<string, AgentRouteTable>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in wfCfg.Edges)
        {
            if (!tables.TryGetValue(edge.From, out var table))
                tables[edge.From] = table = new AgentRouteTable();

            var sourceNode = nodeById.GetValueOrDefault(edge.From);
            if (edge.SourceAgents is { Count: > 0 } && sourceNode is not null
                && !edge.SourceAgents.Contains(sourceNode.Agent, StringComparer.OrdinalIgnoreCase))
                continue;

            var validators = BuildValidatorsFromNames(
                edge.AllValidators, edge.RequiredCommandPattern, edge.ShellFallbackPattern);

            var targetNode    = nodeById.GetValueOrDefault(edge.To);
            var nextAgentName = targetNode?.Agent ?? edge.To;

            table.Routes[edge.Keyword!] = new RouteInfo(
                edge.To.ToLowerInvariant(),
                nextAgentName,
                validators);
        }

        foreach (var node in wfCfg.Nodes.Where(n => n.Terminal && n.Validators is { Count: > 0 }))
        {
            if (!tables.TryGetValue(node.Id, out var table))
                tables[node.Id] = table = new AgentRouteTable();

            table.TerminalValidators = BuildValidatorsFromNames(node.Validators!);
        }

        // Populate IsReviewerType from the explicit GraphNodeConfig.ReviewerType flag.
        foreach (var node in wfCfg.Nodes.Where(n => n.ReviewerType))
        {
            if (!tables.TryGetValue(node.Id, out var table))
                tables[node.Id] = table = new AgentRouteTable();

            table.IsReviewerType = true;
        }

        // Populate ForeignSendForwardKeywords per node so CorrectionEngine can produce
        // targeted "wrong keyword" messages when an agent emits another node's keyword.
        var allRouteKeywords = tables.Values
            .SelectMany(t => t.Routes.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, table) in tables)
            foreach (var kw in allRouteKeywords)
                if (!table.Routes.ContainsKey(kw))
                    table.ForeignSendForwardKeywords.Add(kw);

        return tables;
    }

    // Shared with GraphOrchestrator via ValidatorRegistry — the two orchestrators resolve
    // per-edge validator names identically; see that class's doc comment for why
    // StrategyFactory.BuildValidators is not folded into the same helper.
    private IReadOnlyList<IRoutingValidator> BuildValidatorsFromNames(
        IReadOnlyList<string> names,
        string? requiredCommandPattern = null,
        string? shellFallbackPattern = null) =>
        ValidatorRegistry.BuildValidatorsFromNames(config, names, requiredCommandPattern, shellFallbackPattern);
}
