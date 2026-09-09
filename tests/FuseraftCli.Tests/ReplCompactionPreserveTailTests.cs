using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary>
/// Covers <see cref="ReplCommands.CompactHistoryAsync"/>'s two fidelity safeguards, added after
/// the user raised a concern that auto-compaction firing after "a few turns and many tool calls"
/// could silently lose critical recent context:
///
/// <list type="bullet">
///   <item>A verbatim recent tail (whole turn-groups, sized by <c>PreserveRecentTailRatio</c> of
///   <see cref="ReplSessionContext.ContextTokenBudget"/>) survives compaction unparaphrased —
///   only what's older than the tail gets folded into the LLM summary.</item>
///   <item>An acceptance bar (mirroring Cline's overflow-recovery contract): a compaction whose
///   result isn't actually smaller than the input is rejected outright, leaving
///   <c>ctx.History</c> untouched, rather than silently accepting a lossy no-op.</item>
/// </list>
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplCompactionPreserveTailTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");
    private readonly List<ReplSessionContext> _contexts = [];

    public ReplCompactionPreserveTailTests() =>
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
        foreach (var ctx in _contexts)
        {
            ctx.Emitter.Dispose();
            ctx.Factory.Dispose();
        }
    }

    /// <summary>Stub client that returns a caller-supplied response and counts how many times it was called.</summary>
    private sealed class ScriptedStubChatClient(Func<IEnumerable<ChatMessage>, string> respond) : IChatClient
    {
        public int CallCount;

        public ChatClientMetadata Metadata => new("test", null!, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, respond(messages))));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by CompactHistoryAsync.");

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private ReplSessionContext NewContext(IChatClient client)
    {
        var eventsPath = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        var ctx = new ReplSessionContext(
            cwd: "/tmp", sessionId: "compact-tail-session", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: null, client: client, factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(eventsPath),
            eventsPath: eventsPath,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(Path.GetTempPath(), $"fuseraft-test-mem-{Guid.NewGuid():N}")),
            toolsByCategory: [], systemPrompt: "test system prompt", pendingSave: false,
            adaptiveTrimTracker: new());
        ctx.JsonMode = true;
        _contexts.Add(ctx);
        return ctx;
    }

    // Ten (User, Assistant) turn-groups, each ~12,500 estimated tokens (25,000 chars / 4), for
    // ~125,000 total — comfortably past the 80,000-token default budget so there's a real
    // old/recent split once the tail is carved off. The last group's content is a unique marker
    // so the test can confirm it survives verbatim rather than only appearing paraphrased in the
    // LLM summary.
    private const string LastGroupMarker = "UNIQUE-MARKER-LAST-USER-MESSAGE-42";

    private static void AddSyntheticHistory(ReplSessionContext ctx, int groups = 10)
    {
        for (int i = 0; i < groups; i++)
        {
            var isLast = i == groups - 1;
            ctx.History.Add(new ChatMessage(ChatRole.User,
                isLast ? LastGroupMarker : $"synthetic-group-{i}-" + new string('a', 25_000)));
            ctx.History.Add(new ChatMessage(ChatRole.Assistant, new string('b', 25_000)));
        }
    }

    [Fact]
    public async Task CompactHistoryAsync_PreservesMostRecentTurnGroupVerbatim()
    {
        var client = new ScriptedStubChatClient(_ => "concise handoff summary");
        var ctx = NewContext(client);
        AddSyntheticHistory(ctx);

        var (success, error, _, _) = await ReplCommands.CompactHistoryAsync(
            ctx, focus: null, CancellationToken.None);

        Assert.True(success, error);
        // The most recent turn's exact content must still be present verbatim, not just folded
        // into the (potentially lossy) LLM-generated summary.
        Assert.Contains(ctx.History, m => m.Text == LastGroupMarker);
        // The oldest turn-group should have been summarized away, not carried forward verbatim.
        Assert.DoesNotContain(ctx.History, m => m.Text != null && m.Text.Contains("synthetic-group-0-"));
        // The summarizer was only asked about the older portion, never the whole 125k-token history.
        Assert.True(client.CallCount == 1);
    }

    [Fact]
    public async Task CompactHistoryAsync_RejectsResultThatIsNotSmaller()
    {
        // Summarizer misbehaves and returns something larger than what it was asked to condense.
        var client = new ScriptedStubChatClient(_ => new string('x', 500_000));
        var ctx = NewContext(client);
        AddSyntheticHistory(ctx);
        var before = new List<ChatMessage>(ctx.History);

        var (success, error, _, _) = await ReplCommands.CompactHistoryAsync(
            ctx, focus: null, CancellationToken.None);

        Assert.False(success);
        Assert.Equal("not_smaller", error);
        // History must be untouched — no partial/destructive mutation on rejection.
        Assert.Equal(before.Select(m => m.Text), ctx.History.Select(m => m.Text));
    }

    [Fact]
    public async Task CompactHistoryAsync_NothingToCompact_SkipsLlmCallEntirely()
    {
        var client = new ScriptedStubChatClient(_ => "summary");
        var ctx = NewContext(client);
        // A single small turn-group fits entirely inside the preserved tail — nothing older to summarize.
        ctx.History.Add(new ChatMessage(ChatRole.User, "hi"));
        ctx.History.Add(new ChatMessage(ChatRole.Assistant, "hello"));

        var (success, error, _, _) = await ReplCommands.CompactHistoryAsync(
            ctx, focus: null, CancellationToken.None);

        Assert.False(success);
        Assert.Equal("nothing_to_compact", error);
        Assert.Equal(0, client.CallCount);
    }
}
