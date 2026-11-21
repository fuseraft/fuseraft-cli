using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

public sealed class ReplTurnTrimHistoryTests
{
    private static ChatMessage User(string text) => new(ChatRole.User, text);
    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);

    [Fact]
    public void TrimHistory_DropsTheOldestTurnsFirst()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "sys"),
            User("old " + new string('a', 40_000)), Assistant("old reply"),
            User("latest"), Assistant("latest reply"),
        };

        var removed = ReplTurn.TrimHistory(history, contextTokenBudget: 1_000);

        Assert.Equal(2, removed);
        Assert.Equal(["sys", "latest", "latest reply"], history.Select(m => m.Text));
    }

    [Fact]
    public void TrimHistory_KeepsTheCompactionSummary_AndDropsTheTurnsAfterIt()
    {
        var summary = $"{ReplCommands.CompactedContextPrefix}\n\nwhat happened before";
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "sys"),
            User(summary), Assistant("kept round"),
            User("big " + new string('a', 40_000)), Assistant("big reply"),
            User("latest"), Assistant("latest reply"),
        };

        var removed = ReplTurn.TrimHistory(history, contextTokenBudget: 1_000);

        Assert.Equal(2, removed);
        Assert.Equal(["sys", summary, "kept round", "latest", "latest reply"], history.Select(m => m.Text));
    }

    [Fact]
    public void TrimHistory_SummaryAndOneTurn_RemovesNothing()
    {
        var history = new List<ChatMessage>
        {
            User($"{ReplCommands.CompactedContextPrefix}\n\n" + new string('s', 40_000)),
            User("latest"), Assistant("latest reply"),
        };

        Assert.Equal(0, ReplTurn.TrimHistory(history, contextTokenBudget: 1_000));
        Assert.Equal(3, history.Count);
    }

    [Fact]
    public void LargeMessageWarning_OnlyForAMessageThatTakesALargeShareOfTheBudget()
    {
        Assert.Null(ReplTurn.LargeMessageWarning(chars: 4_000, contextTokenBudget: 200_000));

        // A 3,000-line build log: about 200K tokens, the whole budget.
        var warning = ReplTurn.LargeMessageWarning(chars: 800_000, contextTokenBudget: 200_000);

        Assert.NotNull(warning);
        Assert.Contains("100%", warning);
        Assert.Contains("saved to a file", warning);
    }
}
