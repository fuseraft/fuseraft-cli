using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using fuseraft.Core.Models.Agents;
using fuseraft.Infrastructure.Agents;

namespace FuseraftCli.Tests;

/// <summary>
/// A provider's prompt cache serves a request only up to its first changed message, so between trims each request the
/// middleware sends must start with the one sent before it.
/// </summary>
public sealed class AgentMiddlewareBuilderRequestPrefixTests
{
    private sealed class RecordingStreamingClient : IChatClient
    {
        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add([.. messages]);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add([.. messages]);
            await Task.Yield();
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] };
        }

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private static int RewrittenRequests(List<List<ChatMessage>> requests)
    {
        var rewrites = 0;
        for (var i = 1; i < requests.Count; i++)
        {
            var (prev, cur) = (requests[i - 1], requests[i]);
            if (cur.Count < prev.Count || Enumerable.Range(0, prev.Count).Any(j =>
                    RequestPrefixTracker.Hash(prev[j]) != RequestPrefixTracker.Hash(cur[j])))
                rewrites++;
        }
        return rewrites;
    }

    // The tool schemas take 20K of a 100K window. The view counted only the messages, so from 80K to 100K of messages
    // the streaming pre-trim shortened older results on its own, at each stage it stepped through, before the view's
    // own trim at 100K changed them again.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NearTheWindow_WithToolSchemas_RequestsStayAppendOnlyUntilOneTrim(bool streaming)
    {
        var inner = new RecordingStreamingClient();
        var client = new AgentMiddlewareBuilder(NullLogger.Instance, changeTracker: null, securityConfig: null, governanceKernel: null)
            .BuildMiddlewareChain(
                chatClient: inner, config: new AgentConfig { Name = "a", Model = new() { ModelId = "m" } }, chatOptions: null,
                maxContextChars: 100_000, maxInTurnChars: 100_000, maxInTurnToolPairs: 0,
                toolSchemaChars: 20_000, maxPayloadBytes: 0, hasHandoff: false);

        var history = new List<ChatMessage> { new(ChatRole.System, "sys"), new(ChatRole.User, "go") };
        for (var round = 0; round < 18; round++)
        {
            history.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"c{round}", "shell_run", new Dictionary<string, object?> { ["command"] = $"step {round}" })]));
            history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"c{round}", new string('x', 6_000))]));
            if (round < 10) continue;
            if (streaming)
                await foreach (var _ in client.GetStreamingResponseAsync(history)) { }
            else
                await client.GetResponseAsync(history);
        }

        Assert.Equal(8, inner.Requests.Count);
        Assert.True(RewrittenRequests(inner.Requests) <= 1, $"{RewrittenRequests(inner.Requests)} requests rewrote an earlier message");
    }
}
