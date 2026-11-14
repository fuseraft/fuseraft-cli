using fuseraft.Cli.Commands.Repl;
using Microsoft.Extensions.AI;

namespace FuseraftCli.Tests;

/// <summary>An answer given again after nothing but bookkeeping is found; anything new, or after real work, is not.</summary>
public sealed class RepeatedAnswerTests
{
    // From live grok-4.7 turns (2026-11-13).
    private const string Answer =
        "The third read was `lib/httpserver.kiwi`, lines 1–400. You asked me to read those five file ranges one at a time, " +
        "with exactly one `read_file` call per step and no changes, then summarize what I read in two sentences.";
    private const string Reworded =
        "\n\nThe third read was `lib/httpserver.kiwi`, lines 1–400. You asked me to read those five ranges one at a time, " +
        "one `read_file` call per step and nothing changed, then summarize what I read in two sentences.";

    private static List<ChatResponseUpdate> Turn(string first, string tool, string last, int calls = 1)
    {
        var updates = new List<ChatResponseUpdate>
        {
            new() { Role = ChatRole.Assistant, MessageId = "m1", Contents = [new TextContent(first), new FunctionCallContent("c0", tool)] },
            new() { Role = ChatRole.Tool, MessageId = "t1", Contents = [new FunctionResultContent("c0", "ok")] },
        };
        for (var i = 1; i < calls; i++)
        {
            updates.Add(new() { Role = ChatRole.Assistant, MessageId = $"m{i + 1}", Contents = [new FunctionCallContent($"c{i}", tool)] });
            updates.Add(new() { Role = ChatRole.Tool, MessageId = $"t{i + 1}", Contents = [new FunctionResultContent($"c{i}", "ok")] });
        }
        updates.Add(new() { Role = ChatRole.Assistant, MessageId = "last", Contents = [new TextContent(last)] });
        // As a real stream ends: an update that only reports usage.
        updates.Add(new() { Role = ChatRole.Assistant, Contents = [new UsageContent(new UsageDetails { InputTokenCount = 10 })] });
        return updates;
    }

    [Theory]
    [InlineData("todo_write", 1)]
    [InlineData("todo_write", 2)]
    [InlineData("repl_session_get_context_status", 1)]
    public void TheSameAnswerAgain_AfterBookkeeping_IsFound(string tool, int calls)
    {
        Assert.Equal(Answer, RepeatedAnswer.Find(Turn(Answer, tool, Answer, calls)));
        Assert.Equal(Reworded, RepeatedAnswer.Find(Turn(Answer, tool, Reworded, calls)));
    }

    [Fact]
    public void AnAnswerAfterRealWork_IsKept() =>
        Assert.Null(RepeatedAnswer.Find(Turn(Answer, "shell_run", Reworded)));

    [Theory]
    [InlineData("Those two answers stand as given.")]
    [InlineData("I also checked the context: 11 % of the budget is used, so there's room for the next files.")]
    public void NewWords_AreKept(string last) =>
        Assert.Null(RepeatedAnswer.Find(Turn(Answer, "todo_write", last)));

    [Fact]
    public void ShortText_IsNeverJudged() =>
        Assert.Null(RepeatedAnswer.Find(Turn("I'll run the tests.", "todo_write", "The tests pass.")));
}
