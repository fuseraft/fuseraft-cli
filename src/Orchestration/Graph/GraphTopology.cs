using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Orchestration.Validation;
using fuseraft.Orchestration.Workflow;
using Microsoft.Extensions.Logging;

namespace fuseraft.Orchestration.Graph;

/// <summary>
/// Descriptor for a parallel fan-out group triggered by a single source keyword.
/// Shared by <see cref="GraphTopology"/> (which resolves <see cref="MergeTargetId"/>) and
/// <c>ParallelFanOutExecutor</c> (which dispatches to <see cref="NodeIds"/> and merges back
/// into <see cref="MergeTargetId"/>).
/// </summary>
internal sealed class ParallelGroup
{
    public List<string>                     NodeIds              { get; }      = new();
    public string                           MergeTargetId        { get; set; } = string.Empty;
    public string                           MergeTargetName      { get; set; } = string.Empty;
    public IReadOnlyList<IRoutingValidator> Validators           { get; set; } = [];
    public bool                             RequireHumanApproval { get; set; }
}

/// <summary>
/// Computed graph topology for one <c>GraphOrchestrator.StreamAsync</c> call: back-edge
/// classification, per-node route tables, unconditional (no-keyword) routing, and parallel
/// fan-out group membership. Built once via <see cref="Build"/> at the start of each session
/// and treated as read-only for the rest of that session's lifetime — <c>GraphOrchestrator</c>
/// and its collaborators (<c>SubGraphExecutor</c>, <c>ParallelFanOutExecutor</c>) only read
/// from it after construction.
/// </summary>
internal sealed class GraphTopology
{
    /// <summary>
    /// Edges classified as back-edges by a single DFS from the entry node, keyed by
    /// "{From} {To}" with node IDs upper-invariant to match the case-insensitive node-ID
    /// comparisons used elsewhere.
    /// </summary>
    public HashSet<string> BackEdges { get; private set; } = [];

    public Dictionary<string, List<GraphEdgeConfig>> EdgesBySource { get; private set; } = [];

    /// <summary>Set of parallel node IDs — excluded from the MAF DAG.</summary>
    public HashSet<string> ParallelNodeIds { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, GraphNodeConfig> NodeById { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, AgentRouteTable> RouteTablesByNodeId { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Back-edge keyword → target node ID (null = terminal / session ends).</summary>
    public Dictionary<string, string?> BackEdgeDestinations { get; private set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Unconditional (no-keyword) forward routing, keyed by node ID.</summary>
    public Dictionary<string, RouteInfo> UnconditionalForwardRoutes { get; private set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string?> UnconditionalBackEdges { get; private set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, IReadOnlyList<IRoutingValidator>> UnconditionalBackEdgeValidators { get; private set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Parallel group map: "{sourceNodeId}::{keyword}" → descriptor for the fan-out group.</summary>
    public Dictionary<string, ParallelGroup> ParallelGroups { get; private set; } =
        new(StringComparer.OrdinalIgnoreCase);

    private ILogger _logger = null!;
    private const string TerminalSentinel = GraphOrchestrator.TerminalSentinel;

    /// <returns><c>true</c> when the edge from → to is a back-edge.</returns>
    public bool IsBackEdge(string from, string to) => BackEdges.Contains(EdgeKey(from, to));

    /// <summary>
    /// Computes the full topology for one session: back-edge classification, per-node route
    /// tables (also populating back-edge destinations, unconditional routing, and parallel
    /// groups), then post-hoc parallel-config validation warnings.
    /// </summary>
    public static GraphTopology Build(
        GraphConfig graphCfg,
        OrchestrationConfig config,
        Dictionary<string, GraphNodeConfig> nodeById,
        string entryNodeId,
        ILogger logger)
    {
        var topology = new GraphTopology { _logger = logger };

        topology.EdgesBySource = graphCfg.Edges
            .GroupBy(e => e.From, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        topology.BackEdges = ComputeBackEdges(entryNodeId, topology.EdgesBySource);

        topology.ParallelNodeIds = graphCfg.Nodes
            .Where(n => n.Parallel)
            .Select(n => n.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        topology.NodeById = nodeById;

        topology.BackEdgeDestinations[TerminalSentinel] = null;

        var tables = topology.BuildRouteTableForNode(graphCfg, nodeById, config);
        topology.AssignParallelGroups(tables);
        topology.WireBackEdges(graphCfg, nodeById, tables, config);
        topology.RouteTablesByNodeId = tables;

        topology.ValidateParallelConfig(graphCfg, nodeById);

        return topology;
    }

    /// <summary>
    /// Per-node route table construction. Iterates all graph edges and populates each source
    /// node's <see cref="AgentRouteTable"/> with forward routes, back-edge phase-break entries,
    /// parallel fan-out keywords, terminal validators, reviewer-type flags, and foreign-keyword
    /// sets. Also registers back-edge destinations in <see cref="BackEdgeDestinations"/> and
    /// parallel group membership in <see cref="ParallelGroups"/>.
    /// </summary>
    private Dictionary<string, AgentRouteTable> BuildRouteTableForNode(
        GraphConfig graphCfg,
        Dictionary<string, GraphNodeConfig> nodeById,
        OrchestrationConfig config)
    {
        var tables = new Dictionary<string, AgentRouteTable>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in graphCfg.Edges)
        {
            if (!tables.TryGetValue(edge.From, out var table))
                tables[edge.From] = table = new AgentRouteTable();

            var validators = BuildValidatorsFromNames(
                config,
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
                    if (!BackEdgeDestinations.ContainsKey(edge.Keyword))
                        BackEdgeDestinations[edge.Keyword] = edge.To.ToLowerInvariant();
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
                        if (!ParallelGroups.TryGetValue(groupKey, out var pg))
                            ParallelGroups[groupKey] = pg = new ParallelGroup
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

            table.TerminalValidators = BuildValidatorsFromNames(config, node.Validators!);
        }

        // Populate IsReviewerType from the explicit GraphNodeConfig.ReviewerType flag.
        foreach (var node in graphCfg.Nodes.Where(n => n.ReviewerType))
        {
            if (!tables.TryGetValue(node.Id, out var table))
                tables[node.Id] = table = new AgentRouteTable();

            table.IsReviewerType = true;
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

        return tables;
    }

    /// <summary>
    /// Back-edge destination resolution. Populates unconditional routing maps
    /// (<see cref="UnconditionalForwardRoutes"/>, <see cref="UnconditionalBackEdges"/>,
    /// <see cref="UnconditionalBackEdgeValidators"/>) and registers synthetic back-edge keywords
    /// in <see cref="BackEdgeDestinations"/> for nodes whose ALL outgoing edges carry no keyword.
    /// </summary>
    private void WireBackEdges(
        GraphConfig graphCfg,
        Dictionary<string, GraphNodeConfig> nodeById,
        Dictionary<string, AgentRouteTable> tables,
        OrchestrationConfig config)
    {
        // Populate unconditional routing for nodes whose ALL outgoing edges carry no keyword.
        // A node qualifies when it has exactly one no-keyword edge and zero keyword-based edges.
        foreach (var node in graphCfg.Nodes)
        {
            var outgoing = EdgesBySource.GetValueOrDefault(node.Id, []);
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
                config,
                uncEdge.AllValidators,
                uncEdge.RequiredCommandPattern,
                uncEdge.ShellFallbackPattern);

            if (IsBackEdge(node.Id, uncEdge.To))
            {
                var syntheticKw = $"__UNCOND_BACK:{node.Id.ToLowerInvariant()}";
                BackEdgeDestinations[syntheticKw] = uncEdge.To.ToLowerInvariant();
                UnconditionalBackEdges[node.Id]   = uncEdge.To.ToLowerInvariant();
                if (uncValidators.Count > 0)
                    UnconditionalBackEdgeValidators[node.Id] = uncValidators;
            }
            else
            {
                var targetNode    = nodeById.GetValueOrDefault(uncEdge.To);
                var nextAgentName = targetNode?.Agent ?? uncEdge.To;
                UnconditionalForwardRoutes[node.Id] = new RouteInfo(
                    uncEdge.To.ToLowerInvariant(),
                    nextAgentName,
                    uncValidators);
            }
        }
    }

    /// <summary>
    /// Parallel group membership assignment. Resolves the merge target for each parallel
    /// fan-out group by scanning the group's nodes' own forward routes, then logs a warning
    /// for any group whose merge target could not be determined.
    /// </summary>
    private void AssignParallelGroups(Dictionary<string, AgentRouteTable> tables)
    {
        // Resolve merge targets for parallel groups from the parallel nodes' own route tables.
        // The merge target is the first forward-route destination found in any of the group's nodes.
        foreach (var (groupKey, pg) in ParallelGroups)
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
                _logger.LogWarning(
                    "[GraphOrchestrator] Parallel group '{Key}' has no merge target — " +
                    "each parallel node must have at least one forward edge to the merge-target node.",
                    groupKey);
        }
    }

    // Shared with WorkflowOrchestrator via ValidatorRegistry — the two orchestrators resolve
    // per-edge validator names identically; see that class's doc comment for why
    // StrategyFactory.BuildValidators is not folded into the same helper.
    private static IReadOnlyList<IRoutingValidator> BuildValidatorsFromNames(
        OrchestrationConfig config,
        IReadOnlyList<string> names,
        string? requiredCommandPattern = null,
        string? shellFallbackPattern = null) =>
        ValidatorRegistry.BuildValidatorsFromNames(config, names, requiredCommandPattern, shellFallbackPattern);

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
                _logger.LogWarning(
                    "[GraphOrchestrator] Node '{NodeId}' is both Parallel and Terminal. " +
                    "Terminal is ignored on parallel nodes — they complete when they emit a forward-edge keyword.",
                    node.Id);

            // Parallel nodes that have no forward edges can never signal completion.
            var outgoing = EdgesBySource.GetValueOrDefault(node.Id, []);
            var fwdEdges = outgoing.Where(e => !IsBackEdge(node.Id, e.To)).ToList();
            if (fwdEdges.Count == 0)
                _logger.LogWarning(
                    "[GraphOrchestrator] Parallel node '{NodeId}' has no forward edges — " +
                    "it can never signal completion to its merge target. Add an outgoing edge to the merge-target node.",
                    node.Id);

            // All forward edges from a parallel node must point to the same merge target.
            var mergeTargets = fwdEdges
                .Select(e => e.To.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (mergeTargets.Count > 1)
                _logger.LogWarning(
                    "[GraphOrchestrator] Parallel node '{NodeId}' has forward edges to multiple targets " +
                    "({Targets}). All parallel nodes in a group must converge on a single merge-target node.",
                    node.Id, string.Join(", ", mergeTargets));

            // The merge target of a parallel node must not itself be Parallel.
            foreach (var targetId in mergeTargets)
            {
                if (nodeById.TryGetValue(targetId, out var targetNode) && targetNode.Parallel)
                    _logger.LogWarning(
                        "[GraphOrchestrator] Parallel node '{NodeId}' routes to '{TargetId}' which is also " +
                        "Parallel. Nested parallel fan-out is not supported — the merge target must be a normal node.",
                        node.Id, targetId);
            }
        }

        // Each parallel group that has no merge target resolved means the parallel nodes
        // had no route tables (missing agent or no forward edges). Already warned above;
        // log here for the group-level perspective.
        foreach (var (groupKey, pg) in ParallelGroups.Where(kv => string.IsNullOrEmpty(kv.Value.MergeTargetId)))
            _logger.LogWarning(
                "[GraphOrchestrator] Parallel group '{Key}' could not resolve a merge target. " +
                "The fan-out keyword will be treated as unroutable at runtime.",
                groupKey);
    }

    /// <summary>
    /// Classifies every edge reachable from the entry node as forward or back via a single
    /// DFS, using the standard definition: an edge is a back-edge only when its target is
    /// still on the current DFS stack (a real ancestor of the source) when the edge is
    /// explored. Everything else — tree edges, forward edges to already-finished descendants,
    /// and cross edges to already-finished nodes in another branch — is a forward edge for
    /// fuseraft's purposes (it does not close a cycle).
    /// </summary>
    /// <remarks>
    /// This replaces an earlier BFS-shortest-path-layer approximation (assign each node the
    /// layer of its first BFS encounter, classify an edge as back when target-layer &lt;=
    /// source-layer). That approximation misclassified a legitimate forward edge as a
    /// back-edge whenever two forward paths of different lengths converged on the same node
    /// (a "diamond": A→B→D and A→C→E→D), because the longer path's edge into D always landed
    /// on a layer &lt;= D's already-assigned (shorter-path) layer. DFS-based classification has
    /// no such failure mode since it reasons about actual ancestry, not path length.
    /// </remarks>
    internal static HashSet<string> ComputeBackEdges(
        string entryNodeId,
        Dictionary<string, List<GraphEdgeConfig>> edgesBySource)
    {
        var backEdges = new HashSet<string>();
        var state = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase); // 0=unvisited (absent), 1=on-stack, 2=done

        void Visit(string nodeId)
        {
            state[nodeId] = 1;
            foreach (var edge in edgesBySource.GetValueOrDefault(nodeId, []))
            {
                if (state.TryGetValue(edge.To, out var targetState))
                {
                    if (targetState == 1)
                        backEdges.Add(EdgeKey(nodeId, edge.To));
                    // targetState == 2 (done): forward/cross edge — not a back-edge.
                }
                else
                {
                    Visit(edge.To);
                }
            }
            state[nodeId] = 2;
        }

        Visit(entryNodeId);

        // Nodes unreachable from Entry shouldn't normally occur, but classify their
        // outgoing edges too so IsBackEdge has a defined answer for every edge in the graph.
        foreach (var nodeId in edgesBySource.Keys)
            if (!state.ContainsKey(nodeId))
                Visit(nodeId);

        return backEdges;
    }

    internal static string EdgeKey(string from, string to) =>
        $"{from.ToUpperInvariant()} {to.ToUpperInvariant()}";

    /// <summary>
    /// Resolves the starting node for a new phase-loop run: explicit resume hint (node ID or
    /// agent name) → back-edge/forward-edge keyword scan of prior history → last active agent
    /// name → configured entry node.
    /// </summary>
    public string DetermineStartNodeId(
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
                _logger.LogDebug(
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
                _logger.LogDebug(
                    "[GraphOrchestrator] DetermineStartNodeId: hint '{Hint}' is agent name → node '{NodeId}'",
                    resumeHint, hintNode.Id);
                return hintNode.Id.ToLowerInvariant();
            }

            _logger.LogWarning(
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

            foreach (var kw in BackEdgeDestinations.Keys)
            {
                if (kw == TerminalSentinel) continue;
                if (KeywordDetector.IsKeywordOnOwnLineStrict(msg.Content, kw) &&
                    BackEdgeDestinations.TryGetValue(kw, out var nextNode) &&
                    nextNode is not null)
                {
                    _logger.LogDebug(
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
                    _logger.LogDebug(
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
                _logger.LogDebug(
                    "[GraphOrchestrator] DetermineStartNodeId: agent-name fallback → node '{NodeId}' (agent '{Agent}')",
                    node.Id, node.Agent);
                return node.Id.ToLowerInvariant();
            }
        }

        // Priority 4: configured entry node.
        return defaultEntryNode;
    }
}
