using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

/// <summary>
/// Covers <see cref="ReplReplay.BuildTranscript"/> — the history-to-turns grouping behind the
/// "show previous turns when a session is resumed" feature (terminal and VS Code panel alike).
/// The interesting cases are the internal user-role messages that share <c>ctx.History</c> with
/// real input and must never surface as turns of their own.
/// </summary>
public sealed class ReplReplayTests
{
    private static ChatMessage System(string t = "sys") => new(ChatRole.System, t);
    private static ChatMessage User(string t)           => new(ChatRole.User, t);
    private static ChatMessage Asst(string t)           => new(ChatRole.Assistant, t);

    private static ChatMessage AsstCall(string name, string? text = null, IDictionary<string, object?>? args = null)
    {
        var contents = new List<AIContent>();
        if (text is not null) contents.Add(new TextContent(text));
        contents.Add(new FunctionCallContent(Guid.NewGuid().ToString("N"), name, args));
        return new ChatMessage(ChatRole.Assistant, contents);
    }

    private static ChatMessage ToolResult(string result) =>
        new(ChatRole.Tool, [new FunctionResultContent("id", result)]);

    [Fact]
    public void GroupsToolRoundsIntoOneTurn_DroppingToolResults()
    {
        var t = ReplReplay.BuildTranscript([
            System(),
            User("fix the bug"),
            AsstCall("read_file", "Let me look.", new Dictionary<string, object?> { ["path"] = "a.cs" }),
            ToolResult("file contents"),
            AsstCall("patch_file"),
            ToolResult("ok"),
            Asst("Fixed it."),
        ]);

        var turn = Assert.Single(t.Turns);
        Assert.Equal("fix the bug", turn.User);
        Assert.Equal("Let me look.\n\nFixed it.", turn.Assistant);
        Assert.Equal(["read_file", "patch_file"], turn.ToolCalls.Select(c => c.Name));
        Assert.Equal("a.cs", turn.ToolCalls[0].Args!["path"]);
        Assert.DoesNotContain("file contents", turn.Assistant);
        Assert.False(t.Compacted);
    }

    [Fact]
    public void SplitsTurnsOnRealUserMessages()
    {
        var t = ReplReplay.BuildTranscript([
            System(), User("one"), Asst("first"), User("two"), Asst("second"),
        ]);

        Assert.Equal(["one", "two"], t.Turns.Select(x => x.User));
        Assert.Equal(["first", "second"], t.Turns.Select(x => x.Assistant));
    }

    [Theory]
    [InlineData(ReplTurn.EmptyReplyCorrectionPrefix + " Respond to the user.")]
    [InlineData(ReplTurn.NoWriteToolCorrectionPrefix + " Please call write_file.")]
    [InlineData(ReplTurn.CriticRejectedCorrectionPrefix + "your last response and rejected it: wrong")]
    [InlineData(ReplTurn.TodoOpenCorrectionPrefix + "2 incomplete item(s):\n- [pending] x")]
    public void CorrectionMessages_AreNotTurns_ButTheirOutputBelongsToTheTurn(string correction)
    {
        Assert.True(ReplTurn.IsInternalCorrectionMessage(correction));

        var t = ReplReplay.BuildTranscript([
            System(), User("do the thing"), Asst("Partial answer."), User(correction), Asst("Corrected answer."),
        ]);

        var turn = Assert.Single(t.Turns);
        Assert.Equal("do the thing", turn.User);
        Assert.Equal("Partial answer.\n\nCorrected answer.", turn.Assistant);
    }

    [Fact]
    public void RealUserMessageSharingACorrectionWordingPrefixIsNotMisclassified()
    {
        Assert.False(ReplTurn.IsInternalCorrectionMessage("Your todo list is in the README, please update it"));
        Assert.False(ReplTurn.IsInternalCorrectionMessage("A critic wrote this review, summarise it"));
    }

    [Fact]
    public void StepSummaries_AndRunResults_EndTheTurn_AndSwallowTheirAssistantReply()
    {
        var t = ReplReplay.BuildTranscript([
            System(),
            User("before"), Asst("answer"),
            User("[Step 1 of 2 complete] write the file"),
            User("[Step 2 of 2 complete — findings below] inspect it"),
            Asst("trimmed step reply"),
            User($"{ReplCommands.RunResultPrefix}\nStatus: succeeded"),
            Asst("The run completed successfully."),
            User("after"), Asst("later answer"),
        ]);

        Assert.Equal(["before", "after"], t.Turns.Select(x => x.User));
        Assert.Equal("answer", t.Turns[0].Assistant);
        Assert.Equal("later answer", t.Turns[1].Assistant);
    }

    [Fact]
    public void CompactionSummary_IsFlagged_AndNeverShownAsATurn()
    {
        var t = ReplReplay.BuildTranscript([
            System(),
            User($"{ReplCommands.CompactedContextPrefix}\n\nSummary of everything so far"),
            User("recent question"), Asst("recent answer"),
        ]);

        Assert.True(t.Compacted);
        var turn = Assert.Single(t.Turns);
        Assert.Equal("recent question", turn.User);
    }

    [Fact]
    public void InterruptedFinalTurn_KeepsTheUserMessage_WithNoAssistantText()
    {
        var t = ReplReplay.BuildTranscript([System(), User("q1"), Asst("a1"), User("q2")]);

        Assert.Equal(2, t.Turns.Count);
        Assert.Equal("q2", t.Turns[1].User);
        Assert.Equal(string.Empty, t.Turns[1].Assistant);
        Assert.Empty(t.Turns[1].ToolCalls);
    }

    [Fact]
    public void LeakedToolCallText_IsSanitizedLikeTheLiveTurn()
    {
        var t = ReplReplay.BuildTranscript([System(), User("q"), Asst("to=functions.read_file {\"path\":\"x\"}")]);

        Assert.Equal(string.Empty, Assert.Single(t.Turns).Assistant);
    }

    [Fact]
    public void EmptyHistory_YieldsNoTurns()
    {
        Assert.Empty(ReplReplay.BuildTranscript([System()]).Turns);
        Assert.Empty(ReplReplay.BuildTranscript([]).Turns);
    }

    [Fact]
    public void SummarizeTools_GroupsRepeatsInFirstUseOrder()
    {
        ReplReplay.ReplayToolCall C(string n) => new(n, null);

        Assert.Equal("1 tool call: read_file", ReplReplay.SummarizeTools([C("read_file")]));
        Assert.Equal(
            "4 tool calls: read_file ×3, patch_file",
            ReplReplay.SummarizeTools([C("read_file"), C("patch_file"), C("read_file"), C("read_file")]));
    }
}
