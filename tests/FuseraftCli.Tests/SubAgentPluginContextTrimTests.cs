using Microsoft.Extensions.AI;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Regression coverage for the in-turn context trim wired into
/// <see cref="SubAgentPlugin"/>'s internal tool-calling loop (RunLoopAsync). Before this fix,
/// the loop's <c>loopClient</c> was built with only <c>UseFunctionInvocation</c> — no sliding
/// tool-pair window, no char budget — so every round resent the full accumulated message list,
/// producing O(N²) cumulative input tokens across a long DelegateAsync run (observed: ~1.03M
/// input tokens for a single 40-iteration delegate call editing a dozen files).
///
/// These tests drive <see cref="SubAgentPlugin.DelegateAsync"/> against a stub
/// <see cref="IChatClient"/> that keeps requesting a large-output tool for many rounds, and
/// assert the char volume the stub actually receives stays bounded rather than growing
/// linearly with round count.
/// </summary>
public sealed class SubAgentPluginContextTrimTests
{
    private const int ToolResultChars = 20_000;
    private const int Rounds          = 15; // > SubAgentMaxInTurnToolPairs (10), well under DelegateMaxToolCalls (40)

    [Fact]
    public async Task DelegateLoop_KeepsPerRoundRequestSizeBounded_AcrossManyLargeToolResults()
    {
        var stub = new RecordingStubChatClient(Rounds);

        var fakeTool = AIFunctionFactory.Create(
            (string path) => new string('x', ToolResultChars),
            "fake_write_tool",
            "Simulates a tool call that returns a large result, e.g. a file read or patch confirmation.");

        var plugin = new SubAgentPlugin(
            stub,
            explorerTools: [],
            delegateTools: [fakeTool]);

        var result = await plugin.DelegateAsync("Simulate a multi-file editing task.");

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.True(stub.RequestCharsByRound.Count >= Rounds,
            $"expected at least {Rounds} rounds, saw {stub.RequestCharsByRound.Count}");

        // Without trimming, round N's request size grows roughly linearly with N (each round
        // resends every prior tool result), so the last round would be close to
        // Rounds * ToolResultChars (~300k chars here). With the sliding window + char-budget
        // trim in place, growth should flatten out well below that once the window fills.
        var last = stub.RequestCharsByRound[^1];
        var untrimmedWorstCase = (long)Rounds * ToolResultChars;

        Assert.True(last < untrimmedWorstCase / 2,
            $"last-round request size ({last:N0} chars) should be well below the untrimmed " +
            $"worst case ({untrimmedWorstCase:N0} chars) — context trim does not appear to be applied.");

        // Growth should stay flat and small once the sliding window fills — not climb by
        // roughly one full ToolResultChars-sized increment every round the way it did before
        // this fix (each round's tool result was landing in a message role the char-budget
        // trim couldn't see, so it accumulated forever). A generous absolute ceiling, well
        // under a single tool result's own size, is a more robust signal here than a ratio
        // against an early round — at these trimmed sizes small per-round overhead (call IDs,
        // argument text) can swing a ratio check without indicating unbounded growth.
        Assert.True(last < ToolResultChars,
            $"final-round request size ({last:N0} chars) should stay well under a single " +
            $"untrimmed tool result ({ToolResultChars:N0} chars) — growth looks unbounded rather " +
            "than capped by the sliding window / char budget.");
    }

    private sealed class RecordingStubChatClient(int roundsBeforeFinalAnswer) : IChatClient
    {
        public List<long> RequestCharsByRound { get; } = [];

        public ChatClientMetadata Metadata => new("test", null!, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

            long chars = 0;
            foreach (var m in list)
                foreach (var c in m.Contents)
                    chars += c switch
                    {
                        TextContent t           => t.Text?.Length ?? 0,
                        FunctionResultContent r => (r.Result as string)?.Length ?? 0,
                        FunctionCallContent fc  => fc.Arguments?.Values.Sum(v => v?.ToString()?.Length ?? 0) ?? 0,
                        _                       => 0,
                    };
            RequestCharsByRound.Add(chars);

            var toolResultCount = list.Count(m => m.Role == ChatRole.Tool);

            ChatMessage response = toolResultCount >= roundsBeforeFinalAnswer
                ? new ChatMessage(ChatRole.Assistant, "Done — simulated task complete.")
                : new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent($"call-{toolResultCount}", "fake_write_tool",
                        new AIFunctionArguments(new Dictionary<string, object?> { ["path"] = $"file-{toolResultCount}.md" }))]);

            return Task.FromResult(new ChatResponse(response));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Non-streaming path only for this test.");

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }
}
