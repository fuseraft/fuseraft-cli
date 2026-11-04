using Microsoft.Extensions.AI;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Runs a round's read-only tool calls at the same time when the model asks for several at once
/// (with <see cref="FunctionInvokingChatClient.AllowConcurrentInvocation"/> on), keeping the model's
/// order wherever it can matter: a call that can change anything waits for every call before it,
/// every call after it waits for it, and reads in one <see cref="ToolConcurrency"/> lane take turns.
///
/// <para>
/// Relies on the client starting a round's calls synchronously in index order, so the order calls
/// register here is the model's order. A call to an unknown tool never reaches the invoker, and
/// since nothing waits for a call that hasn't registered, it can't hold the round up.
/// </para>
/// </summary>
internal sealed class ReplToolScheduler
{
    private readonly Lock _lock = new();
    private int _round = -1;
    private IList<ChatMessage>? _roundMessages;
    private readonly List<(string? Lane, Task Done)> _started = [];

    public async ValueTask<object?> InvokeAsync(FunctionInvocationContext context, CancellationToken cancellationToken)
    {
        var lane = ToolConcurrency.ReadLane(context.Function);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] before;
        lock (_lock)
        {
            if (context.Iteration != _round || !ReferenceEquals(context.Messages, _roundMessages))
            {
                _round         = context.Iteration;
                _roundMessages = context.Messages;
                _started.Clear();
            }
            before = [.. _started.Where(s => MustWait(lane, s.Lane)).Select(s => s.Done)];
            _started.Add((lane, done.Task));
        }

        try
        {
            if (before.Length > 0) await Task.WhenAll(before).WaitAsync(cancellationToken);
            if (lane is null) return await context.Function.InvokeAsync(context.Arguments, cancellationToken);
            // Off the caller's thread: a synchronous tool would otherwise run inline and hold up the next call's start.
            return await Task.Run(() => context.Function.InvokeAsync(context.Arguments, cancellationToken).AsTask(), cancellationToken);
        }
        finally
        {
            done.TrySetResult();
        }
    }

    private static bool MustWait(string? lane, string? earlier) =>
        lane is null || earlier is null || (earlier.Length > 0 && earlier == lane);
}
