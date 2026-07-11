using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
using fuseraft.Core.Exceptions;
using fuseraft.Core.Models;
using fuseraft.Orchestration.Workflow;

namespace fuseraft.Orchestration.Graph;

/// <summary>
/// Drives a parallel fan-out group triggered by a <c>Parallel: true</c> forward-edge keyword:
/// runs the group's validators and HITL gate, forks an isolated <see cref="AgentContext"/> per
/// branch node, runs each branch's own retry loop concurrently
/// (<see cref="RunSingleBranchAsync"/>), merges the branches back into the parent context, and
/// dispatches to the merge target. Shares <see cref="TurnExecutionHelpers"/> with
/// <c>GraphOrchestrator</c>'s sequential back-edge/forward-edge turn loop rather than
/// duplicating response-recording/validator/recovery-agent logic.
/// </summary>
internal sealed class ParallelFanOutExecutor(TurnServices services)
{
    /// <returns>
    /// A tuple of (shouldReturn, consecutiveFails). <c>shouldReturn=true</c> means the fan-out
    /// completed and merged — the caller must <c>return</c> from its own turn loop.
    /// <c>shouldReturn=false</c> means validator/HITL failure — the caller must <c>continue</c>.
    /// </returns>
    public async Task<(bool ShouldReturn, int ConsecutiveFails)> RunFanOutAsync(
        string nodeId,
        string agentName,
        string foundKeyword,
        ParallelGroup parallelGroup,
        string responseText,
        AgentContext ctx,
        IWorkflowContext wfCtx,
        Dictionary<string, AIAgent> agents,
        Dictionary<string, string> agentInstructions,
        Dictionary<string, AgentConfig> agentConfigs,
        GraphTopology topology,
        ConcurrentDictionary<string, bool> recoveryActivated,
        string sessionId,
        string task,
        Action<AgentContext, string> recordNodeState,
        int consecutiveFails,
        int maxRetries,
        AgentMessage agentMsg,
        CancellationToken ct)
    {
        var eventEmitter = services.EventEmitter;

        var (pgOk, pgErr, pgValidator) = await TurnExecutionHelpers.RunValidatorsAsync(
            parallelGroup.Validators, ctx.History, ct).ConfigureAwait(false);

        if (!pgOk)
        {
            consecutiveFails = Math.Min(consecutiveFails + 1, maxRetries - 1);
            TurnExecutionHelpers.RecordGovernanceViolation(agentName, pgValidator!, consecutiveFails, maxRetries, sessionId, services);

            if (consecutiveFails >= maxRetries)
                throw new ValidatorStuckException(agentName, pgValidator!, consecutiveFails, pgErr!);

            await TurnExecutionHelpers.EmitAndInjectValidationFailureAsync(
                agentName, foundKeyword, pgValidator!, pgErr!, responseText, consecutiveFails, maxRetries, ctx, ct, services);
            return (false, consecutiveFails);
        }

        if (parallelGroup.RequireHumanApproval && services.HumanApprovalService is not null)
        {
            var (pgApproved, pgApprovedFails) = await TurnExecutionHelpers.ApplyHumanApprovalGateAsync(
                foundKeyword, agentName, parallelGroup.MergeTargetName,
                $"Parallel dispatch to [{string.Join(", ", parallelGroup.NodeIds)}] was blocked by the operator. " +
                $"Continue your work or await further instructions.",
                consecutiveFails, ctx, ct, services);
            consecutiveFails = pgApprovedFails;
            if (!pgApproved) return (false, consecutiveFails);
        }

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync(EventTypes.ParallelStart,
                agent:   agentName,
                payload: new { keyword = foundKeyword, nodes = parallelGroup.NodeIds, merge_target = parallelGroup.MergeTargetName });

        int forkPoint = ctx.History.Count;
        var forkPairs = parallelGroup.NodeIds.Select((targetNodeId, branchIndex) =>
        {
            var targetNode      = topology.NodeById[targetNodeId];
            var targetAgentName = targetNode.Agent;
            return (
                NodeId:       targetNodeId,
                AgentName:    targetAgentName,
                Agent:        agents[targetAgentName],
                Instructions: agentInstructions.GetValueOrDefault(targetAgentName, string.Empty),
                AgentCfg:     agentConfigs.GetValueOrDefault(targetAgentName) ?? new AgentConfig(),
                RouteTable:   topology.RouteTablesByNodeId.GetValueOrDefault(targetNodeId, new AgentRouteTable()),
                BranchIndex:  branchIndex,
                Fork:         ForkContext(ctx, branchIndex));
        }).ToList();

        var parallelTasks = forkPairs
            .Select(async fp =>
            {
                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync(EventTypes.ParallelBranchStart,
                        agent:   fp.AgentName,
                        payload: new { node = fp.NodeId });
                try
                {
                    await RunSingleBranchAsync(
                        fp.NodeId, fp.AgentName, fp.Agent, fp.Instructions, fp.AgentCfg,
                        fp.RouteTable, fp.Fork, ct, agents, agentInstructions, agentConfigs,
                        recoveryActivated, sessionId, task);
                    if (eventEmitter is not null)
                        _ = eventEmitter.EmitAsync(EventTypes.ParallelBranchEnd,
                            agent:   fp.AgentName,
                            payload: new { node = fp.NodeId });
                }
                catch (Exception branchEx)
                {
                    if (eventEmitter is not null)
                        _ = eventEmitter.EmitAsync(EventTypes.ParallelBranchError,
                            agent:   fp.AgentName,
                            payload: new { node = fp.NodeId, error = branchEx.Message });
                    throw;
                }
            })
            .ToArray();

        await Task.WhenAll(parallelTasks).ConfigureAwait(false);

        MergeParallelContexts(ctx, forkPoint,
            forkPairs.Select(fp => (fp.NodeId, fp.AgentName, fp.Fork, fp.BranchIndex)).ToList());

        consecutiveFails = 0;
        ctx.LastKeyword  = foundKeyword;

        recordNodeState(ctx, parallelGroup.MergeTargetName);

        if (eventEmitter is not null)
        {
            await eventEmitter.EmitAsync(EventTypes.ParallelMerge,
                agent:   agentName,
                payload: new { keyword = foundKeyword, to = parallelGroup.MergeTargetName });

            await eventEmitter.EmitAsync(EventTypes.StateAdvanced,
                agent: agentName,
                turn:  agentMsg.TurnIndex,
                payload: new { version = ctx.CurrentState.Version, parallel_merge = true, to = parallelGroup.MergeTargetName });
        }

        ctx.History.Add(new ChatMessage(ChatRole.User,
            $"[fuseraft: parallel workers complete → {parallelGroup.MergeTargetName}]"));

        await wfCtx.SendMessageAsync(ctx, parallelGroup.MergeTargetId, ct).ConfigureAwait(false);
        return (true, consecutiveFails);
    }

    /// <summary>
    /// Executes a single parallel node's agent retry loop against an isolated fork of the
    /// shared <see cref="AgentContext"/>. Unlike <c>GraphOrchestrator.RunNodeExecutorAsync</c>,
    /// this method does not call <c>wfCtx.SendMessageAsync</c> or <c>YieldOutputAsync</c> — it
    /// simply returns when the agent emits a valid forward-edge keyword, leaving the routing
    /// decision to the parent fan-out that called it.
    /// </summary>
    private async Task RunSingleBranchAsync(
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
        Dictionary<string, AgentConfig> agentConfigs,
        ConcurrentDictionary<string, bool> recoveryActivated,
        string sessionId,
        string task)
    {
        services.OnAgentStarting?.Invoke(agentName);
        services.AgentFactory.OnAgentTurnStarting();

        var eventEmitter = services.EventEmitter;

        int maxRetries       = services.Config.Selection.Graph?.MaxRetries ?? GraphOrchestrator.DefaultMaxRetries;
        int maxTotalTurns    = maxRetries * (services.Config.Selection.Graph?.MaxTotalTurnsMultiplier ?? 10);
        int consecutiveFails = 0;
        int totalTurns       = 0;

        while (true)
        {
            if (totalTurns++ >= maxTotalTurns)
                throw new ValidatorStuckException(agentName, "total-turns", totalTurns,
                    $"Parallel node '{nodeId}' ({agentName}) exceeded {maxTotalTurns} total turns without completing.");

            IEnumerable<ChatMessage> context;
            if (services.ContextPipeline is { } contextPipeline)
            {
                var assembled = await contextPipeline.AssembleAsync(
                    new AgentExecutionRequest
                    {
                        AgentName     = agentName,
                        Task          = task,
                        SharedHistory = ctx.History,
                        AgentConfig   = agentCfg,
                        SessionId     = sessionId,
                    }, ct);
                context = assembled.Messages;
                await TurnExecutionHelpers.EmitContextWindowWarnAsync(agentName, agentCfg, assembled.Messages, ctx, services);
                if (eventEmitter is not null)
                    await TurnExecutionHelpers.EmitContextAssemblyAsync(eventEmitter, assembled.Metrics, ctx.TurnIndex);
            }
            else
            {
                var filtered = ContextWindowFilter.Apply(ctx.History, agentCfg.ContextWindow);
                await TurnExecutionHelpers.EmitContextWindowWarnAsync(agentName, agentCfg, filtered, ctx, services);
                context = !string.IsNullOrWhiteSpace(instructions)
                    ? [new ChatMessage(ChatRole.System, instructions), .. filtered]
                    : filtered;
            }

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync(EventTypes.TurnStart, agent: agentName, turn: ctx.TurnIndex);

            AgentResponse response;
            try
            {
                response = services.GovernanceKernel?.CircuitBreaker is { } cb
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
                }

                if (consecutiveFails >= maxRetries)
                    throw new ValidatorStuckException(agentName, "streaming-timeout",
                        consecutiveFails, tex.Message);

                ctx.History.Add(new ChatMessage(ChatRole.User,
                    "TIMEOUT: Response timed out. Resume from where you left off — prior tool results are in context. " +
                    "Do not re-research. Call write_file or shell_run now, or emit the handoff keyword if all work is complete.\n\n" +
                    $"Valid keywords: {CorrectionEngine.BuildValidKeywordList(routeTable)}"));
                continue;
            }

            services.Logger.LogDebug(
                "[{Agent}] Parallel node '{NodeId}' turn {Turn} — response: {Preview}",
                agentName, nodeId, totalTurns,
                StringHelpers.Truncate((response.Text ?? "").Replace('\n', ' '), 200));

            var agentMsg    = await TurnExecutionHelpers.RecordAndEmitAsync(response, agentName, ctx, ct, sessionId, services);
            var responseText = response.Text ?? string.Empty;

            var handoffArgKeyword = KeywordDetector.ExtractHandoffToolCallKeyword(response.Messages, routeTable);
            var allKeywords       = handoffArgKeyword is not null
                ? (IReadOnlyList<string>)[handoffArgKeyword]
                : KeywordDetector.DetectKeywords(responseText, routeTable);

            if (allKeywords.Count > 1)
            {
                consecutiveFails++;

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync(EventTypes.MultiKeyword,
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
                await eventEmitter.EmitAsync(EventTypes.KeywordDetected,
                    agent:   agentName,
                    turn:    agentMsg.TurnIndex,
                    payload: new { keyword = foundKeyword, parallel = true });

            // Back-edge keywords from parallel nodes are a config error — treat as no keyword.
            if (foundKeyword is not null && routeTable.PhaseBreakKeywords.Contains(foundKeyword))
            {
                services.Logger.LogError(
                    "[GraphOrchestrator] Parallel node '{NodeId}' emitted back-edge keyword '{Kw}' — " +
                    "back-edges from parallel nodes are not supported. Treating as no-keyword.",
                    nodeId, foundKeyword);
                foundKeyword = null;
            }

            if (foundKeyword is not null && routeTable.Routes.TryGetValue(foundKeyword, out var route))
            {
                var (ok, errMsg, failingValidator) = await TurnExecutionHelpers.RunValidatorsAsync(
                    route.Validators, ctx.History, ct).ConfigureAwait(false);

                if (ok)
                {
                    if (route.Validators.Count > 0)
                        services.GovernanceKernel?.SloEngine.Get("policy-compliance")?.Record(1.0);

                    consecutiveFails = 0;
                    ctx.LastKeyword  = foundKeyword;

                    if (eventEmitter is not null)
                        await eventEmitter.EmitAsync(EventTypes.AgentRouted,
                            agent:   agentName,
                            turn:    agentMsg.TurnIndex,
                            payload: new { keyword = foundKeyword, to = route.NextExecutorName, parallel = true });

                    return; // fan-out complete for this worker; parent merges results
                }

                consecutiveFails = Math.Min(consecutiveFails + 1, maxRetries - 1);
                TurnExecutionHelpers.RecordGovernanceViolation(agentName, failingValidator!, consecutiveFails, maxRetries, sessionId, services);

                if (consecutiveFails >= maxRetries)
                    throw new ValidatorStuckException(agentName, failingValidator!, consecutiveFails, errMsg!);

                var fwdEdgeKey = $"{nodeId}::{foundKeyword}::parallel";
                if (consecutiveFails >= 2
                    && route.RecoveryAgent is not null
                    && !recoveryActivated.ContainsKey(fwdEdgeKey)
                    && agents.TryGetValue(route.RecoveryAgent, out var fwdRecoveryAgt))
                {
                    recoveryActivated.TryAdd(fwdEdgeKey, true);
                    await TurnExecutionHelpers.InvokeRecoveryAgentAsync(
                        route.RecoveryAgent, fwdRecoveryAgt,
                        agentInstructions, agentConfigs,
                        $"'{failingValidator}' failed {consecutiveFails}× on edge '{foundKeyword}'",
                        errMsg!, foundKeyword, ctx, ct, sessionId, task, services);
                    consecutiveFails = 0;
                    continue;
                }

                await TurnExecutionHelpers.EmitAndInjectValidationFailureAsync(
                    agentName, foundKeyword, failingValidator!, errMsg!, responseText, consecutiveFails, maxRetries, ctx, ct, services);
                continue;
            }

            // No keyword matched.
            consecutiveFails++;

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync(EventTypes.KeywordNotFound,
                    agent:   agentName,
                    turn:    agentMsg.TurnIndex,
                    payload: new { consecutive = consecutiveFails, source = "graph_orchestrator" });

            int histBefore2 = ctx.History.Count;
            await CorrectionEngine.InjectNoKeywordCorrection(
                ctx.History, responseText, agentName, consecutiveFails, routeTable, eventEmitter,
                agentMsg.ToolCalls);
            await TurnExecutionHelpers.PersistCorrectionsAsync(ctx, histBefore2, ct).ConfigureAwait(false);

            if (consecutiveFails >= maxRetries)
                throw new ValidatorStuckException(agentName, "no-keyword", consecutiveFails,
                    $"Parallel node '{nodeId}' ({agentName}) emitted no routing keyword " +
                    $"for {consecutiveFails} consecutive turns.");
        }
    }

    /// <summary>
    /// Creates an isolated <see cref="AgentContext"/> snapshot for a parallel worker. The fork
    /// shares the same <see cref="AgentContext.MessageSink"/> (already thread-safe) but gets
    /// its own <see cref="AgentContext.History"/> copy so concurrent workers cannot corrupt
    /// each other's conversation state.
    /// </summary>
    internal static AgentContext ForkContext(AgentContext parent, int branchIndex = 0)
    {
        var fork = new AgentContext
        {
            MessageSink      = parent.MessageSink,
            TurnIndex        = parent.TurnIndex + branchIndex * GraphOrchestrator.BranchTurnIndexStride,
            CumulativeTokens = parent.CumulativeTokens,
            CurrentState     = parent.CurrentState,
        };
        fork.History.AddRange(parent.History);
        return fork;
    }

    /// <summary>
    /// Merges the post-fork output of each parallel worker back into the parent context. For
    /// each child, a labelled header is injected followed by all messages appended after
    /// <paramref name="forkPoint"/>. Token counts are summed; the turn count each branch
    /// actually consumed is recovered by subtracting its
    /// <see cref="GraphOrchestrator.BranchTurnIndexStride"/> offset back out, and the parent's
    /// <see cref="AgentContext.TurnIndex"/> advances by whichever branch took the most turns —
    /// a normal, non-inflated continuation point for turns recorded after the merge.
    /// </summary>
    internal static void MergeParallelContexts(
        AgentContext parent,
        int forkPoint,
        IReadOnlyList<(string NodeId, string AgentName, AgentContext Fork, int BranchIndex)> children)
    {
        int startTurnIndex  = parent.TurnIndex;
        int maxTurnsTaken   = 0;
        int totalTokenDelta = 0;

        foreach (var (nodeId, agentName, fork, branchIndex) in children)
        {
            totalTokenDelta += fork.CumulativeTokens - parent.CumulativeTokens;
            var turnsTaken = fork.TurnIndex - (startTurnIndex + branchIndex * GraphOrchestrator.BranchTurnIndexStride);
            maxTurnsTaken  = Math.Max(maxTurnsTaken, turnsTaken);

            parent.History.Add(new ChatMessage(ChatRole.User,
                $"[fuseraft: parallel result from {agentName} (node: {nodeId})]"));

            for (int i = forkPoint; i < fork.History.Count; i++)
                parent.History.Add(fork.History[i]);
        }

        parent.CumulativeTokens += Math.Max(0, totalTokenDelta);
        parent.TurnIndex         = startTurnIndex + maxTurnsTaken;
    }
}
