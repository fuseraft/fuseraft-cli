using AgentGovernance;
using AgentGovernance.Audit;
using AgentGovernance.Sre;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
using fuseraft.Core.Exceptions;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Orchestration.Workflow;
using AgentFactory = fuseraft.Infrastructure.Agents.AgentFactory;

namespace fuseraft.Orchestration.Graph;

/// <summary>
/// Bundle of collaborators fixed for the lifetime of one <c>GraphOrchestrator</c> instance
/// (built once from its primary-constructor parameters), threaded through every
/// <see cref="TurnExecutionHelpers"/> call instead of each method taking 8-10 loose
/// parameters. <c>SessionId</c>/<c>Task</c> are deliberately excluded — those mutate
/// post-construction via <c>SetSessionId</c>/<c>StreamAsync</c>, so callers pass them as
/// explicit per-call parameters instead.
/// </summary>
internal sealed record TurnServices(
    OrchestrationConfig Config,
    AgentFactory AgentFactory,
    ILogger<GraphOrchestrator> Logger,
    EventEmitter? EventEmitter,
    GovernanceKernel? GovernanceKernel,
    IContextAssemblyPipeline? ContextPipeline,
    ChangeTracker? ChangeTracker,
    fuseraft.Infrastructure.Repository.RepositoryKnowledgeStore? RepositoryKnowledgeStore,
    IHumanApprovalService? HumanApprovalService,
    Action<string>? OnAgentStarting,
    Action<string, int, int>? OnTokenBudgetWarning);

/// <summary>
/// Turn-execution helpers shared by <c>GraphOrchestrator</c>'s sequential turn loop
/// (<c>RunNodeExecutorAsync</c>/<c>HandleBackEdgeAsync</c>/<c>EvaluateRouteAsync</c>) and
/// <c>ParallelFanOutExecutor</c>'s per-branch loop. Extracted because both callers need the
/// same response-recording, validator-execution, HITL-gating, recovery-agent, and
/// governance-audit logic — mirrors the explicit-parameter <c>internal static class</c>
/// pattern already used by <see cref="CorrectionEngine"/>/<see cref="KeywordDetector"/>.
/// </summary>
internal static class TurnExecutionHelpers
{
    public static async Task<(bool ok, string? error, string? validatorName)> RunValidatorsAsync(
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

    public static async ValueTask PersistCorrectionsAsync(
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

    public static Task EmitContextAssemblyAsync(
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

    /// <summary>
    /// Emits a <c>context_window_warn</c> event when the filtered message count is
    /// approaching the configured context-cap fraction. No-ops when the event emitter is
    /// null or the context window is not configured.
    /// </summary>
    public static async Task EmitContextWindowWarnAsync(
        string agentName, AgentConfig agentCfg, IReadOnlyList<ChatMessage> filtered, AgentContext ctx,
        TurnServices services)
    {
        if (services.EventEmitter is not { } eventEmitter) return;
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

    /// <summary>
    /// Emits a <c>validation_fail</c> event, injects a correction message into history via
    /// <see cref="CorrectionEngine.InjectValidationError"/>, and persists the injected message
    /// to the message sink. Called from every validation-failure path in the turn loop.
    /// </summary>
    public static async Task EmitAndInjectValidationFailureAsync(
        string agentName,
        string keyword,
        string validatorName,
        string errMsg,
        string responseText,
        int consecutiveFails,
        int maxRetries,
        AgentContext ctx,
        CancellationToken ct,
        TurnServices services)
    {
        if (services.EventEmitter is { } eventEmitter)
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
        await CorrectionEngine.InjectValidationError(
            ctx.History, errMsg, consecutiveFails, responseText, keyword, services.EventEmitter, maxRetries);
        await PersistCorrectionsAsync(ctx, histBefore, ct).ConfigureAwait(false);
    }

    public static void RecordGovernanceViolation(
        string agentName,
        string validatorName,
        int consecutiveCount,
        int maxRetries,
        string sessionId,
        TurnServices services)
    {
        if (services.GovernanceKernel is not { } governanceKernel) return;

        var agentDid = services.AgentFactory.GetDid(agentName);
        governanceKernel.AuditEmitter.Emit(
            GovernanceEventType.PolicyViolation,
            agentId:   agentDid,
            sessionId: sessionId,
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

    /// <summary>
    /// HITL approval prompt and approval branching. When the human-approval service rejects
    /// the route, injects a blocked-route message into history, persists it to the message
    /// sink, and resets <paramref name="consecutiveFails"/> to zero.
    /// </summary>
    /// <returns>
    /// A tuple of (approved, updated consecutiveFails). When <c>approved</c> is <c>false</c>
    /// the caller must <c>continue</c> the turn loop.
    /// </returns>
    public static async Task<(bool Approved, int ConsecutiveFails)> ApplyHumanApprovalGateAsync(
        string keyword,
        string agentName,
        string targetName,
        string blockedMessage,
        int consecutiveFails,
        AgentContext ctx,
        CancellationToken ct,
        TurnServices services)
    {
        var approved = await services.HumanApprovalService!.PromptRouteApprovalAsync(
            keyword, agentName, targetName);

        if (services.EventEmitter is { } eventEmitter)
            _ = eventEmitter.EmitAsync(approved ? EventTypes.HitlApproved : EventTypes.HitlRejected,
                agent:   agentName,
                payload: new { keyword, target = targetName });

        if (!approved)
        {
            ctx.History.Add(new ChatMessage(ChatRole.User, blockedMessage));
            consecutiveFails = 0;
            int histBeforeBlocked = ctx.History.Count - 1;
            await PersistCorrectionsAsync(ctx, histBeforeBlocked, ct).ConfigureAwait(false);
        }
        return (approved, consecutiveFails);
    }

    public static async Task<AgentMessage> RecordAndEmitAsync(
        AgentResponse response,
        string agentName,
        AgentContext ctx,
        CancellationToken ct,
        string sessionId,
        TurnServices services)
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

        var warnThreshold = services.Config.WarnTurnTokens;
        if (warnThreshold > 0 && agentMsg.Usage?.InputTokens is { } inputToks && inputToks > warnThreshold)
            services.OnTokenBudgetWarning?.Invoke(agentName, inputToks, warnThreshold);

        // Stream before budget check — work was done and tokens already consumed.
        await ctx.MessageSink.WriteAsync(agentMsg, ct).ConfigureAwait(false);

        if (services.Config.MaxTotalTokens is { } limit && ctx.CumulativeTokens > limit)
            throw new BudgetExceededException(ctx.CumulativeTokens, limit);

        if (services.EventEmitter is { } eventEmitter)
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

            // Emit reasoning content when the model produced any.
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
                    agent:   agentName,
                    turn:    agentMsg.TurnIndex,
                    payload: new { text = truncated }).ConfigureAwait(false);
            }
        }

        if (services.ChangeTracker is { } changeTracker)
        {
            try { await changeTracker.FlushTurnAsync(agentName, agentMsg.TurnIndex, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                services.Logger.LogWarning(ex,
                    "ChangeTracker flush failed for turn {Turn} ({Agent})",
                    agentMsg.TurnIndex, agentName);
            }
        }

        // Persist entity-scoped findings from tool calls for future session retrieval.
        if (services.RepositoryKnowledgeStore is { } repositoryKnowledgeStore && !string.IsNullOrEmpty(sessionId))
        {
            try
            {
                var observations = ObservationExtractor.Extract(
                    (IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>)response.Messages,
                    agentName, agentMsg.TurnIndex);
                foreach (var obs in observations)
                {
                    if (string.IsNullOrWhiteSpace(obs.Entity)) continue;
                    await repositoryKnowledgeStore.AddAsync(new RepositoryKnowledgeFinding
                    {
                        Entity     = obs.Entity!,
                        Finding    = obs.Finding,
                        Source     = sessionId,
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

    /// <summary>
    /// Invokes a recovery agent for one intervention turn and appends its response to shared
    /// history. Best-effort — exceptions are swallowed so the caller's retry loop continues
    /// normally even when the recovery agent itself fails.
    /// </summary>
    public static async Task InvokeRecoveryAgentAsync(
        string recoveryAgentName,
        AIAgent recoveryAgent,
        Dictionary<string, string> agentInstructions,
        Dictionary<string, AgentConfig> agentConfigs,
        string reason,
        string validatorError,
        string triggeringKeyword,
        AgentContext ctx,
        CancellationToken ct,
        string sessionId,
        string task,
        TurnServices services)
    {
        var recoveryCfg = agentConfigs.GetValueOrDefault(recoveryAgentName) ?? new AgentConfig();
        var recoveryInstructions = agentInstructions.GetValueOrDefault(recoveryAgentName, string.Empty);

        ctx.History.Add(new ChatMessage(ChatRole.User,
            $"RECOVERY ACTIVATED: '{recoveryAgentName}' called in — {reason}.\n\n" +
            $"  1. changes_read_latest — review what was attempted.\n" +
            $"  2. Fix the problem described below.\n" +
            $"  3. The pipeline will retry '{triggeringKeyword}' after this turn.\n\n" +
            $"Failure: {validatorError}"));

        if (services.EventEmitter is { } startEmitter)
            await startEmitter.EmitAsync(EventTypes.RecoveryActivated,
                agent: recoveryAgentName,
                payload: new { reason, keyword = triggeringKeyword });

        try
        {
            IEnumerable<ChatMessage> context;
            if (services.ContextPipeline is { } contextPipeline)
            {
                var assembled = await contextPipeline.AssembleAsync(
                    new AgentExecutionRequest
                    {
                        AgentName     = recoveryAgentName,
                        Task          = task,
                        SharedHistory = ctx.History,
                        AgentConfig   = recoveryCfg,
                        SessionId     = sessionId,
                    }, ct);
                context = assembled.Messages;
                if (services.EventEmitter is { } assembledEmitter)
                    await EmitContextAssemblyAsync(assembledEmitter, assembled.Metrics, ctx.TurnIndex);
            }
            else
            {
                var filtered = ContextWindowFilter.Apply(ctx.History, recoveryCfg.ContextWindow);
                context = !string.IsNullOrWhiteSpace(recoveryInstructions)
                    ? [new ChatMessage(ChatRole.System, recoveryInstructions), .. filtered]
                    : filtered;
            }

            var response = services.GovernanceKernel?.CircuitBreaker is { } cb
                ? await cb.ExecuteAsync(() => recoveryAgent.RunAsync(context, null, null, ct)).ConfigureAwait(false)
                : await recoveryAgent.RunAsync(context, null, null, ct).ConfigureAwait(false);

            await RecordAndEmitAsync(response, recoveryAgentName, ctx, ct, sessionId, services);
        }
        catch (Exception ex)
        {
            services.Logger.LogWarning(ex,
                "[GraphOrchestrator] Recovery agent '{Agent}' failed — continuing normal pipeline.",
                recoveryAgentName);
        }
    }
}
