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
/// Deliberately narrow: this only ever <i>annotates</i> a returned result. It never terminates
/// anything — <see cref="ReplTurn.MaxConsecutiveToolFailures"/>,
/// <see cref="ReplTurn.MaxConsecutiveIdenticalToolCalls"/> and the alternation cutoff keep
/// working exactly as before via their existing stream-chunk-parsing checks in
/// <c>ReplTurn.StreamTurnResponseAsync</c>, and see the same chunks either way (this invoker
/// sits *underneath* that parsing, not instead of it). Three notices ride in the tool result,
/// each once per streak, each a chance to course-correct before the matching hard stop:
/// an identical-call repeat, an A/B/A/B alternation
/// (<see cref="ToolCallCycleDetector"/>), and the failure streak one short of the failure cutoff.
/// An invocation exception is left to propagate untouched (it still counts toward the failure
/// streak) — there's no result to annotate.
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
    private int    _consecutiveFailures;
    private readonly ToolCallCycleDetector _cycles = new();

    public async ValueTask<object?> InvokeAsync(FunctionInvocationContext context, CancellationToken cancellationToken)
    {
        if (context.Iteration == 0)
        {
            _lastCallSignature     = string.Empty;
            _consecutiveIdentical  = 0;
            _consecutiveFailures   = 0;
            _cycles.Reset();
        }

        var signature = $"{context.CallContent.Name}|{ToolCallSignature.Compute(context.Arguments)}";
        _consecutiveIdentical = signature == _lastCallSignature ? _consecutiveIdentical + 1 : 1;
        _lastCallSignature    = signature;
        var cycle = _cycles.Observe(signature);

        object? result;
        try
        {
            result = await context.Function.InvokeAsync(context.Arguments, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _consecutiveFailures++;   // ReplTurn counts a thrown invocation as a failure too
            throw;
        }

        var failed = ReplTurn.IsToolFailureText(result?.ToString());
        _consecutiveFailures = failed ? _consecutiveFailures + 1 : 0;

        // Exact equality (not >=) so each fires once per climbing streak — matches Cline's own
        // softThreshold check (state.consecutiveIdenticalCount === config.softThreshold).
        List<string>? notices = null;

        if (_consecutiveIdentical == ReplTurn.SoftRepeatedToolCallThreshold)
        {
            (notices ??= []).Add(
                $"[SYSTEM NOTICE: this is the {_consecutiveIdentical} call in a row with " +
                $"the same name and arguments ('{context.CallContent.Name}') — if this isn't making " +
                "progress, try a different approach.]");
        }

        if (cycle == ToolCallCycleVerdict.Soft)
        {
            (notices ??= []).Add(
                $"[SYSTEM NOTICE: the last {_cycles.LastLength} tool calls have alternated between the " +
                "same two calls (same names and arguments) without changing anything — that's a loop. " +
                "Try a different approach.]");
        }

        // One short of the cutoff, so the model gets a chance to change course instead of being
        // stopped cold on the very next failure.
        if (failed && _consecutiveFailures == ReplTurn.MaxConsecutiveToolFailures - 1)
        {
            (notices ??= []).Add(
                $"[SYSTEM NOTICE: {_consecutiveFailures} tool calls in a row have failed — one more " +
                "failure will end this turn. Re-read the error above and change your approach rather " +
                "than retrying the same thing.]");
        }

        return notices is null ? result : $"{result}\n\n{string.Join("\n", notices)}";
    }
}
