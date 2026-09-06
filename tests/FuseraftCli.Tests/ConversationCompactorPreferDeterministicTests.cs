using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Config;
using fuseraft.Core.Models.Context;
using fuseraft.Orchestration.Context;
using fuseraft.Orchestration.Knowledge;

namespace FuseraftCli.Tests;

/// <summary>
/// Covers <see cref="ConversationCompactor.CompactAsync"/>'s <c>preferDeterministic</c>
/// parameter — added so a compaction forced by context-overflow recovery
/// (<c>CompactionCoordinator</c>'s new AdaptiveTrim trigger) can't itself risk overflowing an
/// LLM summarizer call with the same oversized history that just failed a provider request.
/// </summary>
public sealed class ConversationCompactorPreferDeterministicTests : IDisposable
{
    private readonly string _tempDir;

    public ConversationCompactorPreferDeterministicTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "fuseraft_compactor_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private sealed class CountingChatClient : IChatClient
    {
        public int CallCount { get; private set; }

        public ChatClientMetadata Metadata => new("test", null!, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary text")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private sealed class FakeSnapshotter : IContextSnapshotter
    {
        public Task<ContextSnapshot> SnapshotAsync(CancellationToken ct = default) =>
            Task.FromResult(new ContextSnapshot());
    }

    private static List<AgentMessage> BuildMessages(int count)
    {
        var messages = new List<AgentMessage>();
        for (int i = 0; i < count; i++)
            messages.Add(new AgentMessage
            {
                AgentName = "Developer",
                Content   = $"turn {i}",
                Role      = i % 2 == 0 ? "user" : "assistant",
                TurnIndex = i,
            });
        return messages;
    }

    [Fact]
    public async Task PreferDeterministic_WithIntentLog_UsesIntentModeNotLlm()
    {
        var chatClient = new CountingChatClient();
        var config = new CompactionConfig { Mode = "llm", KeepRecentTurns = 1 };
        var intentLog = new IntentLog(Path.Combine(_tempDir, "intents.json"));
        var compactor = new ConversationCompactor(chatClient, config, NullLogger<ConversationCompactor>.Instance, intentLog: intentLog);

        var (summary, _) = await compactor.CompactAsync(
            "task", BuildMessages(4), preferDeterministic: true);

        Assert.Equal(0, chatClient.CallCount);
        Assert.Contains("INTENT-DERIVED RECONSTRUCTION", summary.Content);
    }

    [Fact]
    public async Task PreferDeterministic_WithSnapshotterOnly_UsesLosslessModeNotLlm()
    {
        var chatClient = new CountingChatClient();
        var config = new CompactionConfig { Mode = "llm", KeepRecentTurns = 1 };
        var compactor = new ConversationCompactor(chatClient, config, NullLogger<ConversationCompactor>.Instance);

        var (summary, _) = await compactor.CompactAsync(
            "task", BuildMessages(4), snapshotter: new FakeSnapshotter(), preferDeterministic: true);

        Assert.Equal(0, chatClient.CallCount);
        Assert.Contains("CONTEXT RECONSTRUCTION", summary.Content);
    }

    [Fact]
    public async Task PreferDeterministic_NoFallbackAvailable_StillUsesLlm()
    {
        var chatClient = new CountingChatClient();
        var config = new CompactionConfig { Mode = "llm", KeepRecentTurns = 1 };
        var compactor = new ConversationCompactor(chatClient, config, NullLogger<ConversationCompactor>.Instance);

        var (summary, _) = await compactor.CompactAsync(
            "task", BuildMessages(4), preferDeterministic: true);

        Assert.Equal(1, chatClient.CallCount);
        Assert.Contains("CONVERSATION SUMMARY", summary.Content);
    }

    [Fact]
    public async Task PreferDeterministicFalse_UsesConfiguredLlmModeEvenWithIntentLogAvailable()
    {
        var chatClient = new CountingChatClient();
        var config = new CompactionConfig { Mode = "llm", KeepRecentTurns = 1 };
        var intentLog = new IntentLog(Path.Combine(_tempDir, "intents.json"));
        var compactor = new ConversationCompactor(chatClient, config, NullLogger<ConversationCompactor>.Instance, intentLog: intentLog);

        var (summary, _) = await compactor.CompactAsync(
            "task", BuildMessages(4)); // preferDeterministic defaults to false

        Assert.Equal(1, chatClient.CallCount);
        Assert.Contains("CONVERSATION SUMMARY", summary.Content);
    }
}
