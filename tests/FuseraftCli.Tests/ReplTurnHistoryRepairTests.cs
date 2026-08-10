using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

/// <summary>
/// Regression tests for <see cref="ReplTurn.RepairDanglingToolCalls"/>: guards against a
/// trailing <see cref="FunctionCallContent"/> left unresolved when a turn's stream ends
/// without a matching <see cref="FunctionResultContent"/> — which otherwise permanently
/// 400s every subsequent turn ("tool_use ids were found without tool_result blocks").
/// </summary>
public sealed class ReplTurnHistoryRepairTests
{
    [Fact]
    public void PairedHistory_IsUnchanged()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "list files"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "list_files")]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "ok")]),
        };

        ReplTurn.RepairDanglingToolCalls(history);

        Assert.Equal(3, history.Count);
    }

    [Fact]
    public void TrailingUnresolvedCall_GetsSyntheticResult()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "search the repo"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "shell_run")]),
        };

        ReplTurn.RepairDanglingToolCalls(history);

        Assert.Equal(3, history.Count);
        var repair = history[^1];
        Assert.Equal(ChatRole.Tool, repair.Role);
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(repair.Contents));
        Assert.Equal("call-1", result.CallId);
    }

    [Fact]
    public void MultipleTrailingUnresolvedCalls_AllGetPaired()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "do two things"),
            new(ChatRole.Assistant,
            [
                new FunctionCallContent("call-1", "shell_run"),
                new FunctionCallContent("call-2", "read_file"),
            ]),
        };

        ReplTurn.RepairDanglingToolCalls(history);

        var repair = history[^1];
        Assert.Equal(2, repair.Contents.Count);
        var callIds = repair.Contents.OfType<FunctionResultContent>().Select(r => r.CallId);
        Assert.Equal(["call-1", "call-2"], callIds);
    }

    [Fact]
    public void EmptyHistory_DoesNotThrow()
    {
        var history = new List<ChatMessage>();
        ReplTurn.RepairDanglingToolCalls(history);
        Assert.Empty(history);
    }

    [Fact]
    public void OnlyTheUnresolvedCall_GetsRepaired_EarlierPairsUntouched()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "step one"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "shell_run")]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "done")]),
            new(ChatRole.Assistant, "step one complete"),
            new(ChatRole.User, "step two"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-2", "shell_run")]),
        };

        ReplTurn.RepairDanglingToolCalls(history);

        Assert.Equal(7, history.Count);
        var repair = history[^1];
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(repair.Contents));
        Assert.Equal("call-2", result.CallId);
    }
}
