using AgentGovernance;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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
