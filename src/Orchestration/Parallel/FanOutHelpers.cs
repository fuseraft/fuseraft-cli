using AgentGovernance;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Parallel;

/// <summary>
/// Shared per-branch invocation helpers for the fan-out orchestrators — <c>MapReduceOrchestrator</c>,
/// <c>ScatterGatherOrchestrator</c>, and <c>AdversarialOrchestrator</c> each independently hand-wrote
/// the same "invoke one agent, wrap the response as an AgentMessage, fire the token-budget warning,
/// flush the change tracker" sequence (confirmed byte-identical between MapReduce and ScatterGather).
///
/// <para>
/// <c>AgentOrchestrator</c>'s parallel fan-out is deliberately <b>not</b> migrated onto these
/// helpers — it was flagged as "a fourth, differently-shaped way" of doing the same concept, and
/// forcing it onto this shape would either not fit or require compromising the helpers for the
/// other three.
/// </para>
///
/// <para>
/// <c>BuildContext</c> is shared only between <c>MapReduceOrchestrator</c> and
/// <c>ScatterGatherOrchestrator</c> — <c>AdversarialOrchestrator</c> has its own, intentionally
/// different context-assembly (the generator/critic context-firewall invariant), so it is not
/// included here.
/// </para>
/// </summary>
internal static class FanOutHelpers
{
    public static IEnumerable<ChatMessage> BuildContext(string? instructions, IList<ChatMessage> history) =>
        !string.IsNullOrWhiteSpace(instructions)
            ? (IEnumerable<ChatMessage>)[new ChatMessage(ChatRole.System, instructions), .. history]
            : history;

    /// <summary>
    /// Assembles per-agent context through the unified <see cref="IContextAssemblyPipeline"/> when
    /// one is configured (memory augmentation, ADR/knowledge retrieval, per-agent <c>Context:</c>
    /// spec — the same treatment <c>GraphOrchestrator</c>/<c>MagenticOrchestrator</c>/
    /// <c>AgentOrchestrator</c> give their agents), falling back to the legacy raw
    /// instructions+history via <see cref="BuildContext"/> when the pipeline is absent.
    /// </summary>
    public static async Task<IEnumerable<ChatMessage>> AssembleContextAsync(
        IContextAssemblyPipeline? contextPipeline,
        EventEmitter? eventEmitter,
        string agentName,
        string task,
        string? instructions,
        IReadOnlyList<ChatMessage> history,
        AgentConfig? agentConfig,
        string? sessionId,
        int turn,
        CancellationToken ct)
    {
        if (contextPipeline is null)
            return BuildContext(instructions, history as IList<ChatMessage> ?? history.ToList());

        var assembled = await contextPipeline.AssembleAsync(
            new AgentExecutionRequest
            {
                AgentName     = agentName,
                Task          = task,
                SharedHistory = history,
                AgentConfig   = agentConfig,
                SessionId     = sessionId,
            }, ct).ConfigureAwait(false);

        if (eventEmitter is not null)
        {
            var metrics = assembled.Metrics;
            await eventEmitter.EmitAsync(EventTypes.ContextAssembly,
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
                }).ConfigureAwait(false);
        }

        return assembled.Messages;
    }

    /// <summary>
    /// Extracts entity-scoped findings from a turn's tool calls and persists them to
    /// <paramref name="repositoryKnowledgeStore"/> for future session retrieval — the same
    /// post-turn observation capture <c>GraphOrchestrator</c>/<c>MagenticOrchestrator</c> perform.
    /// Best-effort: extraction/persistence failures are swallowed so they never fail the turn.
    /// </summary>
    public static async Task PersistObservationsAsync(
        RepositoryKnowledgeStore? repositoryKnowledgeStore,
        string? sessionId,
        AgentResponse response,
        string agentName,
        int turn)
    {
        if (repositoryKnowledgeStore is null || string.IsNullOrEmpty(sessionId)) return;

        try
        {
            var observations = ObservationExtractor.Extract(
                (IReadOnlyList<ChatMessage>)response.Messages, agentName, turn);
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

    public static async Task<AgentResponse> InvokeAgentAsync(
        AIAgent agent,
        IEnumerable<ChatMessage> context,
        GovernanceKernel? governanceKernel,
        CancellationToken ct)
    {
        return governanceKernel?.CircuitBreaker is { } cb
            ? await cb.ExecuteAsync(() => agent.RunAsync(context, null, null, ct)).ConfigureAwait(false)
            : await agent.RunAsync(context, null, null, ct).ConfigureAwait(false);
    }

    public static AgentMessage MakeMessage(
        string agentName, string content, int turn,
        TokenUsage? usage, IReadOnlyList<ToolCallRecord>? toolCalls = null) =>
        new()
        {
            AgentName = agentName,
            Content   = content,
            Role      = "assistant",
            TurnIndex = turn,
            Usage     = usage,
            ToolCalls = toolCalls,
        };

    /// <summary>Invokes <paramref name="onWarning"/> (the caller's own <c>TokenBudgetWarning</c>
    /// event) when the message's input-token count exceeds <paramref name="warnTurnTokens"/>.</summary>
    public static void FireTokenBudgetWarning(
        AgentMessage msg, int warnTurnTokens, Action<string, int, int>? onWarning)
    {
        if (warnTurnTokens > 0 && msg.Usage?.InputTokens is { } input && input > warnTurnTokens)
            onWarning?.Invoke(msg.AgentName ?? string.Empty, input, warnTurnTokens);
    }

    public static async Task FlushChangeTrackerAsync(
        AgentMessage msg, ChangeTracker? changeTracker, ILogger logger, string callerName)
    {
        if (changeTracker is null) return;
        try
        {
            await changeTracker.FlushTurnAsync(
                msg.AgentName ?? string.Empty, msg.TurnIndex, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[{Caller}] ChangeTracker flush failed for turn {Turn} ({Agent}).",
                callerName, msg.TurnIndex, msg.AgentName);
        }
    }
}
