using Microsoft.Extensions.AI;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Installed as <see cref="FunctionInvokingChatClient.FunctionInvoker"/> on the REPL's free-form
/// client (<c>ReplFactory.BuildClient</c>, only when building <c>ctx.Client</c> — never
/// <c>ctx.StepClient</c>) to give <see cref="ReplTurn.SoftRepeatedToolCallThreshold"/> a genuine
/// mid-turn effect: a one-time notice embedded in the *same* tool-result message the model sees
/// on its next round, rather than a message inserted separately (which — verified empirically —
/// lands between a tool_use and its own tool_result and breaks strict-adjacency providers like
/// Anthropic; see the comment on SoftRepeatedToolCallThreshold).
///
/// <para>
/// Deliberately narrow: this does not touch failure counting, the hard repeated-call cutoff, or
/// termination — <see cref="ReplTurn.MaxConsecutiveToolFailures"/> and
/// <see cref="ReplTurn.MaxConsecutiveIdenticalToolCalls"/> keep working exactly as before via
/// their existing stream-chunk-parsing checks in <c>ReplTurn.StreamTurnResponseAsync</c>, and see
/// the same chunks either way (this invoker sits *underneath* that parsing, not instead of it).
/// An invocation exception is left to propagate untouched — this guard only ever annotates a
/// successful return value.
/// </para>
///
/// <para>
/// One instance is shared across the whole REPL session's <c>ctx.Client</c> (rebuilt only on
/// <c>/provider setup</c>, <c>/model</c> switch, etc.), so per-turn state resets itself from
/// <see cref="FunctionInvocationContext.Iteration"/> == 0 — verified empirically to start a new
/// top-level <c>GetStreamingResponseAsync</c>/<c>GetResponseAsync</c> call, not to keep climbing
/// across calls — rather than needing an explicit reset hook from <c>ReplTurn</c>.
/// </para>
/// </summary>
internal sealed class ReplToolLoopGuard
{
    private string _lastCallSignature      = string.Empty;
    private int    _consecutiveIdentical;

    public async ValueTask<object?> InvokeAsync(FunctionInvocationContext context, CancellationToken cancellationToken)
    {
        if (context.Iteration == 0)
        {
            _lastCallSignature     = string.Empty;
            _consecutiveIdentical  = 0;
        }

        var signature = $"{context.CallContent.Name}|{ReplTurn.ToolCallSignature(context.Arguments)}";
        _consecutiveIdentical = signature == _lastCallSignature ? _consecutiveIdentical + 1 : 1;
        _lastCallSignature    = signature;

        var result = await context.Function.InvokeAsync(context.Arguments, cancellationToken);

        // Exact equality (not >=) so this fires once per climbing streak — matches Cline's own
        // softThreshold check (state.consecutiveIdenticalCount === config.softThreshold).
        if (_consecutiveIdentical == ReplTurn.SoftRepeatedToolCallThreshold)
        {
            return $"{result}\n\n[SYSTEM NOTICE: this is the {_consecutiveIdentical} call in a row with " +
                   $"the same name and arguments ('{context.CallContent.Name}') — if this isn't making " +
                   "progress, try a different approach.]";
        }
        return result;
    }
}
