using System.Collections.Concurrent;
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
using fuseraft.Infrastructure.Plugins;
using AgentFactory = fuseraft.Infrastructure.AgentFactory;

namespace fuseraft.Orchestration;

/// <summary>
/// Declarative-graph orchestrator. Executes agents as nodes in a directed graph loaded from
/// <c>Selection.Graph</c> in the orchestration config, activated by
/// <c>Selection.Type: "graph"</c>.
///
/// <para>
/// <b>Topology</b>: Forward edges (those that advance the BFS layer order) are compiled into a
/// MAF <see cref="WorkflowBuilder"/> DAG for the current phase. Back-edges (those that return to
/// an earlier BFS layer) terminate the current MAF phase and restart the outer loop from the
/// target node — enabling cycles (e.g. <c>tester → developer</c> on "BUGS FOUND") without
/// violating the MAF acyclic constraint within a single phase.
/// </para>
///
/// <para>
/// <b>Per-node retries</b>: each node executor runs the assigned agent in a retry loop.
/// Keyword detection, validator evaluation, and correction injection use
/// <see cref="fuseraft.Orchestration.Workflow.CorrectionEngine"/> and
/// <see cref="fuseraft.Orchestration.Workflow.KeywordDetector"/>. All middleware
/// (ContextWindow filter, ChangeTracker, GovernanceKernel, SLO recording, EventEmitter)
/// is applied identically across orchestrators.
/// </para>
/// </summary>
public sealed class GraphOrchestrator(
    OrchestrationConfig config,
    AgentFactory agentFactory,
    ILogger<GraphOrchestrator> logger,
    ChangeTracker? changeTracker = null,
    EventEmitter? eventEmitter = null,
    GovernanceKernel? governanceKernel = null,
    IHumanApprovalService? humanApprovalService = null,
    fuseraft.Core.Interfaces.IContextAssemblyPipeline? contextPipeline = null,
    fuseraft.Infrastructure.RepositoryKnowledgeStore? repositoryKnowledgeStore = null) : IOrchestrator
{
    // Default consecutive-failure limit per node. CorrectionEngine uses this same value
    // in its RETRY n/4 messages, so both stay in sync via this constant.
    internal const int DefaultMaxRetries = 4;

    // Sentinel keyword written to AgentContext.LastKeyword when a terminal node completes.
    // The outer loop maps this to a null destination (→ break).
    private const string TerminalSentinel = "__GRAPH_TERMINAL__";

    private readonly IHumanApprovalService? _humanApprovalService = humanApprovalService;

    private string _sessionId = string.Empty;
    private string? _resumeNodeId;
    // Captured from StreamAsync for use in per-node executor helpers.
    private string _task = string.Empty;
    private fuseraft.Core.Models.TaskModel? _structuredTask;

    // Computed once per StreamAsync call from the graph config.
    // Keyed by node ID (case-insensitive).
    private Dictionary<string, int> _nodeLayers = [];
    private Dictionary<string, List<GraphEdgeConfig>> _edgesBySource = [];

    // Back-edge keyword → target node ID (null = terminal / session ends).
    // Populated by BuildNodeRouteTables; reset at the start of each StreamAsync call.
    private Dictionary<string, string?> _backEdgeDestinations =
        new(StringComparer.OrdinalIgnoreCase);

    // Unconditional (no-keyword) routing — wired for nodes whose only outgoing edge(s)
    // carry no keyword. Populated by BuildNodeRouteTables alongside _backEdgeDestinations.
    // Keyed by node ID (case-insensitive).
    private Dictionary<string, RouteInfo>                       _unconditionalForwardRoutes      = [];
    private Dictionary<string, string?>                         _unconditionalBackEdges          = [];
    private Dictionary<string, IReadOnlyList<IRoutingValidator>> _unconditionalBackEdgeValidators = [];

    // Per-session recovery tracking — keyed by "{nodeId}::{keyword}" (forward) or
    // "{nodeId}::{keyword}::back" (back-edge). Each edge activates recovery at most once.
    // ConcurrentDictionary because parallel workers may check/set it simultaneously.
    private ConcurrentDictionary<string, bool> _recoveryActivated = new(StringComparer.OrdinalIgnoreCase);

    // Set of parallel node IDs — populated at the start of each StreamAsync call.
    // Parallel nodes are excluded from the MAF DAG; they are driven by fan-out in RunNodeExecutorAsync.
    private HashSet<string> _parallelNodeIds = new(StringComparer.OrdinalIgnoreCase);

    // Parallel group map: "{sourceNodeId}::{keyword}" → descriptor for the fan-out group.
    // Populated by BuildNodeRouteTables; reset at the start of each StreamAsync call.
    private Dictionary<string, ParallelGroup> _parallelGroups = new(StringComparer.OrdinalIgnoreCase);

    // Per-call caches so RunNodeExecutorAsync can look up node config and route tables
    // for parallel workers without threading the whole graph through parameter lists.
    private Dictionary<string, GraphNodeConfig>   _nodeById            = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, AgentRouteTable>   _routeTablesByNodeId = new(StringComparer.OrdinalIgnoreCase);

    // State history accumulated across all phases of the session.
    private readonly List<AgentState> _stateHistory = [];
    private readonly object _stateHistoryLock = new();

    /// <summary>
    /// Ordered list of <see cref="AgentState"/> snapshots produced during the most recent
    /// <see cref="StreamAsync"/> call. The first entry is the version-0 seed.
    /// </summary>
    public IReadOnlyList<AgentState> StateHistory
    {
        get { lock (_stateHistoryLock) return [.._stateHistory]; }
    }

    // IOrchestrator

    public void SetSessionId(string sessionId)
    {
        _sessionId = sessionId;
        agentFactory.SetSessionId(sessionId);
        contextPipeline?.SetSessionId(sessionId);
    }

    /// <inheritdoc/>
    /// <remarks>Consumed on the next <see cref="StreamAsync"/> call and cleared.</remarks>
    public void SetResumeExecutorId(string? executorId) => _resumeNodeId = executorId;

    /// <inheritdoc/>
    public void SetStructuredTask(fuseraft.Core.Models.TaskModel? model) => _structuredTask = model;

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
            "Session {SessionId} | GraphOrchestrator starting '{Name}' | Task: {TaskPreview}",
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
            logger.LogError(ex, "Session {SessionId} | Failed after {Turns} turns",
                _sessionId, messages.Count);
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
        var graphCfg = config.Selection.Graph
            ?? throw new InvalidOperationException(
                "Selection.Graph must be configured when Selection.Type is 'graph'.");

        if (graphCfg.Nodes.Count == 0)
            throw new InvalidOperationException("Selection.Graph.Nodes must contain at least one node.");

        var channel = Channel.CreateUnbounded<AgentMessage>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        lock (_stateHistoryLock)
        {
            _stateHistory.Clear();
            _stateHistory.Add(AgentState.Initial("session"));
        }

        // Build agents and per-config lookups.
        var agents = config.Agents.ToDictionary(
            a => a.Name,
            a => agentFactory.Create(a, onToolCalling: (agent, tool, args) => ToolCalling?.Invoke(agent, tool, args)),
            StringComparer.OrdinalIgnoreCase);
        var agentInstructions = config.Agents
            .Where(a => !string.IsNullOrWhiteSpace(a.Instructions))
            .ToDictionary(a => a.Name, a => a.Instructions, StringComparer.OrdinalIgnoreCase);
        var agentConfigs = config.Agents.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        var nodeById = graphCfg.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);

        // Compute topology: index edges by source node and BFS layers from entry.
        var entryNodeId = !string.IsNullOrEmpty(graphCfg.EntryNode)
            ? graphCfg.EntryNode
            : graphCfg.Nodes[0].Id;

        _edgesBySource = graphCfg.Edges
            .GroupBy(e => e.From, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        _nodeLayers = ComputeBfsLayers(entryNodeId);

        _parallelNodeIds = graphCfg.Nodes
            .Where(n => n.Parallel)
            .Select(n => n.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _nodeById = nodeById;

        // Build per-node route tables (also populates _backEdgeDestinations, unconditional route maps,
        // and _parallelGroups).
        _backEdgeDestinations            = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [TerminalSentinel] = null };
        _unconditionalForwardRoutes      = new Dictionary<string, RouteInfo>(StringComparer.OrdinalIgnoreCase);
        _unconditionalBackEdges          = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        _unconditionalBackEdgeValidators = new Dictionary<string, IReadOnlyList<IRoutingValidator>>(StringComparer.OrdinalIgnoreCase);
        _parallelGroups                  = new Dictionary<string, ParallelGroup>(StringComparer.OrdinalIgnoreCase);
        _recoveryActivated               = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var routeTables = BuildNodeRouteTables(graphCfg, nodeById);
        _routeTablesByNodeId = routeTables;

        ValidateParallelConfig(graphCfg, nodeById);

        // Build MAF executor bindings (reused across all phases).
        var bindings = BuildExecutorBindings(
            agents, agentInstructions, agentConfigs, routeTables, nodeById);

        // Shared agent context.
        int seedTurn   = priorHistory is { Count: > 0 } ? priorHistory[^1].TurnIndex + 1 : 0;
        int seedTokens = priorHistory?.Sum(m => m.Usage?.TotalTokens ?? 0) ?? 0;

        var agentCtx = new AgentContext
        {
            MessageSink      = channel.Writer,
            TurnIndex        = seedTurn,
            CumulativeTokens = seedTokens,
        };

        if (_structuredTask is { } taskModel)
            agentCtx.History.Add(new ChatMessage(ChatRole.System, taskModel.FormatForContext()));

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

        // Determine the starting node (consume resume hint, then fall back to heuristics).
        var resumeHint = _resumeNodeId;
        _resumeNodeId  = null;
        string startNodeId = DetermineStartNodeId(priorHistory, resumeHint, entryNodeId, graphCfg, nodeById);

        // Inner CTS so the background RunPhasesAsync is always cancelled when the consumer
        // abandons the IAsyncEnumerable (e.g. RunCommand breaks early for compaction).
        using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync("session_start",
                payload: new { task, start_node = startNodeId, resume = priorHistory is { Count: > 0 } });

        var phaseTask = Task.Run(
            () => RunPhasesAsync(bindings, agentCtx, startNodeId, phaseCts.Token),
            phaseCts.Token);

        try
        {
            await foreach (var msg in channel.Reader.ReadAllAsync(phaseCts.Token).ConfigureAwait(false))
                yield return msg;
        }
        finally
        {
            await phaseCts.CancelAsync().ConfigureAwait(false);
        }

        string sessionEndReason = "completed";
        Exception? sessionError = null;
        try
        {
            await phaseTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (phaseCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
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
                await eventEmitter.EmitAsync("session_end",
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
    // Phase loop
    // -------------------------------------------------------------------------

    private async Task RunPhasesAsync(
        Dictionary<string, ExecutorBinding> bindings,
        AgentContext agentCtx,
        string startNodeId,
        CancellationToken ct)
    {
        try
        {
            string currentStart = startNodeId;
            int    phaseCount   = 0;
            // NOTE: ResolveMaxIterations() caps the number of graph *phases* (outer-loop
            // restart cycles), not individual agent turns. A 4-node graph with one back-edge
            // may produce up to 4× agent turns per phase.  Configure MaxIterations accordingly,
            // or rely on terminal nodes (which always end the session unconditionally).
            int maxPhases = config.Termination?.ResolveMaxIterations() is > 0 and var mp
                ? mp
                : int.MaxValue;

            bool naturallyTerminated = false;

            while (phaseCount < maxPhases)
            {
                phaseCount++;
                logger.LogDebug(
                    "[GraphOrchestrator] Phase {Phase}: starting from node '{Start}'",
                    phaseCount, currentStart);

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync("phase_start",
                        payload: new { phase = phaseCount, from = currentStart });

                MafWorkflow workflow   = BuildPhaseWorkflow(bindings, currentStart);
                var         sessionId  = string.IsNullOrEmpty(_sessionId)
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
                    "[GraphOrchestrator] Phase {Phase} ended — LastKeyword='{Keyword}'",
                    phaseCount, lastKeyword ?? "(none)");

                if (lastKeyword is null)
                {
                    naturallyTerminated = true;
                    break; // No keyword — stop to avoid infinite loop.
                }

                if (!_backEdgeDestinations.TryGetValue(lastKeyword, out var nextStart))
                {
                    naturallyTerminated = true;
                    break; // Unknown keyword — stop.
                }

                // Translate synthetic unconditional-back keywords to human-readable form
                // before injecting into agent history or event logs.
                const string UncondBackPrefix = "__UNCOND_BACK:";
                var displayKeyword = lastKeyword.StartsWith(UncondBackPrefix, StringComparison.OrdinalIgnoreCase)
                    ? $"(unconditional handoff from {lastKeyword[UncondBackPrefix.Length..]})"
                    : lastKeyword;

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync("phase_end",
                        payload: new { phase = phaseCount, keyword = displayKeyword, next = nextStart ?? "terminal" });

                if (nextStart is null)
                {
                    naturallyTerminated = true;
                    break; // Terminal node reached — session complete.
                }

                // Inject a phase-transition marker so the next node has explicit context.
                // When a rejection keyword (REVISION REQUIRED, BUGS FOUND, etc.) drives the
                // transition, also embed the prior agent's last diagnostic content so the
                // incoming agent knows WHAT was rejected even after context compression strips
                // earlier turns from its visible window.
                var rejectionContext = ExtractLastAgentFeedback(agentCtx.History, maxChars: 700);
                var phaseTransitionText = rejectionContext is not null
                    ? $"[fuseraft: {displayKeyword} → {nextStart} — new phase]\n\n" +
                      $"Feedback from prior phase:\n{rejectionContext}\n\n" +
                      $"{nextStart}: address the above issues before handing off."
                    : $"[fuseraft: {displayKeyword} → {nextStart} — new phase. {nextStart}: continue from where you left off.]";
                agentCtx.History.Add(new ChatMessage(ChatRole.User, phaseTransitionText));

                agentCtx.LastKeyword = null; // reset for next phase
                currentStart         = nextStart;
            }

            // When the phase cap fires (rather than a natural terminal/break), emit an
            // explanatory message so the session transcript has a clear stopping reason —
            // mirrors the equivalent behaviour in MagenticOrchestrator.
            if (!naturallyTerminated && !ct.IsCancellationRequested && maxPhases != int.MaxValue)
            {
                logger.LogWarning(
                    "[GraphOrchestrator] Session reached maximum of {Max} phases — terminating.",
                    maxPhases);
                await agentCtx.MessageSink.WriteAsync(new AgentMessage
                {
                    AgentName = "orchestrator",
                    Content   =
                        $"The session reached the maximum of {maxPhases} orchestration phases " +
                        "without completing the task. Review the conversation history and consider " +
                        "restarting with a more specific task or a higher Termination.MaxIterations.",
                    Role      = "assistant",
                    TurnIndex = agentCtx.TurnIndex++,
                }, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            agentCtx.MessageSink.TryComplete();
        }
    }

    // -------------------------------------------------------------------------
    // Workflow construction
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds the MAF DAG for the current phase. Starting from <paramref name="startNodeId"/>,
    /// BFS-traverses only forward edges (to nodes with a higher BFS layer than the source).
    /// Back-edges are excluded from the MAF graph — they are handled at runtime by
    /// <c>YieldOutputAsync</c> in the executor, which triggers the outer phase-loop restart.
    /// </summary>
    private MafWorkflow BuildPhaseWorkflow(
        Dictionary<string, ExecutorBinding> bindings,
        string startNodeId)
    {
        if (!bindings.ContainsKey(startNodeId))
            throw new InvalidOperationException(
                $"No executor binding for graph node '{startNodeId}'. " +
                $"Verify that the node's Agent references a defined agent.");

        // BFS over forward edges only to collect this phase's reachable nodes and edges.
        var visited    = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { startNodeId };
        var queue      = new Queue<string>();
        queue.Enqueue(startNodeId);
        var phaseNodes = new List<string> { startNodeId };
        // (from, to) pairs for MAF AddEdge — deduplicated so multi-keyword edges
        // to the same target don't register duplicate MAF edges.
        var addedEdgePairs = new HashSet<(string, string)>();

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in _edgesBySource.GetValueOrDefault(current, []))
            {
                if (IsBackEdge(current, edge.To)) continue;

                if (_parallelNodeIds.Contains(edge.To))
                {
                    // Parallel nodes are excluded from the MAF DAG. Bridge the gap by
                    // adding a virtual edge from the source directly to the merge target,
                    // so the merge-target executor is registered in the workflow and
                    // reachable when the fan-out calls wfCtx.SendMessageAsync.
                    var parallelKey = $"{current}::{edge.Keyword ?? string.Empty}";
                    if (_parallelGroups.TryGetValue(parallelKey, out var pg)
                        && !string.IsNullOrEmpty(pg.MergeTargetId)
                        && !visited.Contains(pg.MergeTargetId))
                    {
                        visited.Add(pg.MergeTargetId);
                        queue.Enqueue(pg.MergeTargetId);
                        phaseNodes.Add(pg.MergeTargetId);
                        addedEdgePairs.Add((current.ToLowerInvariant(), pg.MergeTargetId));
                    }
                    continue;
                }

                addedEdgePairs.Add((current.ToLowerInvariant(), edge.To.ToLowerInvariant()));

                if (!visited.Contains(edge.To))
                {
                    visited.Add(edge.To);
                    queue.Enqueue(edge.To);
                    phaseNodes.Add(edge.To);
                }
            }
        }

        var startBinding = bindings[startNodeId];
        var builder      = new WorkflowBuilder(startBinding);

        foreach (var (from, to) in addedEdgePairs)
        {
            if (bindings.TryGetValue(from, out var fb) && bindings.TryGetValue(to, out var tb))
                builder.AddEdge(fb, tb);
        }

        // All non-start nodes are potential output sources — any of them may call
        // YieldOutputAsync when a back-edge keyword fires or when they are terminal.
        // When the start node is also terminal (single-node graph), it needs to be in
        // WithOutputFrom too; cover that by including all nodes in the set.
        var outputSources = phaseNodes
            .Where(id => bindings.ContainsKey(id))
            .Select(id => bindings[id])
            .ToArray();

        if (outputSources.Length > 0)
            builder.WithOutputFrom(outputSources);

        return builder.Build(false);
    }

    // -------------------------------------------------------------------------
    // Executor binding factory
    // -------------------------------------------------------------------------

    private Dictionary<string, ExecutorBinding> BuildExecutorBindings(
        Dictionary<string, AIAgent> agents,
        Dictionary<string, string> agentInstructions,
        Dictionary<string, AgentConfig> agentConfigs,
        Dictionary<string, AgentRouteTable> routeTables,
        Dictionary<string, GraphNodeConfig> nodeById)
    {
        var bindings = new Dictionary<string, ExecutorBinding>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in config.Selection.Graph!.Nodes)
        {
            if (!agents.ContainsKey(node.Agent))
            {
                logger.LogWarning(
                    "[GraphOrchestrator] Node '{NodeId}' references unknown agent '{Agent}' — skipping.",
                    node.Id, node.Agent);
                continue;
            }

            var routeTable  = routeTables.GetValueOrDefault(node.Id, new AgentRouteTable());
            var agentName   = node.Agent;
            var isTerminal  = node.Terminal;
            var agent       = agents[agentName];
            var instructions = agentInstructions.GetValueOrDefault(agentName, string.Empty);
            var agentCfg    = agentConfigs.GetValueOrDefault(agentName) ?? new AgentConfig();

            Func<AgentContext, IWorkflowContext, CancellationToken, ValueTask> handler =
                async (ctx, wfCtx, ct) =>
                    await RunNodeExecutorAsync(
                        node.Id, agentName, agent, instructions, agentCfg,
                        isTerminal, routeTable, ctx, wfCtx, ct,
                        agents, agentInstructions, agentConfigs).ConfigureAwait(false);

            // Node ID (lowercase) is the executor ID — unique even when multiple nodes
            // share the same agent, which is not possible with agent-name-based IDs.
            var executor = new FunctionExecutor<AgentContext>(
                node.Id.ToLowerInvariant(),
                handler,
                ExecutorOptions.Default,
                [typeof(AgentContext)],   // sends
                [typeof(AgentContext)],   // yields
                false);                   // declareCrossRunShareable

            bindings[node.Id] = executor;
        }

        return bindings;
    }

    // -------------------------------------------------------------------------
    // Per-node executor
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
        Dictionary<string, AIAgent> agents,
        Dictionary<string, string> agentInstructions,
        Dictionary<string, AgentConfig> agentConfigs)
    {
        AgentStarting?.Invoke(agentName);
        agentFactory.OnAgentTurnStarting();
        changeTracker?.BeginTurn(agentName, ctx.TurnIndex);

        int maxRetries      = config.Selection.Graph?.MaxRetries ?? DefaultMaxRetries;
        int maxTotalTurns   = maxRetries * 10;
        int consecutiveFails = 0;
        int totalTurns       = 0;

        while (true)
        {
            if (totalTurns++ >= maxTotalTurns)
                throw new ValidatorStuckException(agentName, "total-turns", totalTurns,
                    $"Node '{nodeId}' ({agentName}) exceeded {maxTotalTurns} total turns without completing.");

            // Assemble context through the unified pipeline (or legacy filter when pipeline is absent).
            IEnumerable<ChatMessage> context;
            if (contextPipeline is not null)
            {
                var assembled = await contextPipeline.AssembleAsync(
                    new fuseraft.Core.Models.AgentExecutionRequest
                    {
                        AgentName     = agentName,
                        Task          = _task,
                        SharedHistory = ctx.History,
                        AgentConfig   = agentCfg,
                        SessionId     = _sessionId,
                    }, ct);
                context = assembled.Messages;
                await EmitContextCapWarningAsync(agentName, agentCfg, assembled.Messages, ctx);
                if (eventEmitter is not null)
                    await EmitContextAssemblyAsync(eventEmitter, assembled.Metrics, ctx.TurnIndex);
            }
            else
            {
                var filtered = ContextWindowFilter.Apply(ctx.History, agentCfg.ContextWindow);
                await EmitContextCapWarningAsync(agentName, agentCfg, filtered, ctx);
                context = !string.IsNullOrWhiteSpace(instructions)
                    ? [new ChatMessage(ChatRole.System, instructions), .. filtered]
                    : filtered;
            }

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync("turn_start", agent: agentName, turn: ctx.TurnIndex);

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
                    await eventEmitter.EmitAsync("turn_timeout",
                        agent:   agentName,
                        payload: new { message = tex.Message, consecutive = consecutiveFails });

                if (consecutiveFails >= maxRetries)
                    throw new ValidatorStuckException(agentName, "streaming-timeout",
                        consecutiveFails, tex.Message);

                ctx.History.Add(new ChatMessage(ChatRole.User,
                    "TIMEOUT: Response timed out. Resume from where you left off — prior tool results are in context. " +
                    "Do not re-research. Call write_file or shell_run now, or emit the handoff keyword if all work is complete.\n\n" +
                    $"Valid keywords: {CorrectionEngine.BuildValidKeywordList(routeTable)}"));
                continue;
            }

            logger.LogDebug(
                "[{Agent}] Node '{NodeId}' turn {Turn} — response: {Preview}",
                agentName, nodeId, totalTurns,
                StringHelpers.Truncate((response.Text ?? "").Replace('\n', ' '), 200));

            var agentMsg = await RecordAndEmitAsync(response, agentName, ctx, ct);

            // responseText is used by both the terminal validator path and keyword detection.
            var responseText = response.Text ?? string.Empty;

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

                consecutiveFails = 0;
                ctx.LastKeyword  = TerminalSentinel;

                ctx.CurrentState = StateHandoff.Advance(ctx.CurrentState, agentName);
                lock (_stateHistoryLock) _stateHistory.Add(ctx.CurrentState);

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync("state_advanced",
                        agent: agentName,
                        turn:  agentMsg.TurnIndex,
                        payload: new { version = ctx.CurrentState.Version, terminal = true });

                await wfCtx.YieldOutputAsync(ctx, ct).ConfigureAwait(false);
                return;
            }

            // Unconditional-only node: skip keyword detection entirely
            // When the node has no keyword-based edges at all, route automatically
            // without requiring the agent to emit any handoff keyword.
            bool hasKeywordRoutes = routeTable.Routes.Count > 0 || routeTable.PhaseBreakKeywords.Count > 0;

            if (!hasKeywordRoutes)
            {
                if (_unconditionalForwardRoutes.TryGetValue(nodeId, out var autoFwdRoute))
                {
                    var (autoOk, autoErr, autoValidator) = await RunValidatorsAsync(
                        autoFwdRoute.Validators, ctx.History, ct).ConfigureAwait(false);

                    if (autoOk)
                    {
                        if (autoFwdRoute.Validators.Count > 0)
                            governanceKernel?.SloEngine.Get("policy-compliance")?.Record(1.0);

                        consecutiveFails = 0;
                        ctx.LastKeyword  = null;

                        if (eventEmitter is not null)
                            await eventEmitter.EmitAsync("agent_routed",
                                agent:   agentName,
                                turn:    agentMsg.TurnIndex,
                                payload: new { keyword = "(unconditional)", to = autoFwdRoute.NextExecutorName });

                        ctx.CurrentState = StateHandoff.Advance(ctx.CurrentState, autoFwdRoute.NextExecutorName);
                        lock (_stateHistoryLock) _stateHistory.Add(ctx.CurrentState);

                        ctx.History.Add(new ChatMessage(ChatRole.User,
                            $"[fuseraft: {agentName} → {autoFwdRoute.NextExecutorName}]"));

                        await wfCtx.SendMessageAsync(ctx, autoFwdRoute.NextExecutorId, ct).ConfigureAwait(false);
                        return;
                    }

                    consecutiveFails = Math.Min(consecutiveFails + 1, maxRetries - 1);
                    RecordGovernanceViolation(agentName, autoValidator!, consecutiveFails, maxRetries);

                    if (consecutiveFails >= maxRetries)
                        throw new ValidatorStuckException(agentName, autoValidator!, consecutiveFails, autoErr!);

                    await EmitAndInjectValidationFailureAsync(
                        agentName, "(unconditional)", autoValidator!, autoErr!, responseText, consecutiveFails, maxRetries, ctx, ct);
                    continue;
                }

                if (_unconditionalBackEdges.TryGetValue(nodeId, out var autoBackDest))
                {
                    if (_unconditionalBackEdgeValidators.TryGetValue(nodeId, out var uncBackValidators)
                        && uncBackValidators.Count > 0)
                    {
                        var (ubOk, ubErr, ubValidator) = await RunValidatorsAsync(
                            uncBackValidators, ctx.History, ct).ConfigureAwait(false);

                        if (!ubOk)
                        {
                            consecutiveFails = Math.Min(consecutiveFails + 1, maxRetries - 1);
                            RecordGovernanceViolation(agentName, ubValidator!, consecutiveFails, maxRetries);

                            if (consecutiveFails >= maxRetries)
                                throw new ValidatorStuckException(agentName, ubValidator!, consecutiveFails, ubErr!);

                            await EmitAndInjectValidationFailureAsync(
                                agentName, "(unconditional-back)", ubValidator!, ubErr!, responseText, consecutiveFails, maxRetries, ctx, ct);
                            continue;
                        }
                    }

                    consecutiveFails = 0;
                    // Use a synthetic keyword so the outer phase loop can look up the destination.
                    ctx.LastKeyword  = $"__UNCOND_BACK:{nodeId.ToLowerInvariant()}";

                    ctx.CurrentState = StateHandoff.Advance(ctx.CurrentState, autoBackDest ?? agentName);
                    lock (_stateHistoryLock) _stateHistory.Add(ctx.CurrentState);

                    if (eventEmitter is not null)
                        await eventEmitter.EmitAsync("state_advanced",
                            agent: agentName,
                            turn:  agentMsg.TurnIndex,
                            payload: new { version = ctx.CurrentState.Version, phase_break = "(unconditional)", next = autoBackDest ?? "(terminal)" });

                    await wfCtx.YieldOutputAsync(ctx, ct).ConfigureAwait(false);
                    return;
                }

                // Node has no keyword edges and no unconditional route wired — config gap.
                // Log and fall through to the correction path so HITL escalation fires normally.
                logger.LogError(
                    "[GraphOrchestrator] Node '{NodeId}' (agent '{Agent}') has no keyword edges " +
                    "and no unconditional route — it can never route. Check the graph config.",
                    nodeId, agentName);
            }

            // Keyword detection

            var handoffArgKeyword = KeywordDetector.ExtractHandoffToolCallKeyword(response.Messages, routeTable);
            var allKeywords       = handoffArgKeyword is not null
                ? (IReadOnlyList<string>)[handoffArgKeyword]
                : KeywordDetector.DetectKeywords(responseText, routeTable);

            // Ambiguous: multiple keywords found.
            if (allKeywords.Count > 1)
            {
                consecutiveFails++;

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync("multi_keyword",
                        agent:   agentName,
                        turn:    agentMsg.TurnIndex,
                        payload: new { keywords = allKeywords, consecutive = consecutiveFails });

                if (consecutiveFails >= maxRetries)
                    throw new ValidatorStuckException(agentName, "multi-keyword", consecutiveFails,
                        $"Node '{nodeId}' emitted multiple routing keywords " +
                        $"({string.Join(", ", allKeywords.Select(k => $"'{k}'"))}) " +
                        $"for {consecutiveFails} consecutive turns.");

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

            // Back-edge keyword (phase-break): validate then yield to restart outer loop.

            if (foundKeyword is not null && routeTable.PhaseBreakKeywords.Contains(foundKeyword))
            {
                // Run per-keyword validators declared on this back-edge (GAP-2).
                if (routeTable.PhaseBreakValidators.TryGetValue(foundKeyword, out var pbValidators)
                    && pbValidators.Count > 0)
                {
                    var (pbOk, pbErr, pbValidator) = await RunValidatorsAsync(
                        pbValidators, ctx.History, ct).ConfigureAwait(false);

                    if (!pbOk)
                    {
                        consecutiveFails = Math.Min(consecutiveFails + 1, maxRetries - 1);
                        RecordGovernanceViolation(agentName, pbValidator!, consecutiveFails, maxRetries);

                        if (consecutiveFails >= maxRetries)
                            throw new ValidatorStuckException(agentName, pbValidator!, consecutiveFails, pbErr!);

                        // Recovery agent for back-edge validator failures.
                        var backEdgeKey = $"{nodeId}::{foundKeyword}::back";
                        if (consecutiveFails >= 2
                            && routeTable.PhaseBreakRecoveryAgents.TryGetValue(foundKeyword, out var backRecoveryName)
                            && !_recoveryActivated.ContainsKey(backEdgeKey)
                            && agents.TryGetValue(backRecoveryName, out var backRecoveryAgt))
                        {
                            _recoveryActivated.TryAdd(backEdgeKey, true);
                            await InvokeRecoveryAgentAsync(
                                backRecoveryName, backRecoveryAgt,
                                agentInstructions, agentConfigs,
                                $"'{pbValidator}' failed {consecutiveFails}× on back-edge '{foundKeyword}'",
                                pbErr!, foundKeyword, ctx, ct);
                            consecutiveFails = 0;
                            continue;
                        }

                        await EmitAndInjectValidationFailureAsync(
                            agentName, foundKeyword, pbValidator!, pbErr!, responseText, consecutiveFails, maxRetries, ctx, ct);
                        continue;
                    }
                }

                // Human approval gate for back-edges.
                if (routeTable.PhaseBreakRequireHumanApproval.Contains(foundKeyword)
                    && _humanApprovalService is not null)
                {
                    var backTarget = _backEdgeDestinations.TryGetValue(foundKeyword, out var pbd0)
                        ? pbd0 ?? "(terminal)"
                        : "(terminal)";
                    var approved = await _humanApprovalService.PromptRouteApprovalAsync(
                        foundKeyword, agentName, backTarget);
                    if (!approved)
                    {
                        ctx.History.Add(new ChatMessage(ChatRole.User,
                            $"Phase-break to '{backTarget}' was blocked by the operator. " +
                            $"Continue your work or await further instructions."));
                        consecutiveFails = 0;
                        int histBeforePbBlocked = ctx.History.Count - 1;
                        await PersistCorrectionsAsync(ctx, histBeforePbBlocked, ct).ConfigureAwait(false);
                        continue;
                    }
                }

                consecutiveFails = 0;
                ctx.LastKeyword  = foundKeyword;

                var backEdgeDest = _backEdgeDestinations.TryGetValue(foundKeyword, out var pbd) ? pbd : null;
                ctx.CurrentState = StateHandoff.Advance(ctx.CurrentState, backEdgeDest ?? agentName);
                lock (_stateHistoryLock) _stateHistory.Add(ctx.CurrentState);

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync("state_advanced",
                        agent: agentName,
                        turn:  agentMsg.TurnIndex,
                        payload: new { version = ctx.CurrentState.Version, phase_break = foundKeyword, next = backEdgeDest ?? "(terminal)" });

                await wfCtx.YieldOutputAsync(ctx, ct).ConfigureAwait(false);
                return;
            }

            // Parallel fan-out keyword

            var pgKey = $"{nodeId}::{foundKeyword}";
            if (foundKeyword is not null && _parallelGroups.TryGetValue(pgKey, out var parallelGroup))
            {
                var (pgOk, pgErr, pgValidator) = await RunValidatorsAsync(
                    parallelGroup.Validators, ctx.History, ct).ConfigureAwait(false);

                if (!pgOk)
                {
                    consecutiveFails = Math.Min(consecutiveFails + 1, maxRetries - 1);
                    RecordGovernanceViolation(agentName, pgValidator!, consecutiveFails, maxRetries);

                    if (consecutiveFails >= maxRetries)
                        throw new ValidatorStuckException(agentName, pgValidator!, consecutiveFails, pgErr!);

                    await EmitAndInjectValidationFailureAsync(
                        agentName, foundKeyword, pgValidator!, pgErr!, responseText, consecutiveFails, maxRetries, ctx, ct);
                    continue;
                }

                if (parallelGroup.RequireHumanApproval && _humanApprovalService is not null)
                {
                    var approved = await _humanApprovalService.PromptRouteApprovalAsync(
                        foundKeyword, agentName, parallelGroup.MergeTargetName);
                    if (!approved)
                    {
                        ctx.History.Add(new ChatMessage(ChatRole.User,
                            $"Parallel dispatch to [{string.Join(", ", parallelGroup.NodeIds)}] was blocked by the operator. " +
                            $"Continue your work or await further instructions."));
                        consecutiveFails = 0;
                        await PersistCorrectionsAsync(ctx, ctx.History.Count - 1, ct).ConfigureAwait(false);
                        continue;
                    }
                }

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync("parallel_start",
                        agent:   agentName,
                        payload: new { keyword = foundKeyword, nodes = parallelGroup.NodeIds, merge_target = parallelGroup.MergeTargetName });

                int forkPoint = ctx.History.Count;
                var forkPairs = parallelGroup.NodeIds.Select(targetNodeId =>
                {
                    var targetNode      = _nodeById[targetNodeId];
                    var targetAgentName = targetNode.Agent;
                    return (
                        NodeId:       targetNodeId,
                        AgentName:    targetAgentName,
                        Agent:        agents[targetAgentName],
                        Instructions: agentInstructions.GetValueOrDefault(targetAgentName, string.Empty),
                        AgentCfg:     agentConfigs.GetValueOrDefault(targetAgentName) ?? new AgentConfig(),
                        RouteTable:   _routeTablesByNodeId.GetValueOrDefault(targetNodeId, new AgentRouteTable()),
                        Fork:         ForkContext(ctx));
                }).ToList();

                var parallelTasks = forkPairs
                    .Select(fp => RunParallelNodeAsync(
                        fp.NodeId, fp.AgentName, fp.Agent, fp.Instructions, fp.AgentCfg,
                        fp.RouteTable, fp.Fork, ct, agents, agentInstructions, agentConfigs))
                    .ToArray();

                await Task.WhenAll(parallelTasks).ConfigureAwait(false);

                MergeParallelContexts(ctx, forkPoint,
                    forkPairs.Select(fp => (fp.NodeId, fp.AgentName, fp.Fork)).ToList());

                consecutiveFails = 0;
                ctx.LastKeyword  = foundKeyword;

                ctx.CurrentState = StateHandoff.Advance(ctx.CurrentState, parallelGroup.MergeTargetName);
                lock (_stateHistoryLock) _stateHistory.Add(ctx.CurrentState);

                if (eventEmitter is not null)
                {
                    await eventEmitter.EmitAsync("parallel_merge",
                        agent:   agentName,
                        payload: new { keyword = foundKeyword, to = parallelGroup.MergeTargetName });

                    await eventEmitter.EmitAsync("state_advanced",
                        agent: agentName,
                        turn:  agentMsg.TurnIndex,
                        payload: new { version = ctx.CurrentState.Version, parallel_merge = true, to = parallelGroup.MergeTargetName });
                }

                ctx.History.Add(new ChatMessage(ChatRole.User,
                    $"[fuseraft: parallel workers complete → {parallelGroup.MergeTargetName}]"));

                await wfCtx.SendMessageAsync(ctx, parallelGroup.MergeTargetId, ct).ConfigureAwait(false);
                return;
            }

            // Forward-edge keyword: validate and route.

            if (foundKeyword is not null && routeTable.Routes.TryGetValue(foundKeyword, out var route))
            {
                var (ok, errMsg, failingValidator) = await RunValidatorsAsync(
                    route.Validators, ctx.History, ct).ConfigureAwait(false);

                if (ok)
                {
                    if (route.Validators.Count > 0)
                        governanceKernel?.SloEngine.Get("policy-compliance")?.Record(1.0);

                    // Human approval gate: prompt before the route fires.
                    if (route.RequireHumanApproval && _humanApprovalService is not null)
                    {
                        var approved = await _humanApprovalService.PromptRouteApprovalAsync(
                            foundKeyword, agentName, route.NextExecutorName);
                        if (!approved)
                        {
                            ctx.History.Add(new ChatMessage(ChatRole.User,
                                $"Route to {route.NextExecutorName} was blocked by the operator. " +
                                $"Continue your work or await further instructions."));
                            consecutiveFails = 0;
                            int histBeforeBlocked = ctx.History.Count - 1;
                            await PersistCorrectionsAsync(ctx, histBeforeBlocked, ct).ConfigureAwait(false);
                            continue;
                        }
                    }

                    consecutiveFails = 0;
                    ctx.LastKeyword  = foundKeyword;

                    if (eventEmitter is not null)
                        await eventEmitter.EmitAsync("agent_routed",
                            agent:   agentName,
                            turn:    agentMsg.TurnIndex,
                            payload: new { keyword = foundKeyword, to = route.NextExecutorName });

                    ctx.CurrentState = StateHandoff.Advance(ctx.CurrentState, route.NextExecutorName);
                    lock (_stateHistoryLock) _stateHistory.Add(ctx.CurrentState);
                    if (eventEmitter is not null)
                        await eventEmitter.EmitAsync("state_advanced",
                            agent: agentName,
                            turn:  agentMsg.TurnIndex,
                            payload: new { version = ctx.CurrentState.Version, to = route.NextExecutorName });

                    ctx.History.Add(new ChatMessage(ChatRole.User,
                        $"[fuseraft: {agentName} → {route.NextExecutorName}]"));

                    await wfCtx.SendMessageAsync(ctx, route.NextExecutorId, ct).ConfigureAwait(false);
                    return;
                }

                // Validator failed — clamp to maxRetries-1 so a single keyword find is not
                // penalised as heavily as a missing keyword before injecting correction.
                consecutiveFails = Math.Min(consecutiveFails + 1, maxRetries - 1);
                RecordGovernanceViolation(agentName, failingValidator!, consecutiveFails, maxRetries);

                if (consecutiveFails >= maxRetries)
                    throw new ValidatorStuckException(agentName, failingValidator!, consecutiveFails, errMsg!);

                // Recovery agent: activate on >= 2 consecutive failures, at most once per edge.
                var fwdEdgeKey = $"{nodeId}::{foundKeyword}";
                if (consecutiveFails >= 2
                    && route.RecoveryAgent is not null
                    && !_recoveryActivated.ContainsKey(fwdEdgeKey)
                    && agents.TryGetValue(route.RecoveryAgent, out var fwdRecoveryAgt))
                {
                    _recoveryActivated.TryAdd(fwdEdgeKey, true);
                    await InvokeRecoveryAgentAsync(
                        route.RecoveryAgent, fwdRecoveryAgt,
                        agentInstructions, agentConfigs,
                        $"'{failingValidator}' failed {consecutiveFails}× on edge '{foundKeyword}'",
                        errMsg!, foundKeyword, ctx, ct);
                    consecutiveFails = 0;
                    continue;
                }

                await EmitAndInjectValidationFailureAsync(
                    agentName, foundKeyword, failingValidator!, errMsg!, responseText, consecutiveFails, maxRetries, ctx, ct);
                continue;
            }

            // No keyword matched.

            consecutiveFails++;

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync("no_keyword",
                    agent:   agentName,
                    turn:    agentMsg.TurnIndex,
                    payload: new { consecutive = consecutiveFails });

            int histBefore2 = ctx.History.Count;
            await CorrectionEngine.InjectNoKeywordCorrection(
                ctx.History, responseText, agentName, consecutiveFails, routeTable, eventEmitter,
                agentMsg.ToolCalls);
            await PersistCorrectionsAsync(ctx, histBefore2, ct).ConfigureAwait(false);

            if (consecutiveFails >= maxRetries)
                throw new ValidatorStuckException(agentName, "no-keyword", consecutiveFails,
                    $"Node '{nodeId}' ({agentName}) emitted no routing keyword " +
                    $"for {consecutiveFails} consecutive turns.");
        }
    }

    // -------------------------------------------------------------------------
    // Shared per-turn helpers
    // -------------------------------------------------------------------------

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
            await eventEmitter.EmitAsync("turn_end",
                agent: agentName,
                turn:  agentMsg.TurnIndex,
                payload: new
                {
                    input_tokens  = agentMsg.Usage?.InputTokens,
                    output_tokens = agentMsg.Usage?.OutputTokens,
                }).ConfigureAwait(false);

        // Emit reasoning content when the model produced any.
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
            try { await changeTracker.FlushTurnAsync(agentName, agentMsg.TurnIndex, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "ChangeTracker flush failed for turn {Turn} ({Agent})",
                    agentMsg.TurnIndex, agentName);
            }
        }

        // Persist entity-scoped findings from tool calls for future session retrieval.
        if (repositoryKnowledgeStore is not null && !string.IsNullOrEmpty(_sessionId))
        {
            try
            {
                var observations = ObservationExtractor.Extract(
                    (IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>)response.Messages,
                    agentName, agentMsg.TurnIndex);
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
                    }, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch { /* best-effort */ }
        }

        return agentMsg;
    }

    private static Task EmitContextAssemblyAsync(
        EventEmitter emitter,
        fuseraft.Core.Models.ContextAssemblyMetrics metrics,
        int turn) =>
        emitter.EmitAsync("context_assembly",
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
            });

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
                AgentName = "orchestrator",
                Content   = correctionText,
                Role      = "user",
                TurnIndex = Math.Max(0, ctx.TurnIndex - 1),
            }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Invokes a recovery agent for one intervention turn and appends its response to
    /// shared history. Best-effort — exceptions are swallowed so the caller's retry loop
    /// continues normally even when the recovery agent itself fails.
    /// </summary>
    private async Task InvokeRecoveryAgentAsync(
        string recoveryAgentName,
        AIAgent recoveryAgent,
        Dictionary<string, string> agentInstructions,
        Dictionary<string, AgentConfig> agentConfigs,
        string reason,
        string validatorError,
        string triggeringKeyword,
        AgentContext ctx,
        CancellationToken ct)
    {
        var recoveryCfg = agentConfigs.GetValueOrDefault(recoveryAgentName) ?? new AgentConfig();
        var recoveryInstructions = agentInstructions.GetValueOrDefault(recoveryAgentName, string.Empty);

        ctx.History.Add(new ChatMessage(ChatRole.User,
            $"RECOVERY ACTIVATED: '{recoveryAgentName}' called in — {reason}.\n\n" +
            $"  1. changes_read_latest — review what was attempted.\n" +
            $"  2. Fix the problem described below.\n" +
            $"  3. The pipeline will retry '{triggeringKeyword}' after this turn.\n\n" +
            $"Failure: {validatorError}"));

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync("recovery_activated",
                agent: recoveryAgentName,
                payload: new { reason, keyword = triggeringKeyword });

        try
        {
            IEnumerable<ChatMessage> context;
            if (contextPipeline is not null)
            {
                var assembled = await contextPipeline.AssembleAsync(
                    new fuseraft.Core.Models.AgentExecutionRequest
                    {
                        AgentName     = recoveryAgentName,
                        Task          = _task,
                        SharedHistory = ctx.History,
                        AgentConfig   = recoveryCfg,
                        SessionId     = _sessionId,
                    }, ct);
                context = assembled.Messages;
                if (eventEmitter is not null)
                    await EmitContextAssemblyAsync(eventEmitter, assembled.Metrics, ctx.TurnIndex);
            }
            else
            {
                var filtered = ContextWindowFilter.Apply(ctx.History, recoveryCfg.ContextWindow);
                context = !string.IsNullOrWhiteSpace(recoveryInstructions)
                    ? [new ChatMessage(ChatRole.System, recoveryInstructions), .. filtered]
                    : filtered;
            }

            var response = governanceKernel?.CircuitBreaker is { } cb
                ? await cb.ExecuteAsync(() => recoveryAgent.RunAsync(context, null, null, ct)).ConfigureAwait(false)
                : await recoveryAgent.RunAsync(context, null, null, ct).ConfigureAwait(false);

            await RecordAndEmitAsync(response, recoveryAgentName, ctx, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[GraphOrchestrator] Recovery agent '{Agent}' failed — continuing normal pipeline.",
                recoveryAgentName);
        }
    }

    // -------------------------------------------------------------------------
    // Validation-failure helpers (shared by RunNodeExecutorAsync / RunParallelNodeAsync)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Emits a <c>context_cap_warning</c> event when the filtered message count is
    /// approaching the configured context-cap fraction. No-ops when
    /// <paramref name="eventEmitter"/> is null or the context window is not configured.
    /// </summary>
    private async Task EmitContextCapWarningAsync(
        string agentName, AgentConfig agentCfg, IReadOnlyList<ChatMessage> filtered, AgentContext ctx)
    {
        if (eventEmitter is null) return;
        if (agentCfg.ContextWindow is not { ContextCapFraction: > 0, MaxTailMessages: > 0 } cw) return;
        if (filtered.Count <= (int)(cw.MaxTailMessages * cw.ContextCapFraction)) return;

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

    /// <summary>
    /// Emits a <c>validation_fail</c> event, injects a correction message into history via
    /// <see cref="CorrectionEngine.InjectValidationError"/>, and persists the injected message
    /// to the message sink. Called from every validation-failure path in the turn loop.
    /// </summary>
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
            await eventEmitter.EmitAsync("validation_fail",
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

    // -------------------------------------------------------------------------
    // Parallel fan-out helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Executes a single parallel node's agent retry loop against an isolated fork of the
    /// shared <see cref="AgentContext"/>. Unlike <see cref="RunNodeExecutorAsync"/>, this
    /// method does not call <c>wfCtx.SendMessageAsync</c> or <c>YieldOutputAsync</c> —
    /// it simply returns when the agent emits a valid forward-edge keyword, leaving the
    /// routing decision to the parent fan-out that called it.
    /// </summary>
    private async Task RunParallelNodeAsync(
        string nodeId,
        string agentName,
        AIAgent agent,
        string instructions,
        AgentConfig agentCfg,
        AgentRouteTable routeTable,
        AgentContext ctx,
        CancellationToken ct,
        Dictionary<string, AIAgent> agents,
        Dictionary<string, string> agentInstructions,
        Dictionary<string, AgentConfig> agentConfigs)
    {
        AgentStarting?.Invoke(agentName);
        agentFactory.OnAgentTurnStarting();

        int maxRetries       = config.Selection.Graph?.MaxRetries ?? DefaultMaxRetries;
        int maxTotalTurns    = maxRetries * 10;
        int consecutiveFails = 0;
        int totalTurns       = 0;

        while (true)
        {
            if (totalTurns++ >= maxTotalTurns)
                throw new ValidatorStuckException(agentName, "total-turns", totalTurns,
                    $"Parallel node '{nodeId}' ({agentName}) exceeded {maxTotalTurns} total turns without completing.");

            IEnumerable<ChatMessage> context;
            if (contextPipeline is not null)
            {
                var assembled = await contextPipeline.AssembleAsync(
                    new fuseraft.Core.Models.AgentExecutionRequest
                    {
                        AgentName     = agentName,
                        Task          = _task,
                        SharedHistory = ctx.History,
                        AgentConfig   = agentCfg,
                        SessionId     = _sessionId,
                    }, ct);
                context = assembled.Messages;
                await EmitContextCapWarningAsync(agentName, agentCfg, assembled.Messages, ctx);
                if (eventEmitter is not null)
                    await EmitContextAssemblyAsync(eventEmitter, assembled.Metrics, ctx.TurnIndex);
            }
            else
            {
                var filtered = ContextWindowFilter.Apply(ctx.History, agentCfg.ContextWindow);
                await EmitContextCapWarningAsync(agentName, agentCfg, filtered, ctx);
                context = !string.IsNullOrWhiteSpace(instructions)
                    ? [new ChatMessage(ChatRole.System, instructions), .. filtered]
                    : filtered;
            }

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync("turn_start", agent: agentName, turn: ctx.TurnIndex);

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
                    await eventEmitter.EmitAsync("turn_timeout",
                        agent:   agentName,
                        payload: new { message = tex.Message, consecutive = consecutiveFails });

                if (consecutiveFails >= maxRetries)
                    throw new ValidatorStuckException(agentName, "streaming-timeout",
                        consecutiveFails, tex.Message);

                ctx.History.Add(new ChatMessage(ChatRole.User,
                    "TIMEOUT: Response timed out. Resume from where you left off — prior tool results are in context. " +
                    "Do not re-research. Call write_file or shell_run now, or emit the handoff keyword if all work is complete.\n\n" +
                    $"Valid keywords: {CorrectionEngine.BuildValidKeywordList(routeTable)}"));
                continue;
            }

            logger.LogDebug(
                "[{Agent}] Parallel node '{NodeId}' turn {Turn} — response: {Preview}",
                agentName, nodeId, totalTurns,
                StringHelpers.Truncate((response.Text ?? "").Replace('\n', ' '), 200));

            var agentMsg    = await RecordAndEmitAsync(response, agentName, ctx, ct);
            var responseText = response.Text ?? string.Empty;

            var handoffArgKeyword = KeywordDetector.ExtractHandoffToolCallKeyword(response.Messages, routeTable);
            var allKeywords       = handoffArgKeyword is not null
                ? (IReadOnlyList<string>)[handoffArgKeyword]
                : KeywordDetector.DetectKeywords(responseText, routeTable);

            if (allKeywords.Count > 1)
            {
                consecutiveFails++;

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync("multi_keyword",
                        agent:   agentName,
                        turn:    agentMsg.TurnIndex,
                        payload: new { keywords = allKeywords, consecutive = consecutiveFails });

                if (consecutiveFails >= maxRetries)
                    throw new ValidatorStuckException(agentName, "multi-keyword", consecutiveFails,
                        $"Parallel node '{nodeId}' emitted multiple routing keywords " +
                        $"({string.Join(", ", allKeywords.Select(k => $"'{k}'"))}) " +
                        $"for {consecutiveFails} consecutive turns.");

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
                    payload: new { keyword = foundKeyword, parallel = true });

            // Back-edge keywords from parallel nodes are a config error — treat as no keyword.
            if (foundKeyword is not null && routeTable.PhaseBreakKeywords.Contains(foundKeyword))
            {
                logger.LogError(
                    "[GraphOrchestrator] Parallel node '{NodeId}' emitted back-edge keyword '{Kw}' — " +
                    "back-edges from parallel nodes are not supported. Treating as no-keyword.",
                    nodeId, foundKeyword);
                foundKeyword = null;
            }

            if (foundKeyword is not null && routeTable.Routes.TryGetValue(foundKeyword, out var route))
            {
                var (ok, errMsg, failingValidator) = await RunValidatorsAsync(
                    route.Validators, ctx.History, ct).ConfigureAwait(false);

                if (ok)
                {
                    if (route.Validators.Count > 0)
                        governanceKernel?.SloEngine.Get("policy-compliance")?.Record(1.0);

                    consecutiveFails = 0;
                    ctx.LastKeyword  = foundKeyword;

                    if (eventEmitter is not null)
                        await eventEmitter.EmitAsync("agent_routed",
                            agent:   agentName,
                            turn:    agentMsg.TurnIndex,
                            payload: new { keyword = foundKeyword, to = route.NextExecutorName, parallel = true });

                    return; // fan-out complete for this worker; parent merges results
                }

                consecutiveFails = Math.Min(consecutiveFails + 1, maxRetries - 1);
                RecordGovernanceViolation(agentName, failingValidator!, consecutiveFails, maxRetries);

                if (consecutiveFails >= maxRetries)
                    throw new ValidatorStuckException(agentName, failingValidator!, consecutiveFails, errMsg!);

                var fwdEdgeKey = $"{nodeId}::{foundKeyword}::parallel";
                if (consecutiveFails >= 2
                    && route.RecoveryAgent is not null
                    && !_recoveryActivated.ContainsKey(fwdEdgeKey)
                    && agents.TryGetValue(route.RecoveryAgent, out var fwdRecoveryAgt))
                {
                    _recoveryActivated.TryAdd(fwdEdgeKey, true);
                    await InvokeRecoveryAgentAsync(
                        route.RecoveryAgent, fwdRecoveryAgt,
                        agentInstructions, agentConfigs,
                        $"'{failingValidator}' failed {consecutiveFails}× on edge '{foundKeyword}'",
                        errMsg!, foundKeyword, ctx, ct);
                    consecutiveFails = 0;
                    continue;
                }

                await EmitAndInjectValidationFailureAsync(
                    agentName, foundKeyword, failingValidator!, errMsg!, responseText, consecutiveFails, maxRetries, ctx, ct);
                continue;
            }

            // No keyword matched.
            consecutiveFails++;

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync("no_keyword",
                    agent:   agentName,
                    turn:    agentMsg.TurnIndex,
                    payload: new { consecutive = consecutiveFails });

            int histBefore2 = ctx.History.Count;
            await CorrectionEngine.InjectNoKeywordCorrection(
                ctx.History, responseText, agentName, consecutiveFails, routeTable, eventEmitter,
                agentMsg.ToolCalls);
            await PersistCorrectionsAsync(ctx, histBefore2, ct).ConfigureAwait(false);

            if (consecutiveFails >= maxRetries)
                throw new ValidatorStuckException(agentName, "no-keyword", consecutiveFails,
                    $"Parallel node '{nodeId}' ({agentName}) emitted no routing keyword " +
                    $"for {consecutiveFails} consecutive turns.");
        }
    }

    /// <summary>
    /// Creates an isolated <see cref="AgentContext"/> snapshot for a parallel worker.
    /// The fork shares the same <see cref="AgentContext.MessageSink"/> (already thread-safe)
    /// but gets its own <see cref="AgentContext.History"/> copy so concurrent workers cannot
    /// corrupt each other's conversation state.
    /// </summary>
    internal static AgentContext ForkContext(AgentContext parent)
    {
        var fork = new AgentContext
        {
            MessageSink      = parent.MessageSink,
            TurnIndex        = parent.TurnIndex,
            CumulativeTokens = parent.CumulativeTokens,
            CurrentState     = parent.CurrentState,
        };
        fork.History.AddRange(parent.History);
        return fork;
    }

    /// <summary>
    /// Merges the post-fork output of each parallel worker back into the parent context.
    /// For each child, a labelled header is injected followed by all messages appended
    /// after <paramref name="forkPoint"/>. Token counts and turn indices are aggregated.
    /// </summary>
    internal static void MergeParallelContexts(
        AgentContext parent,
        int forkPoint,
        IReadOnlyList<(string NodeId, string AgentName, AgentContext Fork)> children)
    {
        int maxTurnIndex    = parent.TurnIndex;
        int totalTokenDelta = 0;

        foreach (var (nodeId, agentName, fork) in children)
        {
            totalTokenDelta += fork.CumulativeTokens - parent.CumulativeTokens;
            maxTurnIndex     = Math.Max(maxTurnIndex, fork.TurnIndex);

            parent.History.Add(new ChatMessage(ChatRole.User,
                $"[fuseraft: parallel result from {agentName} (node: {nodeId})]"));

            for (int i = forkPoint; i < fork.History.Count; i++)
                parent.History.Add(fork.History[i]);
        }

        parent.CumulativeTokens += Math.Max(0, totalTokenDelta);
        parent.TurnIndex         = maxTurnIndex;
    }

    /// <summary>Descriptor for a parallel fan-out group triggered by a single source keyword.</summary>
    private sealed class ParallelGroup
    {
        public List<string>                    NodeIds              { get; }       = new();
        public string                          MergeTargetId        { get; set; }  = string.Empty;
        public string                          MergeTargetName      { get; set; }  = string.Empty;
        public IReadOnlyList<IRoutingValidator> Validators          { get; set; }  = [];
        public bool                            RequireHumanApproval { get; set; }
    }

    // -------------------------------------------------------------------------
    // Route table construction
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds per-node <see cref="AgentRouteTable"/> instances from the graph edge and node config.
    /// <list type="bullet">
    ///   <item>Forward edges → <c>Routes</c> (send-forward, keyword-triggered).</item>
    ///   <item>Back-edges → <c>PhaseBreakKeywords</c> + <c>PhaseBreakValidators</c>.</item>
    ///   <item>Terminal nodes → <c>TerminalValidators</c> from <see cref="GraphNodeConfig.Validators"/>.</item>
    /// </list>
    /// Also populates <see cref="_backEdgeDestinations"/> for the outer phase loop.
    /// </summary>
    private Dictionary<string, AgentRouteTable> BuildNodeRouteTables(
        GraphConfig graphCfg,
        Dictionary<string, GraphNodeConfig> nodeById)
    {
        var tables = new Dictionary<string, AgentRouteTable>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in graphCfg.Edges)
        {
            if (!tables.TryGetValue(edge.From, out var table))
                tables[edge.From] = table = new AgentRouteTable();

            var validators = BuildValidatorsFromNames(
                edge.AllValidators,
                edge.RequiredCommandPattern,
                edge.ShellFallbackPattern);

            // SourceAgents: skip this entry if the source node's agent is not in the allowed list.
            var sourceNode = nodeById.GetValueOrDefault(edge.From);
            if (edge.SourceAgents is { Count: > 0 } && sourceNode is not null
                && !edge.SourceAgents.Contains(sourceNode.Agent, StringComparer.OrdinalIgnoreCase))
                continue;

            if (IsBackEdge(edge.From, edge.To))
            {
                // Back-edge: fires as a phase-break via YieldOutputAsync.
                if (edge.Keyword is { Length: > 0 })
                {
                    table.PhaseBreakKeywords.Add(edge.Keyword);

                    if (validators.Count > 0)
                        table.PhaseBreakValidators[edge.Keyword] = validators;

                    if (edge.RequireHumanApproval)
                        table.PhaseBreakRequireHumanApproval.Add(edge.Keyword);

                    if (edge.RecoveryAgent is not null)
                        table.PhaseBreakRecoveryAgents[edge.Keyword] = edge.RecoveryAgent;

                    // Register destination for the outer phase loop (first-registered wins
                    // when multiple back-edges share the same keyword to different targets).
                    if (!_backEdgeDestinations.ContainsKey(edge.Keyword))
                        _backEdgeDestinations[edge.Keyword] = edge.To.ToLowerInvariant();
                }
            }
            else
            {
                // Forward edge: fires via SendMessageAsync(ctx, targetNodeId).
                if (edge.Keyword is { Length: > 0 })
                {
                    var targetNode = nodeById.GetValueOrDefault(edge.To);

                    if (targetNode?.Parallel == true)
                    {
                        // Parallel fan-out: accumulate this target into the group for
                        // (source, keyword). Multiple edges with the same keyword and
                        // Parallel targets form one concurrent group.
                        var groupKey = $"{edge.From}::{edge.Keyword}";
                        if (!_parallelGroups.TryGetValue(groupKey, out var pg))
                            _parallelGroups[groupKey] = pg = new ParallelGroup
                            {
                                Validators           = validators,
                                RequireHumanApproval = edge.RequireHumanApproval,
                            };
                        pg.NodeIds.Add(edge.To.ToLowerInvariant());
                        table.ParallelKeywords.Add(edge.Keyword);
                    }
                    else
                    {
                        var nextAgentName = targetNode?.Agent ?? edge.To;
                        table.Routes[edge.Keyword] = new RouteInfo(
                            edge.To.ToLowerInvariant(),
                            nextAgentName,
                            validators,
                            edge.RequireHumanApproval,
                            edge.RecoveryAgent);
                    }
                }
            }
        }

        // Populate TerminalValidators for terminal nodes from GraphNodeConfig.Validators.
        foreach (var node in graphCfg.Nodes.Where(n => n.Terminal && n.Validators is { Count: > 0 }))
        {
            if (!tables.TryGetValue(node.Id, out var table))
                tables[node.Id] = table = new AgentRouteTable();

            table.TerminalValidators = BuildValidatorsFromNames(node.Validators!);
        }

        // Populate ForeignSendForwardKeywords per node so CorrectionEngine can produce
        // targeted "wrong keyword" messages when an agent emits another node's keyword.
        // Includes both forward-route keywords AND back-edge phase-break keywords so agents
        // emitting a foreign phase-break keyword get a targeted correction, not just "no keyword".
        var allRouteKeywords = tables.Values
            .SelectMany(t => t.Routes.Keys.Concat(t.PhaseBreakKeywords))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, table) in tables)
            foreach (var kw in allRouteKeywords)
                if (!table.Routes.ContainsKey(kw) && !table.PhaseBreakKeywords.Contains(kw))
                    table.ForeignSendForwardKeywords.Add(kw);

        // Resolve merge targets for parallel groups from the parallel nodes' own route tables.
        // The merge target is the first forward-route destination found in any of the group's nodes.
        foreach (var (groupKey, pg) in _parallelGroups)
        {
            foreach (var pNodeId in pg.NodeIds)
            {
                if (!tables.TryGetValue(pNodeId, out var pTable)) continue;
                var firstFwdRoute = pTable.Routes.Values.FirstOrDefault();
                if (firstFwdRoute is null) continue;
                pg.MergeTargetId   = firstFwdRoute.NextExecutorId;
                pg.MergeTargetName = firstFwdRoute.NextExecutorName;
                break;
            }

            if (string.IsNullOrEmpty(pg.MergeTargetId))
                logger.LogWarning(
                    "[GraphOrchestrator] Parallel group '{Key}' has no merge target — " +
                    "each parallel node must have at least one forward edge to the merge-target node.",
                    groupKey);
        }

        // Populate unconditional routing for nodes whose ALL outgoing edges carry no keyword.
        // A node qualifies when it has exactly one no-keyword edge and zero keyword-based edges.
        foreach (var node in graphCfg.Nodes)
        {
            var outgoing = _edgesBySource.GetValueOrDefault(node.Id, []);
            if (outgoing.Count == 0) continue;

            // Disqualify if this node already has keyword-driven routes.
            if (tables.TryGetValue(node.Id, out var existingTable)
                && (existingTable.Routes.Count > 0 || existingTable.PhaseBreakKeywords.Count > 0))
                continue;

            var noKeywordEdges = outgoing.Where(e => string.IsNullOrEmpty(e.Keyword)).ToList();
            if (noKeywordEdges.Count != 1) continue; // ambiguous (>1) or none — skip

            var uncEdge = noKeywordEdges[0];

            // SourceAgents: skip if this node's agent is not in the allowed list.
            if (uncEdge.SourceAgents is { Count: > 0 }
                && !uncEdge.SourceAgents.Contains(node.Agent, StringComparer.OrdinalIgnoreCase))
                continue;

            var uncValidators = BuildValidatorsFromNames(
                uncEdge.AllValidators,
                uncEdge.RequiredCommandPattern,
                uncEdge.ShellFallbackPattern);

            if (IsBackEdge(node.Id, uncEdge.To))
            {
                var syntheticKw = $"__UNCOND_BACK:{node.Id.ToLowerInvariant()}";
                _backEdgeDestinations[syntheticKw] = uncEdge.To.ToLowerInvariant();
                _unconditionalBackEdges[node.Id]   = uncEdge.To.ToLowerInvariant();
                if (uncValidators.Count > 0)
                    _unconditionalBackEdgeValidators[node.Id] = uncValidators;
            }
            else
            {
                var targetNode    = nodeById.GetValueOrDefault(uncEdge.To);
                var nextAgentName = targetNode?.Agent ?? uncEdge.To;
                _unconditionalForwardRoutes[node.Id] = new RouteInfo(
                    uncEdge.To.ToLowerInvariant(),
                    nextAgentName,
                    uncValidators);
            }
        }

        return tables;
    }

    private IReadOnlyList<IRoutingValidator> BuildValidatorsFromNames(
        IReadOnlyList<string> names,
        string? requiredCommandPattern = null,
        string? shellFallbackPattern = null)
    {
        var result = new List<IRoutingValidator>();

        // Resolve sandbox root the same way OrchestratorBuilder does.
        var sandboxRoot = config.Security?.FileSystemSandboxPath is { Length: > 0 } sbx
            ? FuseraftPaths.ExpandPath(sbx)
            : null;

        var briefPath = config.Validation?.BriefPath;

        foreach (var name in names)
        {
            IRoutingValidator? v = name.ToLowerInvariant() switch
            {
                "requireshellpass"        => new RequireShellPassValidator(
                                                 requiredCommandPattern,
                                                 config.Validation?.ChangeLogPath),
                "requirewritefile"        => new HandoffToTesterValidator(
                                                 shellFallbackPattern: shellFallbackPattern,
                                                 changeLogPath:        config.Validation?.ChangeLogPath),
                "requireallfileswritten"  => briefPath is not null
                                                 ? new RequireAllFilesWrittenValidator(
                                                       briefPath,
                                                       config.Validation!.ChangeLogPath)
                                                 : null,
                "requirebrief"            => briefPath is not null
                                                 ? new RequireBriefValidator(briefPath)
                                                 : null,
                "testreportvalid"         => config.Validation is not null
                                                 ? new HandoffToReviewerValidator(config.Validation)
                                                 : null,
                "requirereviewjudgement"  => new RequireReviewJudgementValidator(briefPath),
                "requireacceptancecriteriapassed" => briefPath is not null
                                                 ? new RequireAcceptanceCriteriaPassedValidator(
                                                       briefPath,
                                                       config.Validation!.ChangeLogPath)
                                                 : null,
                "requirerelatedtestspass" => config.TestSelector is not null
                                                 ? new RequireRelatedTestsPassValidator(
                                                       config.TestSelector,
                                                       config.Validation?.ChangeLogPath,
                                                       sandboxRoot)
                                                 : null,
                _ => null
            };

            if (v is not null)
                result.Add(v);
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // Topology helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Validates parallel-group configuration after route tables and groups have been built.
    /// Logs warnings for each invalid condition rather than throwing — misconfigured groups
    /// are surfaced immediately so the operator sees them before any agent runs.
    /// </summary>
    private void ValidateParallelConfig(
        GraphConfig graphCfg,
        Dictionary<string, GraphNodeConfig> nodeById)
    {
        foreach (var node in graphCfg.Nodes.Where(n => n.Parallel))
        {
            // Parallel nodes cannot be terminal — they have no MAF workflow role and
            // would be silently skipped since terminal logic lives in RunNodeExecutorAsync.
            if (node.Terminal)
                logger.LogWarning(
                    "[GraphOrchestrator] Node '{NodeId}' is both Parallel and Terminal. " +
                    "Terminal is ignored on parallel nodes — they complete when they emit a forward-edge keyword.",
                    node.Id);

            // Parallel nodes that have no forward edges can never signal completion.
            var outgoing = _edgesBySource.GetValueOrDefault(node.Id, []);
            var fwdEdges = outgoing.Where(e => !IsBackEdge(node.Id, e.To)).ToList();
            if (fwdEdges.Count == 0)
                logger.LogWarning(
                    "[GraphOrchestrator] Parallel node '{NodeId}' has no forward edges — " +
                    "it can never signal completion to its merge target. Add an outgoing edge to the merge-target node.",
                    node.Id);

            // All forward edges from a parallel node must point to the same merge target.
            var mergeTargets = fwdEdges
                .Select(e => e.To.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (mergeTargets.Count > 1)
                logger.LogWarning(
                    "[GraphOrchestrator] Parallel node '{NodeId}' has forward edges to multiple targets " +
                    "({Targets}). All parallel nodes in a group must converge on a single merge-target node.",
                    node.Id, string.Join(", ", mergeTargets));

            // The merge target of a parallel node must not itself be Parallel.
            foreach (var targetId in mergeTargets)
            {
                if (nodeById.TryGetValue(targetId, out var targetNode) && targetNode.Parallel)
                    logger.LogWarning(
                        "[GraphOrchestrator] Parallel node '{NodeId}' routes to '{TargetId}' which is also " +
                        "Parallel. Nested parallel fan-out is not supported — the merge target must be a normal node.",
                        node.Id, targetId);
            }
        }

        // Each parallel group that has no merge target resolved means the parallel nodes
        // had no route tables (missing agent or no forward edges). Already warned above;
        // log here for the group-level perspective.
        foreach (var (groupKey, pg) in _parallelGroups.Where(kv => string.IsNullOrEmpty(kv.Value.MergeTargetId)))
            logger.LogWarning(
                "[GraphOrchestrator] Parallel group '{Key}' could not resolve a merge target. " +
                "The fan-out keyword will be treated as unroutable at runtime.",
                groupKey);
    }

    /// <summary>
    /// Computes BFS layer numbers from the entry node traversing ALL edges (forward and back).
    /// Each node is assigned the layer of its first BFS encounter. Back-edges are those
    /// whose target node has a BFS layer ≤ the source node's layer.
    /// </summary>
    private Dictionary<string, int> ComputeBfsLayers(string entryNodeId)
    {
        var layers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var queue  = new Queue<(string NodeId, int Layer)>();
        queue.Enqueue((entryNodeId, 0));
        layers[entryNodeId] = 0;

        while (queue.Count > 0)
        {
            var (current, layer) = queue.Dequeue();
            foreach (var edge in _edgesBySource.GetValueOrDefault(current, []))
            {
                if (!layers.ContainsKey(edge.To))
                {
                    layers[edge.To] = layer + 1;
                    queue.Enqueue((edge.To, layer + 1));
                }
            }
        }

        return layers;
    }

    /// <returns><c>true</c> when the edge from → to is a back-edge (target has lower or equal BFS layer than source).</returns>
    private bool IsBackEdge(string from, string to)
    {
        var fromLayer = _nodeLayers.GetValueOrDefault(from, 0);
        var toLayer   = _nodeLayers.GetValueOrDefault(to, 0);
        return toLayer <= fromLayer;
    }

    // -------------------------------------------------------------------------
    // Start-node resolution
    // -------------------------------------------------------------------------

    private string DetermineStartNodeId(
        IReadOnlyList<AgentMessage>? priorHistory,
        string? resumeHint,
        string defaultEntryNode,
        GraphConfig graphCfg,
        Dictionary<string, GraphNodeConfig> nodeById)
    {
        // Priority 1: explicit hint from SetResumeExecutorId (most accurate — set by
        // the CLI after checkpoint restore or compaction).
        if (!string.IsNullOrWhiteSpace(resumeHint))
        {
            // Try hint as node ID first — GraphOrchestrator uses node IDs as executor IDs.
            if (nodeById.ContainsKey(resumeHint))
            {
                logger.LogDebug(
                    "[GraphOrchestrator] DetermineStartNodeId: hint matches node Id '{Hint}'",
                    resumeHint);
                return resumeHint.ToLowerInvariant();
            }

            // SessionRunner.ApplyCompactionAsync stores msg.AgentName as ResumeExecutorId, so
            // the hint may be an agent name rather than a node ID — scan for the first match.
            var hintNode = graphCfg.Nodes.FirstOrDefault(n =>
                string.Equals(n.Agent, resumeHint, StringComparison.OrdinalIgnoreCase));
            if (hintNode is not null)
            {
                logger.LogDebug(
                    "[GraphOrchestrator] DetermineStartNodeId: hint '{Hint}' is agent name → node '{NodeId}'",
                    resumeHint, hintNode.Id);
                return hintNode.Id.ToLowerInvariant();
            }

            logger.LogWarning(
                "[GraphOrchestrator] DetermineStartNodeId: hint '{Hint}' does not match any node Id " +
                "or agent name — ignoring and falling back to history heuristics.",
                resumeHint);
        }

        if (priorHistory is not { Count: > 0 })
            return defaultEntryNode;

        // Priority 2: scan back-edge keywords in prior history (newest-first).
        for (int i = priorHistory.Count - 1; i >= 0; i--)
        {
            var msg = priorHistory[i];
            if (msg.Role != "assistant" || string.IsNullOrEmpty(msg.Content)) continue;

            foreach (var kw in _backEdgeDestinations.Keys)
            {
                if (kw == TerminalSentinel) continue;
                if (KeywordDetector.IsKeywordOnOwnLineStrict(msg.Content, kw) &&
                    _backEdgeDestinations.TryGetValue(kw, out var nextNode) &&
                    nextNode is not null)
                {
                    logger.LogDebug(
                        "[GraphOrchestrator] DetermineStartNodeId: back-edge keyword '{Kw}' → '{Next}'",
                        kw, nextNode);
                    return nextNode;
                }
            }

            // Also check forward-edge keywords — when a handoff keyword was the last thing in
            // history, resume from the TARGET node rather than resetting to the entry.
            foreach (var edge in graphCfg.Edges)
            {
                if (!IsBackEdge(edge.From, edge.To) &&
                    edge.Keyword is { Length: > 0 } &&
                    KeywordDetector.IsKeywordOnOwnLineStrict(msg.Content, edge.Keyword))
                {
                    logger.LogDebug(
                        "[GraphOrchestrator] DetermineStartNodeId: forward-edge keyword '{Kw}' → '{Next}'",
                        edge.Keyword, edge.To);
                    return edge.To.ToLowerInvariant();
                }
            }
        }

        // Priority 3: last active agent name → find its node.
        for (int i = priorHistory.Count - 1; i >= 0; i--)
        {
            var msg = priorHistory[i];
            if (msg.Role != "assistant" || string.IsNullOrWhiteSpace(msg.AgentName)) continue;

            var node = graphCfg.Nodes.FirstOrDefault(n =>
                string.Equals(n.Agent, msg.AgentName, StringComparison.OrdinalIgnoreCase));

            if (node is not null)
            {
                logger.LogDebug(
                    "[GraphOrchestrator] DetermineStartNodeId: agent-name fallback → node '{NodeId}' (agent '{Agent}')",
                    node.Id, node.Agent);
                return node.Id.ToLowerInvariant();
            }
        }

        // Priority 4: configured entry node.
        return defaultEntryNode;
    }

    // -------------------------------------------------------------------------
    // Phase-transition helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Scans backward through <paramref name="history"/> to find the last assistant
    /// text message and returns a truncated excerpt to embed in the phase-transition
    /// injection, giving the incoming agent concrete context about what was rejected.
    /// Strips trailing routing keywords so only the diagnostic content survives.
    /// Returns null when no meaningful content is found.
    /// </summary>
    private static string? ExtractLastAgentFeedback(List<ChatMessage> history, int maxChars)
    {
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role != ChatRole.Assistant) continue;

            var text = history[i].Text;
            if (string.IsNullOrWhiteSpace(text)) continue;

            // Strip trailing routing keyword lines (REVISION REQUIRED, APPROVED, etc.)
            // so the injection contains only the diagnostic reasoning, not the signal.
            var lines = text.Split('\n');
            var lastContent = lines
                .Reverse()
                .SkipWhile(l => string.IsNullOrWhiteSpace(l) ||
                                CorrectionEngine.PhaseBreakKeywords.Contains(l.Trim()))
                .Reverse()
                .ToList();

            if (lastContent.Count == 0) continue;

            var content = string.Join('\n', lastContent).Trim();
            if (string.IsNullOrWhiteSpace(content)) continue;

            // Strip JSON review blocks — they're structural, not human-readable feedback.
            // Keep only lines outside of ``` fences.
            var stripped = OrchestratorHelpers.StripCodeFences(content).Trim();
            if (string.IsNullOrWhiteSpace(stripped)) stripped = content;

            return stripped.Length > maxChars
                ? stripped[..maxChars].TrimEnd() + "…"
                : stripped;
        }

        return null;
    }

}
