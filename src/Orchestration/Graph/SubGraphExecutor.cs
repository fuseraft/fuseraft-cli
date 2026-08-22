using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Orchestration.Workflow;

namespace fuseraft.Orchestration.Graph;

/// <summary>
/// Executes a nested <see cref="GraphOrchestrator"/> (or <c>MapReduce</c>/<c>ScatterGather</c>
/// orchestrator) for a <c>SubGraphId</c> node. All shared services are forwarded from the
/// parent so governance, audit, and context pipelines remain unified. Messages emitted by the
/// sub-orchestrator are forwarded to <c>ctx.MessageSink</c> so they appear in the parent
/// session transcript; the sub-orchestrator's final assistant message is injected into
/// <c>ctx.History</c> so the parent's keyword detector can route normally.
/// </summary>
internal sealed class SubGraphExecutor(TurnServices services, ILoggerFactory? loggerFactory)
{
    public async Task RunSubGraphNodeAsync(
        string nodeId,
        string subGraphId,
        bool isTerminal,
        AgentRouteTable routeTable,
        AgentContext ctx,
        IWorkflowContext wfCtx,
        GraphTopology topology,
        string sessionId,
        string task,
        Action<AgentContext, string> recordNodeState,
        CancellationToken ct)
    {
        var config   = services.Config;
        var logger   = services.Logger;
        var eventEmitter = services.EventEmitter;

        var graphCfg = config.Selection.Graph!;
        var subSpec  = graphCfg.SubGraphs![subGraphId];

        logger.LogInformation(
            "[GraphOrchestrator] Node '{NodeId}' executing sub-graph '{SubGraphId}' (type: {Type}).",
            nodeId, subGraphId,
            subSpec.IsMapReduce ? OrchestratorTypes.MapReduce
                : subSpec.IsScatterGather ? OrchestratorTypes.ScatterGather
                : OrchestratorTypes.Graph);

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync(EventTypes.AgentStart,
                agent: $"[SubGraph:{subGraphId}]",
                turn:  ctx.TurnIndex);

        IOrchestrator subOrchestrator;

        if (subSpec.IsMapReduce)
        {
            var subConfig = config with
            {
                Selection = config.Selection with
                {
                    Type      = OrchestratorTypes.MapReduce,
                    Graph     = null,
                    MapReduce = subSpec.MapReduce,
                }
            };
            var mrLogger = loggerFactory?.CreateLogger<MapReduceOrchestrator>()
                ?? (ILogger<MapReduceOrchestrator>)Microsoft.Extensions.Logging.Abstractions.NullLogger<MapReduceOrchestrator>.Instance;
            subOrchestrator = new MapReduceOrchestrator(
                subConfig, services.AgentFactory, mrLogger,
                services.ChangeTracker, eventEmitter, services.GovernanceKernel,
                services.HumanApprovalService, services.ContextPipeline, services.RepositoryKnowledgeStore);
        }
        else if (subSpec.IsScatterGather)
        {
            var subConfig = config with
            {
                Selection = config.Selection with
                {
                    Type          = OrchestratorTypes.ScatterGather,
                    Graph         = null,
                    ScatterGather = subSpec.ScatterGather,
                }
            };
            var sgLogger = loggerFactory?.CreateLogger<ScatterGatherOrchestrator>()
                ?? (ILogger<ScatterGatherOrchestrator>)Microsoft.Extensions.Logging.Abstractions.NullLogger<ScatterGatherOrchestrator>.Instance;
            subOrchestrator = new ScatterGatherOrchestrator(
                subConfig, services.AgentFactory, sgLogger,
                services.ChangeTracker, eventEmitter, services.GovernanceKernel,
                services.HumanApprovalService, services.ContextPipeline, services.RepositoryKnowledgeStore);
        }
        else
        {
            var subConfig = config with
            {
                Selection = config.Selection with
                {
                    Type  = OrchestratorTypes.Graph,
                    Graph = subSpec.Graph,
                }
            };
            subOrchestrator = new GraphOrchestrator(
                subConfig, services.AgentFactory, logger,
                services.ChangeTracker, eventEmitter, services.GovernanceKernel,
                services.HumanApprovalService, services.ContextPipeline, services.RepositoryKnowledgeStore);
        }

        subOrchestrator.SetSessionId(sessionId);

        // Reconstruct the task text from the head of the shared history.
        int firstUserIdx = ctx.History.FindIndex(m => m.Role == ChatRole.User);
        var subTask = firstUserIdx >= 0
            ? ctx.History[firstUserIdx].Contents.OfType<TextContent>().FirstOrDefault()?.Text ?? task
            : task;

        // Pass parent context accumulated after the original task so sub-graph agents
        // can see prior phase outputs, handoff notes, and tool results.
        IReadOnlyList<AgentMessage>? subPriorHistory = null;
        if (firstUserIdx >= 0 && firstUserIdx + 1 < ctx.History.Count)
        {
            subPriorHistory = ctx.History
                .Skip(firstUserIdx + 1)
                .Select((m, i) => new AgentMessage
                {
                    Role      = m.Role == ChatRole.User ? "user" : "assistant",
                    Content   = string.Concat(m.Contents.OfType<TextContent>().Select(t => t.Text)),
                    AgentName = m.AuthorName ?? string.Empty,
                    TurnIndex = i,
                })
                .ToList();
        }

        // Stream the sub-orchestrator and collect messages.
        var subMessages   = new List<AgentMessage>();
        string? lastText  = null;
        string? lastAgent = null;

        await foreach (var msg in subOrchestrator.StreamAsync(subTask, subPriorHistory, ct).ConfigureAwait(false))
        {
            await ctx.MessageSink.WriteAsync(msg, ct).ConfigureAwait(false);
            subMessages.Add(msg);

            if (string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                lastText  = msg.Content;
                lastAgent = msg.AgentName;
            }

            ctx.TurnIndex        = Math.Max(ctx.TurnIndex, msg.TurnIndex + 1);
            ctx.CumulativeTokens += msg.Usage?.TotalTokens ?? 0;
        }

        if (lastText is null)
        {
            logger.LogWarning(
                "[GraphOrchestrator] Sub-graph '{SubGraphId}' produced no assistant messages.",
                subGraphId);
        }

        // Inject the sub-graph's terminal output into the parent history so the parent
        // orchestrator can detect routing keywords from it.
        var syntheticContent = lastText ?? $"[sub-graph '{subGraphId}' completed with no output]";
        var syntheticMsg     = new ChatMessage(ChatRole.Assistant, syntheticContent)
        {
            AuthorName = lastAgent ?? $"SubGraph:{subGraphId}"
        };
        ctx.History.Add(syntheticMsg);

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync(EventTypes.AgentEnd,
                agent: $"[SubGraph:{subGraphId}]",
                turn:  ctx.TurnIndex);

        // Terminal sub-graph node: end the session.
        if (isTerminal)
        {
            ctx.LastKeyword = GraphOrchestrator.TerminalSentinel;
            recordNodeState(ctx, nodeId);
            await wfCtx.YieldOutputAsync(ctx, ct).ConfigureAwait(false);
            return;
        }

        // Keyword detection on the sub-graph's final output for forward-edge routing.
        // Tool-call keyword detection requires raw ChatMessages which the sub-orchestrator
        // does not expose; fall back to text-based detection on the terminal output.
        var allKeywords = KeywordDetector.DetectKeywords(syntheticContent, routeTable);

        string? foundKeyword = allKeywords.Count == 1 ? allKeywords[0] : null;

        // Back-edge keyword.
        if (foundKeyword is not null && routeTable.PhaseBreakKeywords.Contains(foundKeyword))
        {
            ctx.LastKeyword = foundKeyword;
            recordNodeState(ctx, topology.BackEdgeDestinations.TryGetValue(foundKeyword, out var bd) ? bd ?? nodeId : nodeId);

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync(EventTypes.StateAdvanced,
                    agent: $"[SubGraph:{subGraphId}]",
                    turn:  ctx.TurnIndex,
                    payload: new { version = ctx.CurrentState.Version, phase_break = foundKeyword });

            await wfCtx.YieldOutputAsync(ctx, ct).ConfigureAwait(false);
            return;
        }

        // Forward-edge keyword.
        if (foundKeyword is not null && routeTable.Routes.TryGetValue(foundKeyword, out var route))
        {
            ctx.LastKeyword = foundKeyword;
            recordNodeState(ctx, route.NextExecutorName);

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync(EventTypes.AgentRouted,
                    agent:   $"[SubGraph:{subGraphId}]",
                    turn:    ctx.TurnIndex,
                    payload: new { keyword = foundKeyword, to = route.NextExecutorName });

            ctx.History.Add(new ChatMessage(ChatRole.User,
                $"[fuseraft: SubGraph:{subGraphId} → {route.NextExecutorName}]"));

            await wfCtx.SendMessageAsync(ctx, route.NextExecutorId, ct).ConfigureAwait(false);
            return;
        }

        // No keyword — if there are no keyword routes at all, treat as unconditional.
        bool hasKeywordRoutes = routeTable.Routes.Count > 0 || routeTable.PhaseBreakKeywords.Count > 0;
        if (!hasKeywordRoutes)
        {
            if (topology.UnconditionalForwardRoutes.TryGetValue(nodeId, out var autoRoute))
            {
                ctx.LastKeyword = null;
                recordNodeState(ctx, autoRoute.NextExecutorName);
                ctx.History.Add(new ChatMessage(ChatRole.User,
                    $"[fuseraft: SubGraph:{subGraphId} → {autoRoute.NextExecutorName}]"));
                await wfCtx.SendMessageAsync(ctx, autoRoute.NextExecutorId, ct).ConfigureAwait(false);
                return;
            }
        }

        // Sub-graph produced no recognisable keyword — log and terminate the node gracefully.
        logger.LogWarning(
            "[GraphOrchestrator] Sub-graph node '{NodeId}' produced no routing keyword. " +
            "Treating as terminal. Ensure the sub-graph's terminal agent emits a valid keyword.",
            nodeId);

        ctx.LastKeyword = GraphOrchestrator.TerminalSentinel;
        recordNodeState(ctx, nodeId);
        await wfCtx.YieldOutputAsync(ctx, ct).ConfigureAwait(false);
    }
}
