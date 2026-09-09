using Microsoft.Extensions.AI;

namespace fuseraft.Infrastructure.Agents;

/// <summary>
/// Orchestration counterpart to <c>ReplToolLoopGuard</c> — installed as
/// <see cref="FunctionInvokingChatClient.FunctionInvoker"/> for every orchestration agent (see
/// <see cref="AgentFactory.Create"/>) and for <c>SubAgentPlugin.RunLoopAsync</c>'s independent
/// tool-calling loop, guarding against a model looping on the exact same tool call (same name +
/// arguments) forever.
///
/// <para>
/// Unlike the REPL's guard (soft-nudge only — <c>ReplTurn.StreamTurnResponseAsync</c>'s own
/// chunk-parsing already implements a hard cutoff), this class implements both tiers itself,
/// since orchestration has no equivalent backstop to lean on: it reports via a
/// <see cref="EventTypes.ToolLoopWarning"/> event at the soft threshold, and via the same event
/// plus <see cref="FunctionInvocationContext.Terminate"/> at the hard one.
/// </para>
///
/// <para>
/// <b>Deliberately does not annotate the returned tool result</b> — unlike
/// <c>ReplToolLoopGuard</c>, which safely embeds a notice in the result string. Both
/// <see cref="AgentFactory.Create"/> and <c>SubAgentPlugin.RunLoopAsync</c> route their message
/// history through <c>AgentContextCompactionFilters.ApplyInTurnFilters</c> with
/// <c>maxInTurnChars &gt; 0</c> (the REPL's own client does not — its <c>maxInTurnChars</c> is
/// always 0 — which is why <c>ReplToolLoopGuard</c> doesn't have this problem). That filter's
/// <c>TrimInTurnContext</c> elides the *oldest* trimmable messages one at a time in a fixed
/// queue order until the running total drops under budget — verified empirically that changing
/// one tool result's byte size by even a small, fixed amount (an appended notice) can shift
/// *which* message the loop happens to stop at, in rare cases leaving a full-size result
/// un-elided that would otherwise have been reduced to a small placeholder. Reporting through
/// <see cref="EventTypes.ToolLoopWarning"/> instead avoids touching in-conversation content at
/// all, sidestepping that fragility entirely.
/// </para>
///
/// <para>
/// Per-instance streak state resets whenever <see cref="FunctionInvocationContext.Iteration"/> is
/// 0 (a new top-level call) — load-bearing here since this guard's closure persists across every
/// turn of its agent for the life of the orchestration run.
/// </para>
/// </summary>
internal sealed class AgentToolLoopGuard(string? agentName, EventEmitter? emitter)
{
    internal const int SoftThreshold = 3;
    internal const int HardThreshold = 5;

    private string _lastCallSignature = string.Empty;
    private int    _consecutiveIdentical;

    public async ValueTask<object?> InvokeAsync(FunctionInvocationContext context, CancellationToken cancellationToken)
    {
        if (context.Iteration == 0)
        {
            _lastCallSignature    = string.Empty;
            _consecutiveIdentical = 0;
        }

        var signature = $"{context.CallContent.Name}|{ToolCallSignature.Compute(context.Arguments)}";
        _consecutiveIdentical = signature == _lastCallSignature ? _consecutiveIdentical + 1 : 1;
        _lastCallSignature    = signature;

        var result = await context.Function.InvokeAsync(context.Arguments, cancellationToken);

        // Exact equality (not >=) so this fires once per climbing streak — matches
        // ReplToolLoopGuard's own convention (and Cline's LoopDetectionTracker softThreshold check).
        if (_consecutiveIdentical == SoftThreshold)
        {
            await EmitAsync("soft", context.CallContent.Name, _consecutiveIdentical, SoftThreshold);
        }
        else if (_consecutiveIdentical >= HardThreshold)
        {
            await EmitAsync("hard", context.CallContent.Name, _consecutiveIdentical, HardThreshold);
            context.Terminate = true;
        }

        return result;
    }

    // Awaited (not fire-and-forget like AgentMiddlewareBuilder's per-round events) — this fires
    // at most twice per turn, only when a loop is actually detected, so deterministic completion
    // is worth more than the negligible latency it costs on an already-stuck path.
    private Task EmitAsync(string kind, string toolName, int streak, int threshold) =>
        emitter?.EmitAsync(EventTypes.ToolLoopWarning, agent: agentName, turn: null, payload: new
        {
            kind,
            tool = toolName,
            streak,
            threshold,
        }) ?? Task.CompletedTask;
}
