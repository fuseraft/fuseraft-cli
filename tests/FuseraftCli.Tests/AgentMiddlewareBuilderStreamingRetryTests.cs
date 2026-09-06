using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using fuseraft.Core.Models.Agents;
using fuseraft.Infrastructure.Agents;

namespace FuseraftCli.Tests;

/// <summary>
/// Regression tests for the streaming path's reactive adaptive-trim retry in
/// <see cref="AgentMiddlewareBuilder.BuildMiddlewareChain"/>. Before this, only the
/// non-streaming <c>getResponseFunc</c> retried on a provider ContextExceeded rejection —
/// the streaming path (used by the REPL for token-by-token display) could only pre-trim
/// proactively when explicit budget limits were configured, so an unconfigured REPL session
/// hitting a real context-overflow response had no recovery at all: the turn just died. These
/// tests exercise the retry directly against the middleware chain, independent of the REPL.
/// </summary>
public sealed class AgentMiddlewareBuilderStreamingRetryTests
{
    private const string AgentName = "test-agent";

    private static AgentMiddlewareBuilder NewMiddleware(AdaptiveTrimTracker tracker) =>
        new(NullLogger.Instance, changeTracker: null, securityConfig: null, governanceKernel: null, tracker);

    private static AgentConfig NewAgentConfig() => new() { Name = AgentName, Model = new() { ModelId = "test-model" } };

    private static List<ChatMessage> OneUserMessage() => [new ChatMessage(ChatRole.User, "hi")];

    private static async Task<List<ChatResponseUpdate>> DrainAsync(IAsyncEnumerable<ChatResponseUpdate> stream)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in stream) updates.Add(update);
        return updates;
    }

    // Throws once (as if the provider rejected the request as too large) before ever yielding,
    // then succeeds on the retry the middleware issues with trimmed messages.
    private sealed class ThrowOnceThenSucceedClient : IChatClient
    {
        private int _calls;
        public ChatClientMetadata Metadata => new("test", null!, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("streaming-only stub");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Interlocked.Increment(ref _calls) == 1 ? ThrowImmediatelyAsync() : SucceedAsync();

        private static async IAsyncEnumerable<ChatResponseUpdate> ThrowImmediatelyAsync()
        {
            await Task.Yield();
            throw new InvalidOperationException("maximum context length exceeded");
#pragma warning disable CS0162 // unreachable — required so the compiler accepts this as an async-iterator method
            yield break;
#pragma warning restore CS0162
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> SucceedAsync()
        {
            await Task.Yield();
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("recovered")] };
        }

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    // Yields one chunk successfully, then throws mid-stream — simulates a failure that only
    // manifests after output has already reached the caller, which must NOT be retried.
    private sealed class YieldThenThrowClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("test", null!, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("streaming-only stub");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => YieldThenThrowAsync();

        private static async IAsyncEnumerable<ChatResponseUpdate> YieldThenThrowAsync()
        {
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("partial")] };
            await Task.Yield();
            throw new InvalidOperationException("maximum context length exceeded");
        }

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task ContextExceeded_BeforeFirstYield_RetriesWithTrimmedMessages_AndRecordsTrim()
    {
        var tracker = new AdaptiveTrimTracker();
        var client = NewMiddleware(tracker).BuildMiddlewareChain(
            chatClient: new ThrowOnceThenSucceedClient(), config: NewAgentConfig(), chatOptions: null,
            maxContextChars: 0, maxInTurnChars: 0, maxInTurnToolPairs: 0,
            toolSchemaChars: 0, maxPayloadBytes: 0, hasHandoff: false, emitter: null);

        var updates = await DrainAsync(client.GetStreamingResponseAsync(OneUserMessage()));

        var text = string.Concat(updates.SelectMany(u => u.Contents.OfType<TextContent>()).Select(t => t.Text));
        Assert.Equal("recovered", text);

        // The retry must have flagged that this call only survived via truncation, so a real
        // compaction runs before the next turn instead of resending the same oversized history.
        Assert.True(tracker.ConsumeTrim(AgentName));
    }

    [Fact]
    public async Task ContextExceeded_AfterFirstYield_IsNotRetried_AndDoesNotRecordTrim()
    {
        var tracker = new AdaptiveTrimTracker();
        var client = NewMiddleware(tracker).BuildMiddlewareChain(
            chatClient: new YieldThenThrowClient(), config: NewAgentConfig(), chatOptions: null,
            maxContextChars: 0, maxInTurnChars: 0, maxInTurnToolPairs: 0,
            toolSchemaChars: 0, maxPayloadBytes: 0, hasHandoff: false, emitter: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await DrainAsync(client.GetStreamingResponseAsync(OneUserMessage())));

        // No retry means no truncation happened, so nothing should be flagged for compaction.
        Assert.False(tracker.ConsumeTrim(AgentName));
    }
}
