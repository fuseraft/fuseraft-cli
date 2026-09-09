using System.Text.Json;
using Microsoft.Extensions.AI;

namespace FuseraftCli.Tests;

/// <summary>
/// Regression tests for <see cref="AgentToolLoopGuard"/> — orchestration's counterpart to the
/// REPL's <c>ReplToolLoopGuard</c>, installed as
/// <see cref="FunctionInvokingChatClient.FunctionInvoker"/> for every orchestration agent
/// (<c>AgentFactory.Create</c>) and for <c>SubAgentPlugin.RunLoopAsync</c>. Unlike the REPL's
/// guard, this one implements both the soft-nudge and hard-cutoff tiers itself, since
/// orchestration has no separate stream-chunk-parsing backstop to lean on — and unlike the REPL's
/// guard, it never modifies the returned tool result (see the class doc comment for why:
/// verified empirically that doing so can interact badly with
/// <c>AgentContextCompactionFilters.TrimInTurnContext</c>, which both orchestration paths run
/// through but the REPL's client does not). Exercised directly against a fake
/// <see cref="FunctionInvocationContext"/>, mirroring <c>ReplToolLoopGuardTests</c>'s pattern.
/// </summary>
public sealed class AgentToolLoopGuardTests
{
    private static readonly AIFunction EchoFunction =
        AIFunctionFactory.Create((string cmd) => $"ran: {cmd}", "shell_run");

    private static FunctionInvocationContext MakeContext(
        int iteration, string toolName, Dictionary<string, object?>? args) => new()
    {
        Iteration   = iteration,
        Function    = EchoFunction,
        Arguments   = new AIFunctionArguments(args),
        CallContent = new FunctionCallContent($"call-{iteration}", toolName, args),
    };

    [Fact]
    public async Task ResultIsNeverModified_RegardlessOfStreak()
    {
        var guard = new AgentToolLoopGuard("agent-a", emitter: null);
        var args  = new Dictionary<string, object?> { ["cmd"] = "ls" };

        for (var i = 0; i < 6; i++)
        {
            var r = await guard.InvokeAsync(MakeContext(i, "shell_run", args), CancellationToken.None);
            Assert.Equal("ran: ls", r?.ToString());
        }
    }

    [Fact]
    public async Task FifthIdenticalCall_SetsTerminate()
    {
        var guard = new AgentToolLoopGuard("agent-a", emitter: null);
        var args  = new Dictionary<string, object?> { ["cmd"] = "ls" };

        for (var i = 0; i < 4; i++)
            await guard.InvokeAsync(MakeContext(i, "shell_run", args), CancellationToken.None);
        var ctx5 = MakeContext(4, "shell_run", args);
        await guard.InvokeAsync(ctx5, CancellationToken.None);

        Assert.True(ctx5.Terminate);
    }

    [Fact]
    public async Task FirstFourIdenticalCalls_DoNotTerminate()
    {
        var guard = new AgentToolLoopGuard("agent-a", emitter: null);
        var args  = new Dictionary<string, object?> { ["cmd"] = "ls" };

        for (var i = 0; i < 4; i++)
        {
            var ctx = MakeContext(i, "shell_run", args);
            await guard.InvokeAsync(ctx, CancellationToken.None);
            Assert.False(ctx.Terminate);
        }
    }

    [Fact]
    public async Task SixthIdenticalCall_AlsoTerminates()
    {
        // >= HardThreshold, not ==, so the guard keeps stopping every subsequent round too
        // (relevant since orchestration has no separate stream-break to end the round loop).
        var guard = new AgentToolLoopGuard("agent-a", emitter: null);
        var args  = new Dictionary<string, object?> { ["cmd"] = "ls" };

        for (var i = 0; i < 5; i++)
            await guard.InvokeAsync(MakeContext(i, "shell_run", args), CancellationToken.None);
        var ctx6 = MakeContext(5, "shell_run", args);
        await guard.InvokeAsync(ctx6, CancellationToken.None);

        Assert.True(ctx6.Terminate);
    }

    [Fact]
    public async Task DifferentArguments_ResetTheStreak_NeverTerminates()
    {
        var guard = new AgentToolLoopGuard("agent-a", emitter: null);
        for (var i = 0; i < 6; i++)
        {
            var ctx = MakeContext(i, "shell_run", new() { ["cmd"] = $"cmd-{i}" });
            await guard.InvokeAsync(ctx, CancellationToken.None);
            Assert.False(ctx.Terminate);
        }
    }

    [Fact]
    public async Task DifferentToolName_ResetsTheStreakEvenWithSameArguments()
    {
        var guard = new AgentToolLoopGuard("agent-a", emitter: null);
        var args  = new Dictionary<string, object?> { ["cmd"] = "ls" };
        await guard.InvokeAsync(MakeContext(0, "shell_run", args), CancellationToken.None);
        await guard.InvokeAsync(MakeContext(1, "shell_run", args), CancellationToken.None);
        var ctx3 = MakeContext(2, "other_tool", args);
        await guard.InvokeAsync(ctx3, CancellationToken.None);

        Assert.False(ctx3.Terminate);
    }

    [Fact]
    public async Task IterationZero_ResetsStateForANewTurn()
    {
        var guard = new AgentToolLoopGuard("agent-a", emitter: null);
        var args  = new Dictionary<string, object?> { ["cmd"] = "ls" };

        // Two identical calls late in one turn (iterations 5, 6) — persisting state here would
        // let this guard's closure (reused across every turn of an AgentFactory-built agent for
        // the life of the run) mistake the next turn's first call for the 3rd of this streak.
        await guard.InvokeAsync(MakeContext(5, "shell_run", args), CancellationToken.None);
        await guard.InvokeAsync(MakeContext(6, "shell_run", args), CancellationToken.None);

        var ctx = MakeContext(0, "shell_run", args);
        await guard.InvokeAsync(ctx, CancellationToken.None);

        Assert.False(ctx.Terminate);
    }

    [Fact]
    public async Task StreakCanTerminateAgainAfterReset()
    {
        var guard = new AgentToolLoopGuard("agent-a", emitter: null);
        var args  = new Dictionary<string, object?> { ["cmd"] = "ls" };

        for (var i = 0; i < 5; i++)
            await guard.InvokeAsync(MakeContext(i, "shell_run", args), CancellationToken.None);

        // New turn (Iteration resets to 0) — a fresh streak must be able to reach the hard
        // threshold again rather than staying "stuck" terminated from the previous turn.
        for (var i = 0; i < 4; i++)
            await guard.InvokeAsync(MakeContext(i, "shell_run", args), CancellationToken.None);
        var ctx5 = MakeContext(4, "shell_run", args);
        await guard.InvokeAsync(ctx5, CancellationToken.None);

        Assert.True(ctx5.Terminate);
    }

    [Fact]
    public async Task NullEmitter_DoesNotThrow_AtEitherThreshold()
    {
        var guard = new AgentToolLoopGuard(null, emitter: null);
        var args  = new Dictionary<string, object?> { ["cmd"] = "ls" };

        for (var i = 0; i < 6; i++)
            await guard.InvokeAsync(MakeContext(i, "shell_run", args), CancellationToken.None);
        // No assertion needed beyond "didn't throw" — 6 calls walks through soft (3), the
        // between-tiers gap (4), and hard (5, 6) with a null agent name and null emitter.
    }

    [Fact]
    public async Task SoftThreshold_EmitsToolLoopWarningEvent_WithSoftKind()
    {
        var eventsPath = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        using var emitter = new EventEmitter(eventsPath);
        try
        {
            var guard = new AgentToolLoopGuard("agent-a", emitter);
            var args  = new Dictionary<string, object?> { ["cmd"] = "ls" };

            for (var i = 0; i < 3; i++)
                await guard.InvokeAsync(MakeContext(i, "shell_run", args), CancellationToken.None);

            var events = await File.ReadAllLinesAsync(eventsPath);
            var line = Assert.Single(events, l => l.Contains("\"tool_loop_warning\""));
            using var doc = JsonDocument.Parse(line);
            var payload = doc.RootElement.GetProperty("payload");
            Assert.Equal("agent-a", doc.RootElement.GetProperty("agent").GetString());
            Assert.Equal("soft", payload.GetProperty("kind").GetString());
            Assert.Equal("shell_run", payload.GetProperty("tool").GetString());
            Assert.Equal(3, payload.GetProperty("streak").GetInt32());
            Assert.Equal(3, payload.GetProperty("threshold").GetInt32());
        }
        finally
        {
            if (File.Exists(eventsPath)) File.Delete(eventsPath);
        }
    }

    [Fact]
    public async Task HardThreshold_EmitsToolLoopWarningEvent_WithHardKind()
    {
        var eventsPath = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        using var emitter = new EventEmitter(eventsPath);
        try
        {
            var guard = new AgentToolLoopGuard("agent-a", emitter);
            var args  = new Dictionary<string, object?> { ["cmd"] = "ls" };

            for (var i = 0; i < 5; i++)
                await guard.InvokeAsync(MakeContext(i, "shell_run", args), CancellationToken.None);

            var events = await File.ReadAllLinesAsync(eventsPath);
            var line = Assert.Single(events, l => l.Contains("\"kind\":\"hard\""));
            using var doc = JsonDocument.Parse(line);
            var payload = doc.RootElement.GetProperty("payload");
            Assert.Equal(5, payload.GetProperty("streak").GetInt32());
            Assert.Equal(5, payload.GetProperty("threshold").GetInt32());
        }
        finally
        {
            if (File.Exists(eventsPath)) File.Delete(eventsPath);
        }
    }
}
