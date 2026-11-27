using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using fuseraft.Core.Models.Agents;
using fuseraft.Infrastructure.Agents;

namespace FuseraftCli.Tests;

/// <summary>
/// Claude Sonnet 5.5 and Opus 5.5 reject a forced tool call with a 400. An agent with <c>functionChoice: required</c>
/// failed every turn on them; it now asks again with auto, and keeps asking with auto.
/// </summary>
public sealed class AgentMiddlewareBuilderToolChoiceTests
{
    private const string Rejection =
        "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"tool_choice: type \\\"tool\\\" and \\\"any\\\" are not supported for this model.\"}}";

    private sealed class RefusesForcedToolChoice : IChatClient
    {
        public readonly List<ChatToolMode?> Modes = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Modes.Add(options?.ToolMode);
            if (options?.ToolMode is RequiredChatToolMode) throw new InvalidOperationException(Rejection);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Modes.Add(options?.ToolMode);
            await Task.Yield();
            if (options?.ToolMode is RequiredChatToolMode) throw new InvalidOperationException(Rejection);
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] };
        }

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARefusedForcedToolCall_IsAskedAgainWithAuto_AndStaysAuto(bool streaming)
    {
        var inner = new RefusesForcedToolChoice();
        var defaults = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "x", "read_file")], ToolMode = ChatToolMode.RequireAny };
        var tracker = new AdaptiveTrimTracker();
        var client = new AgentMiddlewareBuilder(NullLogger.Instance, changeTracker: null, securityConfig: null, governanceKernel: null, tracker)
            .BuildMiddlewareChain(
                chatClient: inner, config: new AgentConfig { Name = "a", Model = new() { ModelId = "m" } }, chatOptions: defaults,
                maxContextChars: 0, maxInTurnChars: 0, maxInTurnToolPairs: 0,
                toolSchemaChars: 0, maxPayloadBytes: 0, hasHandoff: false);

        for (var turn = 0; turn < 2; turn++)
        {
            List<ChatMessage> request = [new(ChatRole.User, $"turn {turn}")];
            var text = streaming
                ? string.Concat(await client.GetStreamingResponseAsync(request).Select(u => u.Text).ToListAsync())
                : (await client.GetResponseAsync(request)).Text;
            Assert.Equal("ok", text);
        }

        Assert.Equal(3, inner.Modes.Count);
        Assert.IsType<RequiredChatToolMode>(inner.Modes[0]);
        Assert.All(inner.Modes.Skip(1), m => Assert.IsType<AutoChatToolMode>(m));
        Assert.False(tracker.ConsumeTrim("a")); // not mistaken for a context overflow
    }

    [Fact]
    public void OnlyAToolChoiceError_CountsAsARefusalOfForcing()
    {
        Assert.True(AgentMiddlewareBuilder.IsToolChoiceRejection(new InvalidOperationException(Rejection)));
        Assert.False(AgentMiddlewareBuilder.IsToolChoiceRejection(new InvalidOperationException("prompt is too long")));
    }
}
