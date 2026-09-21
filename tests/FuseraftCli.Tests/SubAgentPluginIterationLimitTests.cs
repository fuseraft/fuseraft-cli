using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using fuseraft.Core.Events;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// A sub-agent loop that hits its iteration cap must say so — <c>FunctionInvokingChatClient</c>
/// stops silently on an unexecuted tool call, which used to read as "no output" or as a finished answer.
/// </summary>
public sealed class SubAgentPluginIterationLimitTests
{
    private const int Cap = 3;

    private static readonly AIFunction FakeTool =
        AIFunctionFactory.Create((string path) => "ok", "fake_read", "Reads a file.");

    [Fact]
    public async Task NonStreaming_HitsCap_ReportsItInsteadOfNoOutput()
    {
        var plugin = new SubAgentPlugin(new ToolLoopClient(answerAfterCalls: null, narrate: false),
            explorerTools: [FakeTool], maxToolCalls: Cap);

        var result = await plugin.ExploreAsync("q");

        Assert.Contains($"stopped after {Cap} tool calls", result);
        Assert.DoesNotContain("no text output", result);
        Assert.DoesNotContain("Partial output", result);
    }

    [Fact]
    public async Task NonStreaming_HitsCap_KeepsNarrationAsPartialOutput()
    {
        var plugin = new SubAgentPlugin(new ToolLoopClient(answerAfterCalls: null, narrate: true),
            explorerTools: [FakeTool], maxToolCalls: Cap);

        var result = await plugin.ExploreAsync("q");

        Assert.Contains($"stopped after {Cap} tool calls", result);
        Assert.Contains("Partial output", result);
        Assert.Contains("looking at file-1", result);
    }

    [Fact]
    public async Task Streaming_HitsCap_SendsNoticeToTheUserAndReturnsIt()
    {
        var plugin = new SubAgentPlugin(new ToolLoopClient(answerAfterCalls: null, narrate: true),
            explorerTools: [FakeTool], maxToolCalls: Cap);
        var chunks = new List<string>();

        var (result, _, _) = await plugin.ExploreStreamingAsync("q", c => { chunks.Add(c); return Task.CompletedTask; });

        Assert.Contains($"stopped after {Cap} tool calls", string.Concat(chunks));
        Assert.Contains($"stopped after {Cap} tool calls", result);
        Assert.Contains("looking at file-1", result);
    }

    [Fact]
    public async Task Streaming_HitsCapWithNoNarration_StillSendsNotice()
    {
        var plugin = new SubAgentPlugin(new ToolLoopClient(answerAfterCalls: null, narrate: false),
            explorerTools: [FakeTool], maxToolCalls: Cap);
        var chunks = new List<string>();

        var (result, _, _) = await plugin.ExploreStreamingAsync("q", c => { chunks.Add(c); return Task.CompletedTask; });

        Assert.Contains($"stopped after {Cap} tool calls", string.Concat(chunks));
        Assert.DoesNotContain("no text output", result);
    }

    [Fact]
    public async Task NonStreaming_FinishesWithinCap_ReturnsTheAnswerUntouched()
    {
        var plugin = new SubAgentPlugin(new ToolLoopClient(answerAfterCalls: 2, narrate: false),
            explorerTools: [FakeTool], maxToolCalls: Cap);

        var result = await plugin.ExploreAsync("q");

        Assert.Equal("All done.", result);
    }

    [Fact]
    public async Task Streaming_FinishesWithinCap_ReturnsTheAnswerUntouched()
    {
        var plugin = new SubAgentPlugin(new ToolLoopClient(answerAfterCalls: 2, narrate: false),
            explorerTools: [FakeTool], maxToolCalls: Cap);
        var chunks = new List<string>();

        var (result, _, _) = await plugin.ExploreStreamingAsync("q", c => { chunks.Add(c); return Task.CompletedTask; });

        Assert.Equal("All done.", result);
        Assert.Equal("All done.", string.Concat(chunks));
    }

    [Theory]
    [InlineData(null, "iteration_limit")]
    [InlineData(2, "completed")]
    public async Task SubAgentEndEvent_RecordsWhetherTheCapWasHit(int? answerAfterCalls, string expectedOutcome)
    {
        var eventsPath = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var emitter = new EventEmitter(eventsPath))
            {
                var plugin = new SubAgentPlugin(new ToolLoopClient(answerAfterCalls, narrate: false),
                    explorerTools: [FakeTool], eventEmitter: emitter, maxToolCalls: Cap);

                await plugin.ExploreAsync("q");
            }

            var endLine = File.ReadAllLines(eventsPath).Single(l => l.Contains("sub_agent_end"));
            Assert.Contains($"\"outcome\":\"{expectedOutcome}\"", endLine);
        }
        finally
        {
            File.Delete(eventsPath);
        }
    }

    /// <summary>Requests a tool on every round until it has seen <c>answerAfterCalls</c> results (never, when null).</summary>
    private sealed class ToolLoopClient(int? answerAfterCalls, bool narrate) : IChatClient
    {
        public ChatClientMetadata Metadata => new("test", null!, "stub");

        private ChatMessage Next(IEnumerable<ChatMessage> messages)
        {
            var results = messages.Count(m => m.Role == ChatRole.Tool);
            if (answerAfterCalls is { } n && results >= n)
                return new ChatMessage(ChatRole.Assistant, "All done.");

            List<AIContent> contents = [];
            if (narrate) contents.Add(new TextContent($"looking at file-{results + 1}"));
            contents.Add(new FunctionCallContent($"call-{results + 1}", "fake_read",
                new AIFunctionArguments(new Dictionary<string, object?> { ["path"] = $"file-{results + 1}.md" })));
            return new ChatMessage(ChatRole.Assistant, contents);
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(Next(messages)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var next = Next(messages);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, next.Contents);
        }

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }
}
