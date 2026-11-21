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

    private static int VerbatimGroupsKept(ReplSessionContext ctx) =>
        ctx.History.Count(m => m.Role == ChatRole.User
            && (m.Text == LastGroupMarker || (m.Text?.StartsWith("synthetic-group-") ?? false)));

    private async Task<int> GroupsKeptAtRatioAsync(double? ratio)
    {
        var ctx = NewContext(new ScriptedStubChatClient(_ => "concise handoff summary"));
        ctx.UserCfg = new fuseraft.Core.Models.Config.UserConfig
        {
            Repl = new fuseraft.Core.Models.Config.ReplDefaultsConfig { CompactPreserveTailRatio = ratio },
        };
        AddSyntheticHistory(ctx);

        var (success, error, _, _) = await ReplCommands.CompactHistoryAsync(ctx, focus: null, CancellationToken.None);

        Assert.True(success, error);
        return VerbatimGroupsKept(ctx);
    }

    [Fact]
    public async Task CompactHistoryAsync_KeepsMoreOrFewerRecentTurns_AsThePreserveRatioIsRaisedOrLowered()
    {
        var atDefault = await GroupsKeptAtRatioAsync(null);
        var narrow    = await GroupsKeptAtRatioAsync(0.05);
        var wide      = await GroupsKeptAtRatioAsync(0.5);

        Assert.True(narrow < atDefault, $"0.05 kept {narrow}, default kept {atDefault}");
        Assert.True(wide > atDefault, $"0.5 kept {wide}, default kept {atDefault}");
        Assert.Equal(1, narrow); // the most recent group is always kept, however small the ratio
    }

    // One request answered by many tool rounds, as an agentic task runs: a single turn-group far larger than the window.
    private const string RequestMarker = "UNIQUE-REQUEST: implement it, then tell me plainly what you couldn't check";

    private static void AddOneLongTurn(ReplSessionContext ctx, int rounds = 20)
    {
        ctx.History.Add(new ChatMessage(ChatRole.User, RequestMarker));
        for (int i = 0; i < rounds; i++)
        {
            ctx.History.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"c{i}", "read_file", new Dictionary<string, object?> { ["path"] = $"f{i}" })]));
            ctx.History.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"c{i}", new string('r', 25_000))]));
        }
    }

    [Fact]
    public async Task CompactHistoryAsync_SplitsOneLongTurn_KeepingItsRecentRoundsAndTheRequestWordForWord()
    {
        var client = new ScriptedStubChatClient(_ => "concise handoff summary");
        var ctx = NewContext(client);
        AddOneLongTurn(ctx);

        var (success, error, _, _) = await ReplCommands.CompactHistoryAsync(ctx, focus: null, CancellationToken.None);

        Assert.True(success, error);
        var body = ctx.History.Where(m => m.Role != ChatRole.System).ToList();
        Assert.Equal(ChatRole.User, body[0].Role);
        Assert.Contains(RequestMarker, body[0].Text);
        // The kept rounds start with a call, and every kept result has its call.
        Assert.Equal(ChatRole.Assistant, body[1].Role);
        var calls   = body.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId).ToHashSet();
        var results = body.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId).ToList();
        Assert.All(results, id => Assert.Contains(id, calls));
        Assert.Contains("c19", results);
        Assert.DoesNotContain("c0", results);
    }

    [Fact]
    public async Task CompactHistoryAsync_AsksTheSummaryToQuoteTheUsersRequests()
    {
        string? prompt = null;
        var ctx = NewContext(new ScriptedStubChatClient(m => { prompt = m.Last().Text; return "summary"; }));
        AddSyntheticHistory(ctx);

        await ReplCommands.CompactHistoryAsync(ctx, focus: null, CancellationToken.None);

        Assert.Contains("each request and instruction the user gave in the conversation above", prompt);
        Assert.Contains("This message is not one of them", prompt);
        Assert.Contains("later messages are kept word for word after this handoff", prompt);
    }

    // A pasted build log: thousands of lines, the first and last of which are distinct, ~100K tokens against the 80K default budget.
    private static string PastedLog(int lines = 4_000) =>
        "LOG-FIRST-LINE: please help me fix this build\n" +
        string.Concat(Enumerable.Range(0, lines).Select(i => $"  CSC : warning CS0618: line {i} {new string('w', 80)}\n")) +
        "LOG-LAST-LINE: Build FAILED. 1 error";

    [Fact]
    public async Task CompactHistoryAsync_PastedLogBiggerThanTheWindow_IsSummarizedAndQuotedOnlyAtItsEnds()
    {
        IEnumerable<ChatMessage>? summarized = null;
        var ctx = NewContext(new ScriptedStubChatClient(m => { summarized = m.ToList(); return "summary"; }));
        AddSyntheticHistory(ctx, groups: 2);
        var log = PastedLog();
        ctx.History.Add(new ChatMessage(ChatRole.User, log));
        ctx.History.Add(new ChatMessage(ChatRole.Assistant, "The failing project is X."));

        var (success, error, _, _) = await ReplCommands.CompactHistoryAsync(ctx, focus: null, CancellationToken.None);

        Assert.True(success, error);
        Assert.DoesNotContain(ctx.History, m => m.Text == log);
        var summary = ctx.History.First(m => ReplCommands.IsCompactedSummary(m.Text)).Text;
        Assert.Contains("LOG-FIRST-LINE", summary);
        Assert.Contains("LOG-LAST-LINE", summary);
        Assert.Contains("left out here", summary);
        Assert.True(ctx.EstimateTokens() < ctx.ContextTokenBudget / 2, $"{ctx.EstimateTokens()} tokens left");
        Assert.Equal("The failing project is X.", ctx.History[^1].Text);
        // The summarizer saw the log's start and end, not all of it.
        var sentLog = summarized!.Single(m => m.Text.Contains("LOG-FIRST-LINE")).Text;
        Assert.Contains("LOG-LAST-LINE", sentLog);
        Assert.True(sentLog.Length < log.Length / 2);
    }

    [Fact]
    public async Task CompactHistoryAsync_OversizedSummaryFromAnEarlierCompaction_IsCompactedAgain()
    {
        // What the old code left behind: a summary carrying a pasted log word for word, then small turns after it.
        // Every compaction found nothing older than the tail, and the trim then dropped the summary.
        var ctx = NewContext(new ScriptedStubChatClient(_ => "summary"));
        ctx.History.Add(new ChatMessage(ChatRole.User,
            $"{ReplCommands.CompactedContextPrefix}\n\nold summary\n\nThe request being worked on, word for word:\n{PastedLog()}"));
        ctx.History.Add(new ChatMessage(ChatRole.Assistant, "looking at it"));
        ctx.History.Add(new ChatMessage(ChatRole.User, "Continue"));
        ctx.History.Add(new ChatMessage(ChatRole.Assistant, "done"));

        var (success, error, _, _) = await ReplCommands.CompactHistoryAsync(ctx, focus: null, CancellationToken.None);

        Assert.True(success, error);
        Assert.True(ctx.EstimateTokens() < ctx.ContextTokenBudget / 2, $"{ctx.EstimateTokens()} tokens left");
        Assert.Equal("Continue", ctx.History[^2].Text);
    }

    [Fact]
    public async Task CompactHistoryAsync_SendsTheConversationsOwnPrefix_SoTheSummaryCallIsCached()
    {
        List<ChatMessage>? sent = null;
        var ctx = NewContext(new ScriptedStubChatClient(m => { sent = m.ToList(); return "summary"; }));
        AddSyntheticHistory(ctx);
        var systemPrompt = ctx.History[0];

        await ReplCommands.CompactHistoryAsync(ctx, focus: null, CancellationToken.None);

        // The same messages the conversation's last request started with, in the same order.
        Assert.Equal(ChatRole.System, systemPrompt.Role);
        Assert.Same(systemPrompt, sent![0]);
        Assert.StartsWith("synthetic-group-0-", sent[1].Text);
    }

    private sealed class RejectsToolChoiceNoneClient : IChatClient
    {
        public List<ChatOptions?> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(options);
            if (options?.ToolMode == ChatToolMode.None)
                throw new InvalidOperationException("400: tool_choice 'none' is not supported");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task CompactHistoryAsync_ProviderRejectsTheToolsOffCall_RetriesWithoutTools()
    {
        var client = new RejectsToolChoiceNoneClient();
        var ctx = NewContext(client);
        ctx.ChatOptions = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "", "read_file")] };
        AddSyntheticHistory(ctx);

        var (success, error, _, _) = await ReplCommands.CompactHistoryAsync(ctx, focus: null, CancellationToken.None);

        Assert.True(success, error);
        Assert.Equal(2, client.Calls.Count);
        Assert.Null(client.Calls[1]);
    }

    [Fact]
    public void SummaryCallOptions_KeepsTheSessionsTools_ButNoneCanBeCalled()
    {
        var tool = AIFunctionFactory.Create(() => "ran", "read_file", "Reads a file.");
        var session = new ChatOptions { Tools = [tool], Temperature = 0.2f };

        var options = ReplCommands.SummaryCallOptions(session)!;

        var declared = Assert.Single(options.Tools!);
        Assert.IsNotAssignableFrom<AIFunction>(declared);
        Assert.Equal(("read_file", "Reads a file."), (declared.Name, declared.Description));
        Assert.Equal(ChatToolMode.None, options.ToolMode);
        Assert.Equal(0.2f, options.Temperature);
        Assert.Same(tool, Assert.Single(session.Tools));
        Assert.Null(ReplCommands.SummaryCallOptions(new ChatOptions()));
    }

    [Fact]
    public void KeepStartAndEnd_CutsAtLineBreaks_AndSaysHowMuchWasLeftOut()
    {
        Assert.Equal("short", ReplCommands.KeepStartAndEnd("short", 100));

        var log = PastedLog(lines: 1_000);
        var kept = ReplCommands.KeepStartAndEnd(log, 4_000);

        Assert.StartsWith("LOG-FIRST-LINE", kept);
        Assert.EndsWith("LOG-LAST-LINE: Build FAILED. 1 error", kept);
        Assert.Contains("lines (", kept);
        Assert.True(kept.Length < 4_500, $"{kept.Length} chars");
        // Whole lines on both sides of the note.
        Assert.All(kept.Split('\n').Where(l => l.Contains("CS0618")), l => Assert.EndsWith(new string('w', 80), l));
    }

    [Fact]
    public void CompactionFailureText_SaysWhy_AndOnlyRetryableReasonsSuggestCompactingLater()
    {
        Assert.Contains("nothing older to summarize", ReplCommands.CompactionFailureText("nothing_to_compact"));
        Assert.False(ReplCommands.CompactionCouldSucceedLater("nothing_to_compact"));
        Assert.False(ReplCommands.CompactionCouldSucceedLater("not_smaller"));
        Assert.True(ReplCommands.CompactionCouldSucceedLater("HTTP 500"));
        Assert.Equal("HTTP 500", ReplCommands.CompactionFailureText("HTTP 500"));
    }

    [Fact]
    public void CompactionNotNeeded_BelowTheAutoCompactThreshold_SaysSo_AndAboveItDoesNot()
    {
        var ctx = NewContext(new ScriptedStubChatClient(_ => "summary"));
        ctx.History.Add(new ChatMessage(ChatRole.User, "hi"));

        Assert.Contains("Not compacted", ReplCommands.CompactionNotNeeded(ctx));

        AddSyntheticHistory(ctx);
        Assert.Null(ReplCommands.CompactionNotNeeded(ctx));
    }
}
