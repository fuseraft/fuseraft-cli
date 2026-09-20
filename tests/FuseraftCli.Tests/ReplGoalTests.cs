using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

/// <summary>
/// Covers the pure parts of <c>/goal</c> (<see cref="ReplGoal"/>, <see cref="GoalState"/>): how a
/// judge reply is read, when the loop stops, and what the judge is shown. The loop itself is
/// exercised live against a real model — these pin the rules it relies on.
/// </summary>
public sealed class ReplGoalTests
{
    private static GoalVerdict V(bool complete = false, bool blocked = false, string missing = "tests fail", double score = 0.3) =>
        new(score, complete, blocked, missing);

    // ── ParseVerdict ────────────────────────────────────────────────────────────

    [Fact]
    public void ParseVerdict_ReadsPlainJson()
    {
        var v = ReplGoal.ParseVerdict("""{"score": 0.4, "complete": false, "blocked": false, "missing": "no test run"}""");

        Assert.Equal(0.4, v.Score);
        Assert.False(v.Complete);
        Assert.False(v.Blocked);
        Assert.Equal("no test run", v.Missing);
    }

    [Fact]
    public void ParseVerdict_ToleratesCodeFencesAndSurroundingProse()
    {
        var v = ReplGoal.ParseVerdict("Here is my verdict:\n```json\n{\"score\":1.0,\"complete\":true,\"missing\":\"\"}\n```\nThanks.");

        Assert.True(v.Complete);
        Assert.Equal(1.0, v.Score);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("I think it is done!")]
    [InlineData("{not json}")]
    [InlineData("[1,2,3]")]
    public void ParseVerdict_Unparseable_IsConservativelyNotComplete(string? raw)
    {
        var v = ReplGoal.ParseVerdict(raw);

        Assert.False(v.Complete);
        Assert.False(v.Blocked);
        Assert.Equal(0.0, v.Score);
        Assert.Contains("could not be parsed", v.Missing);
    }

    [Fact]
    public void ParseVerdict_ClampsScore()
    {
        Assert.Equal(1.0, ReplGoal.ParseVerdict("""{"score": 7, "complete": true}""").Score);
        Assert.Equal(0.0, ReplGoal.ParseVerdict("""{"score": -2, "complete": false}""").Score);
    }

    [Fact]
    public void ParseVerdict_MissingCompleteFlag_FallsBackToScore()
    {
        Assert.True(ReplGoal.ParseVerdict("""{"score": 1.0}""").Complete);
        Assert.False(ReplGoal.ParseVerdict("""{"score": 0.9}""").Complete);
    }

    [Fact]
    public void ParseVerdict_CompleteThatStillListsMissingWork_IsNotComplete()
    {
        // A self-contradicting verdict: trust the specific (something is missing) over the summary flag.
        var v = ReplGoal.ParseVerdict("""{"score": 0.9, "complete": true, "missing": "lint not run"}""");

        Assert.False(v.Complete);
        Assert.Equal("lint not run", v.Missing);
    }

    [Fact]
    public void ParseVerdict_BlockedIsOnlyHonouredWhenNotComplete()
    {
        Assert.True(ReplGoal.ParseVerdict("""{"score":0.2,"complete":false,"blocked":true,"missing":"needs a DB choice"}""").Blocked);
        Assert.False(ReplGoal.ParseVerdict("""{"score":1.0,"complete":true,"blocked":true,"missing":""}""").Blocked);
    }

    [Fact]
    public void ParseVerdict_WrongTypedFieldsAreIgnoredNotThrown()
    {
        var v = ReplGoal.ParseVerdict("""{"score": "high", "complete": "yes", "blocked": "maybe", "missing": 5}""");

        Assert.Equal(0.0, v.Score);
        Assert.False(v.Complete);
        Assert.False(v.Blocked);
        Assert.Equal(string.Empty, v.Missing);
    }

    // ── GoalState.Record ────────────────────────────────────────────────────────

    [Fact]
    public void Record_CompleteWinsOverEverythingIncludingTheCap()
    {
        var g = new GoalState("x", maxIterations: 1);

        Assert.Equal(GoalAction.Complete, g.Record(V(complete: true, missing: "")));
    }

    [Fact]
    public void Record_ContinuesUntilTheCap_ThenReportsCapped()
    {
        var g = new GoalState("x", maxIterations: 3);

        Assert.Equal(GoalAction.Continue, g.Record(V(missing: "a")));
        Assert.Equal(GoalAction.Continue, g.Record(V(missing: "b")));
        Assert.Equal(GoalAction.Capped,   g.Record(V(missing: "c")));
        Assert.Equal(3, g.Iteration);
    }

    [Fact]
    public void Record_Blocked_StopsImmediately_WithoutSpendingTheBudget()
    {
        var g = new GoalState("x", maxIterations: 10);

        Assert.Equal(GoalAction.Blocked, g.Record(V(blocked: true, missing: "pick a database")));
    }

    [Fact]
    public void Record_SameMissingWorkThreeTimesRunning_IsStalled()
    {
        var g = new GoalState("x", maxIterations: 10);

        Assert.Equal(GoalAction.Continue, g.Record(V(missing: "Tests are failing.")));
        Assert.Equal(GoalAction.Continue, g.Record(V(missing: "tests are failing")));   // case/punctuation-insensitive
        Assert.Equal(GoalAction.Stalled,  g.Record(V(missing: "Tests   are failing!")));
    }

    [Fact]
    public void Record_ChangingMissingWork_ResetsTheStallRun()
    {
        var g = new GoalState("x", maxIterations: 10);

        g.Record(V(missing: "a"));
        g.Record(V(missing: "a"));
        Assert.Equal(GoalAction.Continue, g.Record(V(missing: "b")));  // progress: run restarts
        Assert.Equal(GoalAction.Continue, g.Record(V(missing: "b")));
        Assert.Equal(GoalAction.Stalled,  g.Record(V(missing: "b")));
    }

    [Fact]
    public void Record_EmptyMissing_NeverCountsAsAStall()
    {
        // An unhelpful judge that reports nothing specific must not trip the stall rule; the cap bounds it.
        var g = new GoalState("x", maxIterations: 10);

        for (var i = 0; i < 5; i++)
            Assert.Equal(GoalAction.Continue, g.Record(V(missing: "")));
    }

    [Fact]
    public void Record_KeepsTheLatestVerdict()
    {
        var g = new GoalState("x", maxIterations: 5);
        var second = V(missing: "second", score: 0.6);

        g.Record(V(missing: "first"));
        g.Record(second);

        Assert.Same(second, g.LastVerdict);
    }

    // ── Transcript ──────────────────────────────────────────────────────────────

    [Fact]
    public void RenderTranscript_OmitsTheSystemPrompt_AndShowsCallsAndResults()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "SECRET-SYSTEM-PROMPT"),
            new(ChatRole.User, "fix the build"),
            new(ChatRole.Assistant, [new TextContent("Running tests."), new FunctionCallContent("c1", "shell_run", new Dictionary<string, object?> { ["command"] = "dotnet test" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "Passed: 12")]),
            new(ChatRole.Assistant, "All green."),
        };

        var t = ReplGoal.RenderTranscript(history);

        Assert.DoesNotContain("SECRET-SYSTEM-PROMPT", t);
        Assert.Contains("user: fix the build", t);
        Assert.Contains("[tool call] shell_run(", t);
        Assert.Contains("dotnet test", t);
        Assert.Contains("[tool result] Passed: 12", t);
        Assert.Contains("assistant: All green.", t);
    }

    [Fact]
    public void RenderTranscript_TruncatesHugeToolResults()
    {
        var huge = new string('x', 50_000);
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "go"),
            new(ChatRole.Tool, [new FunctionResultContent("c", huge)]),
        };

        var t = ReplGoal.RenderTranscript(history);

        Assert.True(t.Length < 3_000, $"transcript was {t.Length} chars");
        Assert.Contains("(truncated)", t);
    }

    [Fact]
    public void RenderTranscript_OverBudget_DropsOldestEntries_AndSaysSo()
    {
        var history = new List<ChatMessage>();
        for (var i = 0; i < 40; i++) history.Add(new ChatMessage(ChatRole.User, $"message-{i:D2} " + new string('y', 200)));

        var t = ReplGoal.RenderTranscript(history, maxChars: 2_000);

        Assert.Contains("earlier message(s) omitted", t);
        Assert.DoesNotContain("message-00", t);
        Assert.Contains("message-39", t);
        Assert.True(t.Length < 2_400);
    }

    [Fact]
    public void RenderTranscript_MarksImagesInsteadOfInliningThem()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextContent("see this"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")]),
        };

        var t = ReplGoal.RenderTranscript(history);

        Assert.Contains("see this", t);
        Assert.Contains("[attachment]", t);
    }

    // ── Prompts ─────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildJudgePrompt_SurvivesBracesInTheObjectiveAndTranscript()
    {
        var p = ReplGoal.BuildJudgePrompt("make {0} and {bar} work", "user: {\"a\": 1} {1}");

        Assert.Contains("make {0} and {bar} work", p);
        Assert.Contains("user: {\"a\": 1} {1}", p);
        Assert.Contains("{\"score\":", p);   // the template's own JSON example, with its braces un-doubled
    }

    [Fact]
    public void FollowUpAndResumeMessages_AreRecognisedAsInternal()
    {
        Assert.True(ReplTurn.IsInternalCorrectionMessage(ReplGoal.BuildFollowUp(2, "lint")));
        Assert.True(ReplTurn.IsInternalCorrectionMessage(ReplGoal.BuildResumeMessage("ship it")));
        Assert.False(ReplTurn.IsInternalCorrectionMessage("The goal is to ship it"));
    }

    [Fact]
    public void FollowUp_CarriesTheOutstandingWork_AndFallsBackWhenNoneGiven()
    {
        Assert.Contains("Outstanding: lint not run", ReplGoal.BuildFollowUp(1, "lint not run"));
        Assert.Contains("not yet verified", ReplGoal.BuildFollowUp(1, "  "));
    }

    [Fact]
    public void FollowUpMessages_DoNotBecomeReplayTurns_TheyContinueTheGoalTurn()
    {
        var t = ReplReplay.BuildTranscript([
            new ChatMessage(ChatRole.System, "sys"),
            new ChatMessage(ChatRole.User, "make it faster"),
            new ChatMessage(ChatRole.Assistant, "First pass."),
            new ChatMessage(ChatRole.User, ReplGoal.BuildFollowUp(1, "no benchmark")),
            new ChatMessage(ChatRole.Assistant, "Benchmarked."),
        ]);

        var turn = Assert.Single(t.Turns);
        Assert.Equal("make it faster", turn.User);
        Assert.Contains("Benchmarked.", turn.Assistant);
    }

    // ── Arguments ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("fix the tests",              5, "fix the tests")]
    [InlineData("--max 8 fix the tests",      8, "fix the tests")]
    [InlineData("--max=3 fix it",             3, "fix it")]
    [InlineData("  --max 12   spaced   out ", 12, "spaced   out")]
    [InlineData("resume",                     5, "resume")]
    [InlineData("--max 4 resume",             4, "resume")]
    [InlineData("",                           5, "")]
    [InlineData("mention --max in the middle", 5, "mention --max in the middle")]
    public void TryParseGoalArgs_Accepts(string arg, int expectedMax, string expectedRest)
    {
        Assert.True(ReplCommands.TryParseGoalArgs(arg, out var max, out var rest, out var error));
        Assert.Equal(expectedMax, max);
        Assert.Equal(expectedRest, rest);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("--max")]
    [InlineData("--max abc do it")]
    [InlineData("--max 0 do it")]
    [InlineData("--max 51 do it")]
    [InlineData("--max -3 do it")]
    public void TryParseGoalArgs_Rejects(string arg)
    {
        Assert.False(ReplCommands.TryParseGoalArgs(arg, out _, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    // ── Judge call ──────────────────────────────────────────────────────────────

    private sealed class RecordingClient(Func<string> reply, Exception? throwWith = null) : IChatClient
    {
        public List<List<ChatMessage>> Calls { get; } = [];
        public ChatOptions? LastOptions { get; private set; }

        public object? GetService(Type t, object? k) => null;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> msgs, ChatOptions? opts, CancellationToken ct)
        {
            Calls.Add(msgs.ToList());
            LastOptions = opts;
            if (throwWith is not null) throw throwWith;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply())));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> msgs, ChatOptions? opts, CancellationToken ct)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();

        public void Dispose() { }
    }

    [Fact]
    public async Task JudgeAsync_SendsObjectiveAndTranscript_WithNoTools_AndParsesTheReply()
    {
        var client = new RecordingClient(() => """{"score":0.5,"complete":false,"blocked":false,"missing":"no tests"}""");
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "AGENT-SYSTEM-PROMPT"),
            new(ChatRole.User, "add a cache"),
            new(ChatRole.Assistant, "Added it."),
        };

        var v = await ReplGoal.JudgeAsync(client, "add a cache with tests", history, CancellationToken.None);

        var call = Assert.Single(client.Calls);
        var sent = Assert.Single(call);
        Assert.Equal(ChatRole.User, sent.Role);
        Assert.Contains("add a cache with tests", sent.Text);
        Assert.Contains("assistant: Added it.", sent.Text);
        Assert.DoesNotContain("AGENT-SYSTEM-PROMPT", sent.Text);
        Assert.Null(client.LastOptions?.Tools);                 // the judge must not be able to act
        Assert.Equal("no tests", v.Missing);
        Assert.False(v.Complete);
    }

    [Fact]
    public async Task JudgeAsync_ProviderFailure_Propagates_SoTheLoopStopsInsteadOfGuessing()
    {
        var client = new RecordingClient(() => "", throwWith: new HttpRequestException("503"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            ReplGoal.JudgeAsync(client, "x", [new ChatMessage(ChatRole.User, "y")], CancellationToken.None));
    }

    [Fact]
    public async Task JudgeAsync_GarbageReply_IsNotComplete()
    {
        var client = new RecordingClient(() => "sure, looks good to me");

        var v = await ReplGoal.JudgeAsync(client, "x", [new ChatMessage(ChatRole.User, "y")], CancellationToken.None);

        Assert.False(v.Complete);
    }

    // ── Describe ────────────────────────────────────────────────────────────────

    [Fact]
    public void DescribeGoalEnd_ReadsSensibly()
    {
        Assert.Contains("complete (2 audits", ReplCommands.DescribeGoalEnd(new GoalRecord("x", GoalEnd.Complete, 2, V(complete: true, missing: "", score: 0.95))));
        Assert.Contains("interrupted after 1 audit.", ReplCommands.DescribeGoalEnd(new GoalRecord("x", GoalEnd.Interrupted, 1, null)));
        Assert.Contains("before the first audit", ReplCommands.DescribeGoalEnd(new GoalRecord("x", GoalEnd.Interrupted, 0, null)));
        Assert.Contains("needs your input", ReplCommands.DescribeGoalEnd(new GoalRecord("x", GoalEnd.Blocked, 1, V(blocked: true, missing: "pick one"))));
        Assert.Contains("Outstanding: tests fail", ReplCommands.DescribeGoalEnd(new GoalRecord("x", GoalEnd.Capped, 5, V())));
    }
}
