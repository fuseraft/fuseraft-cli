using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Lets a message the user sends while a turn runs join that turn. Sits directly under the function-invocation loop:
/// before each of the turn's model calls that follows tool results, it takes what the user sent since and adds it as a
/// user message. It also yields that message first, as a user-role update, so the loop keeps it in its history for the
/// rounds after and the turn's transcript (<c>ReplTurn</c>'s raw updates) records it where it was read.
/// </summary>
/// <remarks>
/// Only after tool results, so a call is never separated from its result. <see cref="Take"/> is set by
/// <c>ReplTurn</c> for the length of a free-form turn and is null otherwise, so subagent clients and plan steps never
/// take a message.
/// </remarks>
internal sealed class ReplInterjectingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    /// <summary>What the user sent since the last model call, or null; set for the length of a turn.</summary>
    public Func<CancellationToken, Task<string?>>? Take { get; set; }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        if (await InterjectionAsync(list, cancellationToken) is { } typed)
        {
            yield return new ChatResponseUpdate(ChatRole.User, typed.Text) { MessageId = typed.MessageId };
            list = [.. list, typed];
        }
        await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken))
            yield return update;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var typed = await InterjectionAsync(list, cancellationToken);
        var response = await base.GetResponseAsync(typed is null ? list : [.. list, typed], options, cancellationToken);
        if (typed is not null)
            response.Messages.Insert(0, typed);
        return response;
    }

    async Task<ChatMessage?> InterjectionAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        if (messages.Count == 0 || messages[^1].Role != ChatRole.Tool || Take is not { } take
            || await take(ct) is not { Length: > 0 } text)
            return null;
        return new ChatMessage(ChatRole.User, text) { MessageId = "interjection-" + Guid.NewGuid().ToString("N") };
    }
}
