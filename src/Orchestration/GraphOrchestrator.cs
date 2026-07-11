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
using fuseraft.Orchestration.Graph;
using fuseraft.Orchestration.Validation;
using fuseraft.Orchestration.Workflow;

// Disambiguate from Microsoft.Agents.AI.AgentFactory
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Plugins;
using AgentFactory = fuseraft.Infrastructure.Agents.AgentFactory;

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
    fuseraft.Infrastructure.Repository.RepositoryKnowledgeStore? repositoryKnowledgeStore = null,
    ILoggerFactory? loggerFactory = null) : IOrchestrator
{
    // Default consecutive-failure limit per node. CorrectionEngine uses this same value
    // in its RETRY n/4 messages, so both stay in sync via this constant.
    internal const int DefaultMaxRetries = 4;

    // Sentinel keyword written to AgentContext.LastKeyword when a terminal node completes.
    // The outer loop maps this to a null destination (→ break). Internal (not private) so
    // GraphTopology/SubGraphExecutor/ParallelFanOutExecutor can reference the same constant
    // instead of redeclaring it — mirrors how CorrectionEngine already reaches into
    // DefaultMaxRetries below.
    internal const string TerminalSentinel = "__GRAPH_TERMINAL__";

    // Per-branch TurnIndex offset applied by ForkContext so concurrent parallel branches
    // never emit colliding TurnIndex values to the shared MessageSink/event log. Large
    // enough that no single branch can plausibly take this many turns (bounded by
    // MaxRetries * MaxTotalTurnsMultiplier, typically well under 100). Internal so
    // ParallelFanOutExecutor (which owns ForkContext/MergeParallelContexts) can reference it.
    internal const int BranchTurnIndexStride = 100_000;

    private readonly IHumanApprovalService? _humanApprovalService = humanApprovalService;

    // Collaborators fixed for this instance's lifetime, bundled for TurnExecutionHelpers /
    // SubGraphExecutor / ParallelFanOutExecutor — see TurnServices' doc comment for why
    // SessionId/Task are intentionally excluded (they mutate post-construction). Lazily built
    // (not a field initializer) because the callbacks below reference AgentStarting/
    // TokenBudgetWarning, and field initializers cannot reference other instance members
    // (CS0236) — a property getter runs after construction completes, so it's unrestricted.
    private TurnServices? _servicesLazy;
    private TurnServices _services => _servicesLazy ??= new(
        config, agentFactory, logger, eventEmitter, governanceKernel, contextPipeline,
        changeTracker, repositoryKnowledgeStore, humanApprovalService,
        OnAgentStarting: name => AgentStarting?.Invoke(name),
        OnTokenBudgetWarning: (name, input, warn) => TokenBudgetWarning?.Invoke(name, input, warn));

    // Same lazy-property reasoning as _services (CS0236 — depends on the _services property).
    private SubGraphExecutor? _subGraphExecutorLazy;
    private SubGraphExecutor _subGraphExecutor => _subGraphExecutorLazy ??= new(_services, loggerFactory);

    private ParallelFanOutExecutor? _parallelFanOutLazy;
    private ParallelFanOutExecutor _parallelFanOut => _parallelFanOutLazy ??= new(_services);

    private string _sessionId = string.Empty;
    private string? _resumeNodeId;
    // Captured from StreamAsync for use in per-node executor helpers.
    private string _task = string.Empty;
    private TaskModel? _structuredTask;

    // Computed once per StreamAsync call by GraphTopology.Build — back-edge classification,
    // per-node route tables, unconditional (no-keyword) routing, and parallel fan-out group
    // membership. Read-only for the rest of the session once assigned.
    private GraphTopology _topology = null!;

    // Per-session recovery tracking — keyed by "{nodeId}::{keyword}" (forward) or
    // "{nodeId}::{keyword}::back" (back-edge). Each edge activates recovery at most once.
    // ConcurrentDictionary because parallel workers may check/set it simultaneously. Reset at
    // the start of each StreamAsync call; passed by reference into ParallelFanOutExecutor so
    // parallel-branch and sequential back/forward-edge recovery tracking share one dedupe space.
    private ConcurrentDictionary<string, bool> _recoveryActivated = new(StringComparer.OrdinalIgnoreCase);

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
    public void SetStructuredTask(TaskModel? model) => _structuredTask = model;

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

        _topology = GraphTopology.Build(graphCfg, config, nodeById, entryNodeId, logger);
        _recoveryActivated = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Build MAF executor bindings (reused across all phases).
        var bindings = BuildExecutorBindings(
            agents, agentInstructions, agentConfigs, _topology.RouteTablesByNodeId, nodeById);

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

        // Determine the starting node (consume resume hint, then fall back to heuristics).
        var resumeHint = _resumeNodeId;
        _resumeNodeId  = null;
        string startNodeId = _topology.DetermineStartNodeId(priorHistory, resumeHint, entryNodeId, graphCfg, nodeById);

        // Inner CTS so the background RunPhasesAsync is always cancelled when the consumer
        // abandons the IAsyncEnumerable (e.g. RunCommand breaks early for compaction).
        using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync(EventTypes.SessionStart,
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
                    await eventEmitter.EmitAsync(EventTypes.PhaseStart,
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

                if (!_topology.BackEdgeDestinations.TryGetValue(lastKeyword, out var nextStart))
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
                    await eventEmitter.EmitAsync(EventTypes.PhaseEnd,
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

            if (naturallyTerminated)
            {
                if (eventEmitter is not null)
                    _ = eventEmitter.EmitAsync(EventTypes.TerminationSatisfied,
                        payload: new { phases = phaseCount });
            }
            else if (!ct.IsCancellationRequested && maxPhases != int.MaxValue)
            {
                if (eventEmitter is not null)
                    _ = eventEmitter.EmitAsync(EventTypes.MaxTurnsExceeded,
                        payload: new { phases = phaseCount, max = maxPhases });
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
                    AgentName = AgentNames.Orchestrator,
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
            foreach (var edge in _topology.EdgesBySource.GetValueOrDefault(current, []))
            {
                if (_topology.IsBackEdge(current, edge.To)) continue;

                if (_topology.ParallelNodeIds.Contains(edge.To))
                {
                    // Parallel nodes are excluded from the MAF DAG. Bridge the gap by
                    // adding a virtual edge from the source directly to the merge target,
                    // so the merge-target executor is registered in the workflow and
                    // reachable when the fan-out calls wfCtx.SendMessageAsync.
                    var parallelKey = $"{current}::{edge.Keyword ?? string.Empty}";
                    if (_topology.ParallelGroups.TryGetValue(parallelKey, out var pg)
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
            var routeTable = routeTables.GetValueOrDefault(node.Id, new AgentRouteTable());

            // Sub-graph node: run a nested GraphOrchestrator instead of a single agent.
            if (!string.IsNullOrEmpty(node.SubGraphId))
            {
                var subGraphId = node.SubGraphId;
                var isTerminal = node.Terminal;

                Func<AgentContext, IWorkflowContext, CancellationToken, ValueTask> subHandler =
                    async (ctx, wfCtx, ct) =>
                        await _subGraphExecutor.RunSubGraphNodeAsync(
                            node.Id, subGraphId, isTerminal, routeTable, ctx, wfCtx,
                            _topology, _sessionId, _task, RecordNodeState, ct)
                        .ConfigureAwait(false);

                var subExecutor = new FunctionExecutor<AgentContext>(
                    node.Id.ToLowerInvariant(),
                    subHandler,
                    ExecutorOptions.Default,
                    [typeof(AgentContext)],
                    [typeof(AgentContext)],
                    false);

                bindings[node.Id] = subExecutor;
                continue;
            }

            if (!agents.ContainsKey(node.Agent))
            {
                logger.LogWarning(
                    "[GraphOrchestrator] Node '{NodeId}' references unknown agent '{Agent}' — skipping.",
                    node.Id, node.Agent);
                continue;
            }

            var agentName   = node.Agent;
            var isAgentTerminal = node.Terminal;
            var agent       = agents[agentName];
            var instructions = agentInstructions.GetValueOrDefault(agentName, string.Empty);
            var agentCfg    = agentConfigs.GetValueOrDefault(agentName) ?? new AgentConfig();

            Func<AgentContext, IWorkflowContext, CancellationToken, ValueTask> handler =
                async (ctx, wfCtx, ct) =>
                    await RunNodeExecutorAsync(
                        node.Id, agentName, agent, instructions, agentCfg,
                        isAgentTerminal, routeTable, ctx, wfCtx, ct,
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

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync(EventTypes.AgentStart,
                agent: agentName,
                turn:  ctx.TurnIndex);

        int maxRetries      = config.Selection.Graph?.MaxRetries ?? DefaultMaxRetries;
        int maxTotalTurns   = maxRetries * (config.Selection.Graph?.MaxTotalTurnsMultiplier ?? 10);
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

            // responseText is used by both the terminal validator path and keyword detection.
            var responseText = response!.Text ?? string.Empty;

            // Terminal node: validate then end the session.
            if (isTerminal)
            {
                if (routeTable.TerminalValidators.Count > 0)
                {
                    var (termOk, termErr, termValidator) = await TurnExecutionHelpers.RunValidatorsAsync(
                        routeTable.TerminalValidators, ctx.History, ct).ConfigureAwait(false);

                    if (!termOk)
                    {
                        consecutiveFails++;
                        TurnExecutionHelpers.RecordGovernanceViolation(agentName, termValidator!, consecutiveFails, maxRetries, _sessionId, _services);

                        if (consecutiveFails >= maxRetries)
                            throw new ValidatorStuckException(agentName, termValidator!, consecutiveFails, termErr!);

                        await TurnExecutionHelpers.EmitAndInjectValidationFailureAsync(
                            agentName, "(terminal)", termValidator!, termErr!, responseText, consecutiveFails, maxRetries, ctx, ct, _services);
                        continue;
                    }
                }

                consecutiveFails = 0;
                ctx.LastKeyword  = TerminalSentinel;

                RecordNodeState(ctx, agentName);

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync(EventTypes.StateAdvanced,
                        agent: agentName,
                        turn:  agentMsg!.TurnIndex,
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
                var (uncHandled, uncShouldReturn, uncFails) = await HandleUnconditionalRoutingAsync(
                    nodeId, agentName, responseText, consecutiveFails, maxRetries, ctx, agentMsg!, wfCtx, ct);
                consecutiveFails = uncFails;
                if (uncShouldReturn) return;
                if (uncHandled) continue;
            }

            // Keyword detection

            var handoffArgKeyword = KeywordDetector.ExtractHandoffToolCallKeyword(response!.Messages, routeTable);
            var allKeywords       = handoffArgKeyword is not null
                ? (IReadOnlyList<string>)[handoffArgKeyword]
                : KeywordDetector.DetectKeywords(responseText, routeTable);

            // Ambiguous: multiple keywords found.
            if (allKeywords.Count > 1)
            {
                consecutiveFails++;

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync(EventTypes.MultiKeyword,
                        agent:   agentName,
                        turn:    agentMsg!.TurnIndex,
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
                await eventEmitter.EmitAsync(EventTypes.KeywordDetected,
                    agent:   agentName,
                    turn:    agentMsg!.TurnIndex,
                    payload: new { keyword = foundKeyword });

            // Back-edge keyword (phase-break): validate then yield to restart outer loop.

            if (foundKeyword is not null && routeTable.PhaseBreakKeywords.Contains(foundKeyword))
            {
                var (backHandled, backShouldReturn, backFails) =
                    await HandleBackEdgeAsync(
                        nodeId, agentName, foundKeyword, routeTable, agentMsg!, responseText,
                        consecutiveFails, maxRetries, ctx, wfCtx, agents, agentInstructions, agentConfigs, ct);
                consecutiveFails = backFails;
                if (backShouldReturn) return;
                if (backHandled) continue;
            }

            // Parallel fan-out keyword

            var pgKey = $"{nodeId}::{foundKeyword}";
            if (foundKeyword is not null && _topology.ParallelGroups.TryGetValue(pgKey, out var parallelGroup))
            {
                var (pgShouldReturn, pgFails) = await _parallelFanOut.RunFanOutAsync(
                    nodeId, agentName, foundKeyword, parallelGroup, responseText, ctx, wfCtx,
                    agents, agentInstructions, agentConfigs, _topology, _recoveryActivated,
                    _sessionId, _task, RecordNodeState, consecutiveFails, maxRetries, agentMsg!, ct);
                consecutiveFails = pgFails;
                if (pgShouldReturn) return;
                continue;
            }

            // Forward-edge keyword: validate and route.

            if (foundKeyword is not null && routeTable.Routes.TryGetValue(foundKeyword, out var route))
            {
                var (fwdHandled, fwdShouldReturn, fwdFails) =
                    await EvaluateRouteAsync(
                        nodeId, agentName, foundKeyword, route, agentMsg!, responseText,
                        consecutiveFails, maxRetries, ctx, wfCtx, agents, agentInstructions, agentConfigs, ct);
                consecutiveFails = fwdFails;
                if (fwdShouldReturn) return;
                if (fwdHandled) continue;
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
                    payload: new { consecutive = consecutiveFails, source = "graph_orchestrator" });

            int histBefore2 = ctx.History.Count;
            await CorrectionEngine.InjectNoKeywordCorrection(
                ctx.History, responseText, agentName, consecutiveFails, routeTable, eventEmitter,
                agentMsg!.ToolCalls);
            await TurnExecutionHelpers.PersistCorrectionsAsync(ctx, histBefore2, ct).ConfigureAwait(false);

            if (consecutiveFails >= maxRetries)
            {
                if (eventEmitter is not null)
                    _ = eventEmitter.EmitAsync(EventTypes.RetryExhausted,
                        agent:   agentName,
                        turn:    agentMsg!.TurnIndex,
                        payload: new { reason = "no-keyword", consecutive = consecutiveFails, max = maxRetries });
                throw new ValidatorStuckException(agentName, "no-keyword", consecutiveFails,
                    $"Node '{nodeId}' ({agentName}) emitted no routing keyword " +
                    $"for {consecutiveFails} consecutive turns.");
            }

            if (eventEmitter is not null)
                _ = eventEmitter.EmitAsync(EventTypes.RetryScheduled,
                    agent:   agentName,
                    turn:    agentMsg!.TurnIndex,
                    payload: new { reason = "no-keyword", attempt = consecutiveFails + 1, max = maxRetries });
        }
    }

    /// <summary>
    /// Single agent turn and stream collection. Assembles context via
    /// <see cref="HandleContextOverflowAsync"/>, emits <c>turn_start</c>, runs the agent,
    /// handles timeout by injecting a correction and signalling retry, then records and
    /// emits the response via <see cref="RecordAndEmitAsync"/>.
    /// </summary>
    /// <returns>
    /// A tuple of (<see cref="AgentResponse"/>, <see cref="AgentMessage"/>,
    /// updated consecutive-fail count, shouldContinue). When <c>shouldContinue</c> is
    /// <c>true</c> a timeout was handled and the caller must retry the turn loop.
    /// </returns>
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
        // Assemble context through the unified pipeline (or legacy filter when pipeline is absent).
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
                throw new ValidatorStuckException(agentName, "streaming-timeout",
                    consecutiveFails, tex.Message);

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

        var agentMsg = await TurnExecutionHelpers.RecordAndEmitAsync(response, agentName, ctx, ct, _sessionId, _services);
        return (response, agentMsg, consecutiveFails, false);
    }

    /// <summary>
    /// Context cap warning and compaction trigger. Assembles the per-turn message list via
    /// the unified context pipeline (when configured) or the legacy
    /// <see cref="ContextWindowFilter"/>, emits a <c>context_window_warn</c> event when
    /// the filtered count approaches the configured cap fraction, and returns the assembled
    /// context ready for the agent call.
    /// </summary>
    private async Task<IEnumerable<ChatMessage>> HandleContextOverflowAsync(
        string agentName,
        AgentConfig agentCfg,
        string instructions,
        AgentContext ctx,
        CancellationToken ct)
    {
        // Assemble context through the unified pipeline (or legacy filter when pipeline is absent).
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
            await TurnExecutionHelpers.EmitContextWindowWarnAsync(agentName, agentCfg, assembled.Messages, ctx, _services);
            if (eventEmitter is not null)
                await TurnExecutionHelpers.EmitContextAssemblyAsync(eventEmitter, assembled.Metrics, ctx.TurnIndex);
        }
        else
        {
            var filtered = ContextWindowFilter.Apply(ctx.History, agentCfg.ContextWindow);
            await TurnExecutionHelpers.EmitContextWindowWarnAsync(agentName, agentCfg, filtered, ctx, _services);
            context = !string.IsNullOrWhiteSpace(instructions)
                ? [new ChatMessage(ChatRole.System, instructions), .. filtered]
                : filtered;
        }
        return context;
    }

    /// <summary>
    /// Back-edge detection and recovery agent logic. Runs per-keyword validators,
    /// activates the recovery agent on repeated failures, enforces the human-approval
    /// gate, then yields output to restart the outer phase loop.
    /// </summary>
    /// <returns>
    /// A tuple of (handled, shouldReturn, consecutiveFails).
    /// <c>handled=true, shouldReturn=true</c> means the back-edge fired and the caller
    /// must <c>return</c>. <c>handled=true, shouldReturn=false</c> means validation
    /// failed and the caller must <c>continue</c>. <c>handled=false</c> is never
    /// returned; all back-edge paths resolve to one of the two above.
    /// </returns>
    private async Task<(bool Handled, bool ShouldReturn, int ConsecutiveFails)> HandleBackEdgeAsync(
        string nodeId,
        string agentName,
        string foundKeyword,
        AgentRouteTable routeTable,
        AgentMessage agentMsg,
        string responseText,
        int consecutiveFails,
        int maxRetries,
        AgentContext ctx,
        IWorkflowContext wfCtx,
        Dictionary<string, AIAgent> agents,
        Dictionary<string, string> agentInstructions,
        Dictionary<string, AgentConfig> agentConfigs,
        CancellationToken ct)
    {
        // Run per-keyword validators declared on this back-edge (GAP-2).
        if (routeTable.PhaseBreakValidators.TryGetValue(foundKeyword, out var pbValidators)
            && pbValidators.Count > 0)
        {
            var (pbOk, pbErr, pbValidator) = await TurnExecutionHelpers.RunValidatorsAsync(
                pbValidators, ctx.History, ct).ConfigureAwait(false);

            if (!pbOk)
            {
                consecutiveFails = Math.Min(consecutiveFails + 1, maxRetries - 1);
                TurnExecutionHelpers.RecordGovernanceViolation(agentName, pbValidator!, consecutiveFails, maxRetries, _sessionId, _services);

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
                    await TurnExecutionHelpers.InvokeRecoveryAgentAsync(
                        backRecoveryName, backRecoveryAgt,
                        agentInstructions, agentConfigs,
                        $"'{pbValidator}' failed {consecutiveFails}× on back-edge '{foundKeyword}'",
                        pbErr!, foundKeyword, ctx, ct, _sessionId, _task, _services);
                    consecutiveFails = 0;
                    return (true, false, consecutiveFails);
                }

                await TurnExecutionHelpers.EmitAndInjectValidationFailureAsync(
                    agentName, foundKeyword, pbValidator!, pbErr!, responseText, consecutiveFails, maxRetries, ctx, ct, _services);
                return (true, false, consecutiveFails);
            }
        }

        // Human approval gate for back-edges.
        if (routeTable.PhaseBreakRequireHumanApproval.Contains(foundKeyword)
            && _humanApprovalService is not null)
        {
            var backTarget = _topology.BackEdgeDestinations.TryGetValue(foundKeyword, out var pbd0)
                ? pbd0 ?? "(terminal)"
                : "(terminal)";
            var (approved, approvedFails) = await TurnExecutionHelpers.ApplyHumanApprovalGateAsync(
                foundKeyword, agentName, backTarget,
                $"Phase-break to '{backTarget}' was blocked by the operator. " +
                $"Continue your work or await further instructions.",
                consecutiveFails, ctx, ct, _services);
            consecutiveFails = approvedFails;
            if (!approved) return (true, false, consecutiveFails);
        }

        consecutiveFails = 0;
        ctx.LastKeyword  = foundKeyword;

        var backEdgeDest = _topology.BackEdgeDestinations.TryGetValue(foundKeyword, out var pbd) ? pbd : null;
        RecordNodeState(ctx, backEdgeDest ?? agentName);

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync(EventTypes.StateAdvanced,
                agent: agentName,
                turn:  agentMsg.TurnIndex,
                payload: new { version = ctx.CurrentState.Version, phase_break = foundKeyword, next = backEdgeDest ?? "(terminal)" });

        await wfCtx.YieldOutputAsync(ctx, ct).ConfigureAwait(false);
        return (true, true, consecutiveFails);
    }

    /// <summary>
    /// Unconditional (no-keyword) routing for nodes whose only outgoing edge(s) carry no
    /// keyword — routes automatically without requiring the agent to emit a handoff keyword.
    /// Checks a forward route first, then a back-edge; logs a config-gap error and falls
    /// through (<c>Handled=false</c>) when the node has neither wired.
    /// </summary>
    /// <returns>
    /// A tuple of (handled, shouldReturn, consecutiveFails).
    /// <c>handled=false</c> means no unconditional route is wired for this node — the caller
    /// must fall through to keyword detection. <c>handled=true, shouldReturn=true</c> means
    /// the route fired and the caller must <c>return</c>. <c>handled=true, shouldReturn=false</c>
    /// means validation failed and the caller must <c>continue</c>.
    /// </returns>
    private async Task<(bool Handled, bool ShouldReturn, int ConsecutiveFails)> HandleUnconditionalRoutingAsync(
        string nodeId,
        string agentName,
        string responseText,
        int consecutiveFails,
        int maxRetries,
        AgentContext ctx,
        AgentMessage agentMsg,
        IWorkflowContext wfCtx,
        CancellationToken ct)
    {
        if (_topology.UnconditionalForwardRoutes.TryGetValue(nodeId, out var autoFwdRoute))
        {
            var (autoOk, autoErr, autoValidator) = await TurnExecutionHelpers.RunValidatorsAsync(
                autoFwdRoute.Validators, ctx.History, ct).ConfigureAwait(false);

            if (autoOk)
            {
                if (autoFwdRoute.Validators.Count > 0)
                    governanceKernel?.SloEngine.Get("policy-compliance")?.Record(1.0);

                consecutiveFails = 0;
                ctx.LastKeyword  = null;

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync(EventTypes.AgentRouted,
                        agent:   agentName,
                        turn:    agentMsg.TurnIndex,
                        payload: new { keyword = "(unconditional)", to = autoFwdRoute.NextExecutorName });

                RecordNodeState(ctx, autoFwdRoute.NextExecutorName);

                ctx.History.Add(new ChatMessage(ChatRole.User,
                    $"[fuseraft: {agentName} → {autoFwdRoute.NextExecutorName}]"));

                await wfCtx.SendMessageAsync(ctx, autoFwdRoute.NextExecutorId, ct).ConfigureAwait(false);
                return (true, true, consecutiveFails);
            }

            consecutiveFails = Math.Min(consecutiveFails + 1, maxRetries - 1);
            TurnExecutionHelpers.RecordGovernanceViolation(agentName, autoValidator!, consecutiveFails, maxRetries, _sessionId, _services);

            if (consecutiveFails >= maxRetries)
                throw new ValidatorStuckException(agentName, autoValidator!, consecutiveFails, autoErr!);

            await TurnExecutionHelpers.EmitAndInjectValidationFailureAsync(
                agentName, "(unconditional)", autoValidator!, autoErr!, responseText, consecutiveFails, maxRetries, ctx, ct, _services);
            return (true, false, consecutiveFails);
        }

        if (_topology.UnconditionalBackEdges.TryGetValue(nodeId, out var autoBackDest))
        {
            if (_topology.UnconditionalBackEdgeValidators.TryGetValue(nodeId, out var uncBackValidators)
                && uncBackValidators.Count > 0)
            {
                var (ubOk, ubErr, ubValidator) = await TurnExecutionHelpers.RunValidatorsAsync(
                    uncBackValidators, ctx.History, ct).ConfigureAwait(false);

                if (!ubOk)
                {
                    consecutiveFails = Math.Min(consecutiveFails + 1, maxRetries - 1);
                    TurnExecutionHelpers.RecordGovernanceViolation(agentName, ubValidator!, consecutiveFails, maxRetries, _sessionId, _services);

                    if (consecutiveFails >= maxRetries)
                        throw new ValidatorStuckException(agentName, ubValidator!, consecutiveFails, ubErr!);

                    await TurnExecutionHelpers.EmitAndInjectValidationFailureAsync(
                        agentName, "(unconditional-back)", ubValidator!, ubErr!, responseText, consecutiveFails, maxRetries, ctx, ct, _services);
                    return (true, false, consecutiveFails);
                }
            }

            consecutiveFails = 0;
            // Use a synthetic keyword so the outer phase loop can look up the destination.
            ctx.LastKeyword  = $"__UNCOND_BACK:{nodeId.ToLowerInvariant()}";

            RecordNodeState(ctx, autoBackDest ?? agentName);

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync(EventTypes.StateAdvanced,
                    agent: agentName,
                    turn:  agentMsg.TurnIndex,
                    payload: new { version = ctx.CurrentState.Version, phase_break = "(unconditional)", next = autoBackDest ?? "(terminal)" });

            await wfCtx.YieldOutputAsync(ctx, ct).ConfigureAwait(false);
            return (true, true, consecutiveFails);
        }

        // Node has no keyword edges and no unconditional route wired — config gap.
        // Log and fall through to the correction path so HITL escalation fires normally.
        logger.LogError(
            "[GraphOrchestrator] Node '{NodeId}' (agent '{Agent}') has no keyword edges " +
            "and no unconditional route — it can never route. Check the graph config.",
            nodeId, agentName);

        return (false, false, consecutiveFails);
    }

    /// <summary>
    /// Route table lookup and validator execution for forward-edge keywords. Runs the
    /// route's validators, enforces the human-approval gate on success, records state,
    /// and dispatches via <c>SendMessageAsync</c>. On validation failure activates the
    /// recovery agent when eligible, then injects a correction and signals retry.
    /// </summary>
    /// <returns>
    /// A tuple of (handled, shouldReturn, consecutiveFails).
    /// <c>handled=true, shouldReturn=true</c> means the route fired and the caller
    /// must <c>return</c>. <c>handled=true, shouldReturn=false</c> means validation
    /// failed and the caller must <c>continue</c>.
    /// </returns>
    private async Task<(bool Handled, bool ShouldReturn, int ConsecutiveFails)> EvaluateRouteAsync(
        string nodeId,
        string agentName,
        string foundKeyword,
        RouteInfo route,
        AgentMessage agentMsg,
        string responseText,
        int consecutiveFails,
        int maxRetries,
        AgentContext ctx,
        IWorkflowContext wfCtx,
        Dictionary<string, AIAgent> agents,
        Dictionary<string, string> agentInstructions,
        Dictionary<string, AgentConfig> agentConfigs,
        CancellationToken ct)
    {
        var (ok, errMsg, failingValidator) = await TurnExecutionHelpers.RunValidatorsAsync(
            route.Validators, ctx.History, ct).ConfigureAwait(false);

        if (ok)
        {
            if (route.Validators.Count > 0)
                governanceKernel?.SloEngine.Get("policy-compliance")?.Record(1.0);

            // Human approval gate: prompt before the route fires.
            if (route.RequireHumanApproval && _humanApprovalService is not null)
            {
                var (approved, approvedFails) = await TurnExecutionHelpers.ApplyHumanApprovalGateAsync(
                    foundKeyword, agentName, route.NextExecutorName,
                    $"Route to {route.NextExecutorName} was blocked by the operator. " +
                    $"Continue your work or await further instructions.",
                    consecutiveFails, ctx, ct, _services);
                consecutiveFails = approvedFails;
                if (!approved) return (true, false, consecutiveFails);
            }

            consecutiveFails = 0;
            ctx.LastKeyword  = foundKeyword;

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync(EventTypes.AgentRouted,
                    agent:   agentName,
                    turn:    agentMsg.TurnIndex,
                    payload: new { keyword = foundKeyword, to = route.NextExecutorName });

            RecordNodeState(ctx, route.NextExecutorName);
            if (eventEmitter is not null)
                await eventEmitter.EmitAsync(EventTypes.StateAdvanced,
                    agent: agentName,
                    turn:  agentMsg.TurnIndex,
                    payload: new { version = ctx.CurrentState.Version, to = route.NextExecutorName });

            ctx.History.Add(new ChatMessage(ChatRole.User,
                $"[fuseraft: {agentName} → {route.NextExecutorName}]"));

            await wfCtx.SendMessageAsync(ctx, route.NextExecutorId, ct).ConfigureAwait(false);
            return (true, true, consecutiveFails);
        }

        // Validator failed — clamp to maxRetries-1 so a single keyword find is not
        // penalised as heavily as a missing keyword before injecting correction.
        consecutiveFails = Math.Min(consecutiveFails + 1, maxRetries - 1);
        TurnExecutionHelpers.RecordGovernanceViolation(agentName, failingValidator!, consecutiveFails, maxRetries, _sessionId, _services);

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
            await TurnExecutionHelpers.InvokeRecoveryAgentAsync(
                route.RecoveryAgent, fwdRecoveryAgt,
                agentInstructions, agentConfigs,
                $"'{failingValidator}' failed {consecutiveFails}× on edge '{foundKeyword}'",
                errMsg!, foundKeyword, ctx, ct, _sessionId, _task, _services);
            consecutiveFails = 0;
            return (true, false, consecutiveFails);
        }

        await TurnExecutionHelpers.EmitAndInjectValidationFailureAsync(
            agentName, foundKeyword, failingValidator!, errMsg!, responseText, consecutiveFails, maxRetries, ctx, ct, _services);
        return (true, false, consecutiveFails);
    }

    /// <summary>
    /// State history append and checkpoint write. Advances the current agent state via
    /// <see cref="StateHandoff.Advance"/> and appends the new snapshot to
    /// <see cref="_stateHistory"/> under the state-history lock.
    /// </summary>
    private void RecordNodeState(AgentContext ctx, string nextNodeName)
    {
        ctx.CurrentState = StateHandoff.Advance(ctx.CurrentState, nextNodeName);
        lock (_stateHistoryLock) _stateHistory.Add(ctx.CurrentState);
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
