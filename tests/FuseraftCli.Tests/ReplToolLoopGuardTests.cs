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

    // Alternation (A/B/A/B) and failure-streak notices

    private static FunctionInvocationContext MakeContextWith(
        AIFunction function, int iteration, string cmd) => new()
    {
        Iteration   = iteration,
        Function    = function,
        Arguments   = new AIFunctionArguments(new Dictionary<string, object?> { ["cmd"] = cmd }),
        CallContent = new FunctionCallContent($"call-{iteration}", "shell_run",
            new Dictionary<string, object?> { ["cmd"] = cmd }),
    };

    // "bad*" fails the way plugins report failure (a bracketed prefix); "throw*" throws; the rest succeed.
    private static readonly AIFunction FlakyFunction = AIFunctionFactory.Create(
        (string cmd) => cmd.StartsWith("throw", StringComparison.Ordinal)
            ? throw new InvalidOperationException("boom")
            : cmd.StartsWith("bad", StringComparison.Ordinal) ? "[ERROR] nope" : $"ran: {cmd}",
        "shell_run");

    [Fact]
    public async Task AlternatingTwoCalls_SixthCallGetsNotice_ButNotBefore()
    {
        var guard = new ReplToolLoopGuard();
        var results = new List<string>();
        for (var i = 0; i < 6; i++)
            results.Add((await guard.InvokeAsync(MakeContextWith(FlakyFunction, i, i % 2 == 0 ? "read" : "test"),
                CancellationToken.None))?.ToString() ?? "");

        Assert.All(results.Take(5), r => Assert.DoesNotContain("SYSTEM NOTICE", r));
        Assert.Contains("ran: test", results[5]);            // real result preserved
        Assert.Contains("alternated between the same two calls", results[5]);
    }

    [Fact]
    public async Task AlternationNotice_FiresOncePerRun()
    {
        var guard = new ReplToolLoopGuard();
        string? seventh = null;
        for (var i = 0; i < 7; i++)
            seventh = (await guard.InvokeAsync(MakeContextWith(FlakyFunction, i, i % 2 == 0 ? "read" : "test"),
                CancellationToken.None))?.ToString();

        Assert.DoesNotContain("SYSTEM NOTICE", seventh);
    }

    [Fact]
    public async Task VariedCalls_NeverTriggerTheAlternationNotice()
    {
        var guard = new ReplToolLoopGuard();
        for (var i = 0; i < 12; i++)
        {
            var r = await guard.InvokeAsync(MakeContextWith(FlakyFunction, i, $"step-{i}"), CancellationToken.None);
            Assert.DoesNotContain("SYSTEM NOTICE", r?.ToString());
        }
    }

    [Fact]
    public async Task SecondConsecutiveFailure_GetsNudgeAppendedAfterTheRealError()
    {
        var guard = new ReplToolLoopGuard();

        var r1 = (await guard.InvokeAsync(MakeContextWith(FlakyFunction, 0, "bad-1"), CancellationToken.None))?.ToString();
        var r2 = (await guard.InvokeAsync(MakeContextWith(FlakyFunction, 1, "bad-2"), CancellationToken.None))?.ToString();

        Assert.DoesNotContain("SYSTEM NOTICE", r1);
        Assert.StartsWith("[ERROR] nope", r2);                       // still classified as a failure by ReplTurn
        Assert.Contains("2 tool calls in a row have failed", r2);
        Assert.Contains("one more failure will end this turn", r2);
    }

    [Fact]
    public async Task FailureNudge_FiresOnce_NotAgainOnTheCutoffCall()
    {
        var guard = new ReplToolLoopGuard();
        string? third = null;
        for (var i = 0; i < 3; i++)
            third = (await guard.InvokeAsync(MakeContextWith(FlakyFunction, i, $"bad-{i}"), CancellationToken.None))?.ToString();

        Assert.DoesNotContain("SYSTEM NOTICE", third);
    }

    [Fact]
    public async Task ASuccessBetweenFailures_ResetsTheFailureStreak()
    {
        var guard = new ReplToolLoopGuard();
        await guard.InvokeAsync(MakeContextWith(FlakyFunction, 0, "bad-1"), CancellationToken.None);
        await guard.InvokeAsync(MakeContextWith(FlakyFunction, 1, "fine"), CancellationToken.None);
        var r = (await guard.InvokeAsync(MakeContextWith(FlakyFunction, 2, "bad-2"), CancellationToken.None))?.ToString();

        Assert.DoesNotContain("SYSTEM NOTICE", r);   // the 2nd failure overall, but not 2 in a row
    }

    [Fact]
    public async Task ThrownInvocation_CountsTowardTheStreak_AndStillPropagates()
    {
        var guard = new ReplToolLoopGuard();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await guard.InvokeAsync(MakeContextWith(FlakyFunction, 0, "throw-1"), CancellationToken.None));
        var r = (await guard.InvokeAsync(MakeContextWith(FlakyFunction, 1, "bad-2"), CancellationToken.None))?.ToString();

        Assert.Contains("2 tool calls in a row have failed", r);
    }

    [Fact]
    public async Task IterationZero_ResetsTheFailureStreakForANewTurn()
    {
        var guard = new ReplToolLoopGuard();
        await guard.InvokeAsync(MakeContextWith(FlakyFunction, 3, "bad-1"), CancellationToken.None);

        var r = (await guard.InvokeAsync(MakeContextWith(FlakyFunction, 0, "bad-2"), CancellationToken.None))?.ToString();

        Assert.DoesNotContain("SYSTEM NOTICE", r);   // first failure of the new turn, not the 2nd
    }

    [Fact]
    public async Task IterationZero_ResetsTheAlternationRunForANewTurn()
    {
        var guard = new ReplToolLoopGuard();
        for (var i = 1; i <= 5; i++)
            await guard.InvokeAsync(MakeContextWith(FlakyFunction, i, i % 2 == 1 ? "read" : "test"), CancellationToken.None);

        // Would be the 6th alternating call had the previous turn's run carried over.
        var r = (await guard.InvokeAsync(MakeContextWith(FlakyFunction, 0, "test"), CancellationToken.None))?.ToString();

        Assert.DoesNotContain("SYSTEM NOTICE", r);
    }

    [Theory]
    [InlineData("[ERROR] x", true)]
    [InlineData("[FAIL] x", true)]
    [InlineData("[DENIED] x", true)]
    [InlineData("[NOT FOUND] x", true)]
    [InlineData("[TIMEOUT] x", true)]
    [InlineData("[EXIT 2]\nstderr", true)]
    [InlineData("[OK] done", false)]
    [InlineData("[INFO] fyi", false)]
    [InlineData("plain stdout", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsToolFailureText_RecognisesThePluginFailureConventions(string? text, bool expected) =>
        Assert.Equal(expected, ReplTurn.IsToolFailureText(text));
}
