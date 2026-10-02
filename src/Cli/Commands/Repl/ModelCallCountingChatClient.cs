using Microsoft.Extensions.AI;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Counts the model calls made through it. <see cref="ReplFactory.BuildClient"/> places one directly
/// under the function-invocation loop, so every round of a turn passes through it exactly once, and
/// <see cref="ReplTurn"/> finds it with <c>GetService</c> to report how many rounds a turn took.
///
/// <para>
/// Rounds used to be inferred from the stream — a usage chunk or a finish reason marking each round's
/// end — but Microsoft.Extensions.AI's OpenAI adapter copies the finish reason onto the usage-only
/// update and onto the update carrying the tool calls, so one model call shows two or three of them:
/// 25 real calls to grok were reported as 71 rounds. Counting the calls themselves needs no knowledge
/// of how any provider shapes its stream, and also covers providers that report no usage at all.
/// </para>
///
/// <para>
/// Each client built by <see cref="ReplFactory.BuildClient"/> gets its own counter, so a subagent's
/// calls — made while a REPL turn is running, through a client of their own — never count toward
/// that turn.
/// </para>
/// </summary>
internal sealed class ModelCallCountingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private int _calls;

    /// <summary>Model calls started through this client so far.</summary>
    public int Calls => Volatile.Read(ref _calls);

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }
}
