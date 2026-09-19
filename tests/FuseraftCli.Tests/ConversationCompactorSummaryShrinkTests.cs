using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Config;
using fuseraft.Core.Models.Context;
using fuseraft.Orchestration.Context;

namespace FuseraftCli.Tests;

/// <summary>
/// The compaction summary call can itself overflow the provider's context. Instead of degrading
/// straight to a "COMPACTION FAILED" marker (losing the whole history), the compactor re-prunes
/// the history to a smaller per-message cap and retries — but only for a context-exceeded
/// failure, and only a bounded number of times.
/// </summary>
public sealed class ConversationCompactorSummaryShrinkTests
{
    private const string FallbackMarker = "COMPACTION FAILED";

    // Throws a provider-shaped "prompt is too long" 400 whenever the prompt exceeds the limit,
    // otherwise returns a summary. Records the size of every prompt it was sent.
    private sealed class OverflowingChatClient(int maxPromptChars, Exception? alwaysThrow = null) : IChatClient
    {
        public List<int> PromptLengths { get; } = [];

        public ChatClientMetadata Metadata => new("test", null!, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            PromptLengths.Add(messages.Sum(m => m.Text?.Length ?? 0));

            if (alwaysThrow is not null) throw alwaysThrow;
            if (PromptLengths[^1] > maxPromptChars)
                throw new HttpRequestException("prompt is too long: 250000 tokens > 200000 maximum", null, HttpStatusCode.BadRequest);

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary text")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private sealed class FakeSnapshotter : IContextSnapshotter
    {
        public Task<ContextSnapshot> SnapshotAsync(CancellationToken ct = default) =>
            Task.FromResult(new ContextSnapshot());
    }

    // Messages well under the default 8 000-char cap, so the first attempt sends them all in full.
    private static List<AgentMessage> BigMessages(int count, int chars = 8_000) =>
        Enumerable.Range(0, count).Select(i => new AgentMessage
        {
            AgentName = "Developer",
            Content   = new string((char)('a' + i % 26), chars),
            Role      = i % 2 == 0 ? "user" : "assistant",
            TurnIndex = i,
        }).ToList();

    private static ConversationCompactor NewCompactor(IChatClient client, string mode = "llm") =>
        new(client, new CompactionConfig { Mode = mode, KeepRecentTurns = 1 }, NullLogger<ConversationCompactor>.Instance);

    [Fact]
    public async Task SummaryCallThatOverflows_IsRetriedSmaller_AndSucceeds()
    {
        // 7 messages → 6 compacted × 8 000 chars is far over the 30 000-char limit on the first try.
        var client = new OverflowingChatClient(maxPromptChars: 30_000);

        var (summary, _) = await NewCompactor(client).CompactAsync("task", BigMessages(7));

        Assert.DoesNotContain(FallbackMarker, summary.Content);
        Assert.Contains("summary text", summary.Content);
        Assert.True(client.PromptLengths.Count >= 2, "expected at least one shrunken retry");
        for (var i = 1; i < client.PromptLengths.Count; i++)
            Assert.True(client.PromptLengths[i] < client.PromptLengths[i - 1],
                $"prompt {i} ({client.PromptLengths[i]}) should be smaller than prompt {i - 1} ({client.PromptLengths[i - 1]})");
        Assert.True(client.PromptLengths[^1] <= 30_000);
    }

    [Fact]
    public async Task SummaryCallThatFitsFirstTime_IsNotRetried()
    {
        var client = new OverflowingChatClient(maxPromptChars: int.MaxValue);

        var (summary, _) = await NewCompactor(client).CompactAsync("task", BigMessages(7));

        Assert.Single(client.PromptLengths);
        Assert.Contains("summary text", summary.Content);
    }

    [Fact]
    public async Task PersistentOverflow_GivesUpAfterBoundedRetries_AndFallsBackToMarker()
    {
        // Overflows at any size: 1 initial attempt + 4 shrunken retries, then the fallback marker.
        var client = new OverflowingChatClient(maxPromptChars: 0);

        var (summary, _) = await NewCompactor(client).CompactAsync("task", BigMessages(7));

        Assert.Equal(5, client.PromptLengths.Count);
        Assert.Contains(FallbackMarker, summary.Content);
    }

    [Fact]
    public async Task NonOverflowFailure_IsNotRetried()
    {
        // Sending less can't fix an auth error, so it must go straight to the fallback marker.
        var client = new OverflowingChatClient(
            maxPromptChars: int.MaxValue,
            alwaysThrow: new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized));

        var (summary, _) = await NewCompactor(client).CompactAsync("task", BigMessages(7));

        Assert.Single(client.PromptLengths);
        Assert.Contains(FallbackMarker, summary.Content);
    }

    [Fact]
    public async Task HybridMode_AlsoShrinksAndRetries()
    {
        var client = new OverflowingChatClient(maxPromptChars: 30_000);

        var (summary, _) = await NewCompactor(client, mode: "hybrid")
            .CompactAsync("task", BigMessages(7), snapshotter: new FakeSnapshotter());

        Assert.Contains("summary text", summary.Content);
        Assert.True(client.PromptLengths.Count >= 2);
    }

    [Fact]
    public async Task CancellationDuringRetry_Propagates_InsteadOfBeingSwallowed()
    {
        using var cts = new CancellationTokenSource();
        var client = new OverflowingChatClient(
            maxPromptChars: int.MaxValue, alwaysThrow: new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewCompactor(client).CompactAsync("task", BigMessages(7), cancellationToken: cts.Token));
    }
}
