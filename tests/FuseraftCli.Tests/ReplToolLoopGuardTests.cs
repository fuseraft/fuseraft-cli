using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

/// <summary>
/// Regression tests for <see cref="ReplToolLoopGuard"/> — the
/// <see cref="ReplTurn.SoftRepeatedToolCallThreshold"/> mid-turn nudge, installed as
/// <see cref="FunctionInvokingChatClient.FunctionInvoker"/> only on the REPL's free-form client
/// (see <c>ReplFactory.BuildClient</c>). Exercised directly against a fake
/// <see cref="FunctionInvocationContext"/> rather than through a full REPL turn, since that's
/// the actual seam this class plugs into.
/// </summary>
public sealed class ReplToolLoopGuardTests
{
    private static readonly AIFunction EchoFunction =
        AIFunctionFactory.Create((string cmd) => $"ran: {cmd}", "shell_run");

    private static FunctionInvocationContext MakeContext(
        int iteration, string toolName, Dictionary<string, object?>? args) => new()
    {
        Iteration    = iteration,
        Function     = EchoFunction,
        Arguments    = new AIFunctionArguments(args),
        CallContent  = new FunctionCallContent($"call-{iteration}", toolName, args),
    };

    [Fact]
    public async Task FirstTwoIdenticalCalls_NoNoticeAppended()
    {
        var guard = new ReplToolLoopGuard();
        var args  = new Dictionary<string, object?> { ["cmd"] = "ls" };

        var r1 = await guard.InvokeAsync(MakeContext(0, "shell_run", args), CancellationToken.None);
        var r2 = await guard.InvokeAsync(MakeContext(1, "shell_run", args), CancellationToken.None);

        Assert.DoesNotContain("SYSTEM NOTICE", r1?.ToString());
        Assert.DoesNotContain("SYSTEM NOTICE", r2?.ToString());
    }

    [Fact]
    public async Task ThirdIdenticalCall_GetsNoticeAppendedToRealResult()
    {
        var guard = new ReplToolLoopGuard();
        var args  = new Dictionary<string, object?> { ["cmd"] = "ls" };

        await guard.InvokeAsync(MakeContext(0, "shell_run", args), CancellationToken.None);
        await guard.InvokeAsync(MakeContext(1, "shell_run", args), CancellationToken.None);
        var r3 = await guard.InvokeAsync(MakeContext(2, "shell_run", args), CancellationToken.None);

        var text = r3?.ToString() ?? "";
        Assert.Contains("ran: ls", text);      // the real tool result is preserved verbatim
        Assert.Contains("SYSTEM NOTICE", text); // riding in the SAME returned value/message
    }

    [Fact]
    public async Task FourthIdenticalCall_NoNoticeAppended_FiresOncePerStreak()
    {
        var guard = new ReplToolLoopGuard();
        var args  = new Dictionary<string, object?> { ["cmd"] = "ls" };

        for (var i = 0; i < 3; i++)
            await guard.InvokeAsync(MakeContext(i, "shell_run", args), CancellationToken.None);
        var r4 = await guard.InvokeAsync(MakeContext(3, "shell_run", args), CancellationToken.None);

        Assert.DoesNotContain("SYSTEM NOTICE", r4?.ToString());
    }

    [Fact]
    public async Task DifferentArguments_ResetTheStreak()
    {
        var guard = new ReplToolLoopGuard();
        await guard.InvokeAsync(MakeContext(0, "shell_run", new() { ["cmd"] = "ls" }), CancellationToken.None);
        await guard.InvokeAsync(MakeContext(1, "shell_run", new() { ["cmd"] = "ls" }), CancellationToken.None);
        // Different argument breaks the streak — the 3rd call overall must not trip the notice.
        var r3 = await guard.InvokeAsync(MakeContext(2, "shell_run", new() { ["cmd"] = "pwd" }), CancellationToken.None);

        Assert.DoesNotContain("SYSTEM NOTICE", r3?.ToString());
    }

    [Fact]
    public async Task DifferentToolName_ResetsTheStreakEvenWithSameArguments()
    {
        var guard = new ReplToolLoopGuard();
        var args  = new Dictionary<string, object?> { ["cmd"] = "ls" };
        await guard.InvokeAsync(MakeContext(0, "shell_run", args), CancellationToken.None);
        await guard.InvokeAsync(MakeContext(1, "shell_run", args), CancellationToken.None);
        var r3 = await guard.InvokeAsync(MakeContext(2, "other_tool", args), CancellationToken.None);

        Assert.DoesNotContain("SYSTEM NOTICE", r3?.ToString());
    }

    [Fact]
    public async Task IterationZero_ResetsStateForANewTurn()
    {
        var guard = new ReplToolLoopGuard();
        var args  = new Dictionary<string, object?> { ["cmd"] = "ls" };

        // Two identical calls late in a turn (iterations 5, 6) — one away from the streak that
        // would trip the notice on the same client instance if state leaked across turns.
        await guard.InvokeAsync(MakeContext(5, "shell_run", args), CancellationToken.None);
        await guard.InvokeAsync(MakeContext(6, "shell_run", args), CancellationToken.None);

        // A new top-level turn starts at Iteration 0 — must not be treated as this call being
        // the 3rd in the previous turn's streak.
        var r = await guard.InvokeAsync(MakeContext(0, "shell_run", args), CancellationToken.None);

        Assert.DoesNotContain("SYSTEM NOTICE", r?.ToString());
    }

    [Fact]
    public async Task StreakCanFireAgainAfterReset()
    {
        var guard = new ReplToolLoopGuard();
        var args  = new Dictionary<string, object?> { ["cmd"] = "ls" };

        await guard.InvokeAsync(MakeContext(0, "shell_run", args), CancellationToken.None);
        await guard.InvokeAsync(MakeContext(1, "shell_run", args), CancellationToken.None);
        await guard.InvokeAsync(MakeContext(2, "shell_run", args), CancellationToken.None); // 3rd - fires

        // New turn (Iteration resets to 0) — a fresh 3-in-a-row streak must fire again.
        await guard.InvokeAsync(MakeContext(0, "shell_run", args), CancellationToken.None);
        await guard.InvokeAsync(MakeContext(1, "shell_run", args), CancellationToken.None);
        var r3 = await guard.InvokeAsync(MakeContext(2, "shell_run", args), CancellationToken.None);

        Assert.Contains("SYSTEM NOTICE", r3?.ToString());
    }
}
