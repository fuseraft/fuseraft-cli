using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

public sealed class ReplInterjectingChatClientTests
{
    // First call asks for a tool; every later call answers in text and records what it was sent.
    private sealed class ToolThenTextClient : IChatClient
    {
        public List<List<ChatMessage>> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add([.. messages]);
            await Task.Yield();
            if (Calls.Count == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("c1", "noop")]);
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
                yield return new ChatResponseUpdate(ChatRole.Assistant, " here");
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static (IChatClient Client, ToolThenTextClient Inner, ReplInterjectingChatClient Interjector) Build(Queue<string> sent)
    {
        var inner = new ToolThenTextClient();
        var interjector = new ReplInterjectingChatClient(inner) { Take = _ => Task.FromResult(sent.Count > 0 ? sent.Dequeue() : null) };
        var client = new FunctionInvokingChatClient(interjector);
        return (client, inner, interjector);
    }

    private static ChatOptions Options() => new() { Tools = [AIFunctionFactory.Create(() => "ok", "noop")] };

    [Fact]
    public async Task A_message_sent_mid_turn_joins_after_the_tool_result()
    {
        var sent = new Queue<string>(["skip the tests"]);
        var (client, inner, _) = Build(sent);
        List<ChatResponseUpdate> updates = [];

        await foreach (var u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "fix it")], Options()))
            updates.Add(u);

        var second = inner.Calls[1];
        Assert.Equal(ChatRole.Tool, second[^2].Role);
        Assert.Equal(ChatRole.User, second[^1].Role);
        Assert.Equal("skip the tests", second[^1].Text);

        List<ChatMessage> transcript = [];
        transcript.AddMessages(updates);
        Assert.Equal([ChatRole.Assistant, ChatRole.Tool, ChatRole.User, ChatRole.Assistant], transcript.Select(m => m.Role));
        Assert.Equal("skip the tests", transcript[2].Text);
        Assert.Equal("done here", transcript[3].Text);
    }

    [Fact]
    public async Task Nothing_is_taken_before_the_first_tool_result()
    {
        var sent = new Queue<string>(["later"]);
        var inner = new ToolThenTextClient();
        var taken = 0;
        var interjector = new ReplInterjectingChatClient(inner) { Take = _ => { taken++; return Task.FromResult(sent.Count > 0 ? sent.Dequeue() : null); } };

        await foreach (var _ in interjector.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])) { }

        Assert.Equal(0, taken);
        Assert.Single(sent);
    }

    [Fact]
    public async Task Without_take_it_passes_through()
    {
        var (client, inner, interjector) = Build(new Queue<string>());
        interjector.Take = null;

        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "fix it")], Options())) { }

        Assert.DoesNotContain(inner.Calls[1], m => m.Role == ChatRole.User && m.Text != "fix it");
    }
}
