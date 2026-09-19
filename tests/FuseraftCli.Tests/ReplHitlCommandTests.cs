using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary>
/// Covers the REPL-side half of the /hitl wiring: the command handler flips
/// <see cref="ReplSessionContext.HitlMode"/> (backed by the shared <see cref="HitlModeState"/>
/// object), independent of whether a real ShellPlugin approver is attached. The other half —
/// that ShellPlugin actually honors an approver callback — is covered by ShellPluginTests'
/// approveCommand tests; the two together cover the same path ReplCommand.cs wires at startup.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplHitlCommandTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");
    private readonly List<string> _eventsPaths = [];
    private readonly List<ReplSessionContext> _contexts = [];

    public ReplHitlCommandTests() =>
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
        foreach (var path in _eventsPaths)
            if (File.Exists(path)) File.Delete(path);
    }

    private sealed class NoopChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("test", null!, "stub");
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();
        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private ReplSessionContext NewContext(string eventsPath, HitlModeState? hitlState = null)
    {
        _eventsPaths.Add(eventsPath);
        var ctx = new ReplSessionContext(
            cwd: "/tmp", sessionId: "hitl-command-session", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: null, client: new NoopChatClient(), factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(eventsPath),
            eventsPath: eventsPath,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(Path.GetTempPath(), $"fuseraft-test-mem-{Guid.NewGuid():N}")),
            toolsByCategory: [], systemPrompt: "test system prompt", pendingSave: false,
            adaptiveTrimTracker: new(), hitlState: hitlState);
        ctx.JsonMode = true; // skip Ansi rendering paths — irrelevant to this test
        _contexts.Add(ctx);
        return ctx;
    }

    [Fact]
    public void HitlMode_DefaultsToOff()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-default.jsonl"));
        Assert.False(ctx.HitlMode);
    }

    [Fact]
    public async Task HitlOn_SetsHitlModeTrue()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-on.jsonl"));

        var result = await ReplCommands.HandleAsync(ctx, "/hitl", "on", CancellationToken.None);

        Assert.True(ctx.HitlMode);
        Assert.Equal(CommandOutcome.Continue, result.Outcome);
    }

    [Fact]
    public async Task HitlOnThenOff_RestoresHitlModeFalse()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-toggle.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/hitl", "on", CancellationToken.None);
        Assert.True(ctx.HitlMode);

        await ReplCommands.HandleAsync(ctx, "/hitl", "off", CancellationToken.None);
        Assert.False(ctx.HitlMode);
    }

    [Fact]
    public async Task HitlOn_UnknownArgument_LeavesHitlModeUnchanged()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-bad-arg.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/hitl", "sideways", CancellationToken.None);

        Assert.False(ctx.HitlMode);
    }

    [Fact]
    public async Task HitlOn_SharedHitlModeState_IsVisibleToExternalHolder()
    {
        // Mirrors ReplCommand.cs's real wiring: the same HitlModeState instance handed to the
        // ShellPlugin approver closure at startup is handed to ReplSessionContext here, so
        // toggling ctx.HitlMode via /hitl must be observable through that external reference —
        // this is the exact mechanism the ShellPlugin closure reads on every shell_run call.
        var sharedState = new HitlModeState();
        var ctx = NewContext(Path.Combine(_tempHome, "events-shared.jsonl"), sharedState);

        Assert.False(sharedState.Enabled);
        await ReplCommands.HandleAsync(ctx, "/hitl", "on", CancellationToken.None);
        Assert.True(sharedState.Enabled);

        await ReplCommands.HandleAsync(ctx, "/hitl", "off", CancellationToken.None);
        Assert.False(sharedState.Enabled);
    }

    // /hitl auto — read-only shell commands skip the prompt

    [Fact]
    public async Task HitlAuto_TurnsHitlOnAndEnablesReadOnlyAutoApproval()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-auto.jsonl"));

        var result = await ReplCommands.HandleAsync(ctx, "/hitl", "auto", CancellationToken.None);

        Assert.True(ctx.HitlMode);
        Assert.True(ctx.Hitl.AutoApproveReadOnly);
        Assert.Equal(CommandOutcome.Continue, result.Outcome);
    }

    [Fact]
    public async Task HitlAuto_WhileHitlIsAlreadyOn_SwitchesToAutoWithoutTurningItOffAndOn()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-on-then-auto.jsonl"));
        await ReplCommands.HandleAsync(ctx, "/hitl", "on", CancellationToken.None);

        await ReplCommands.HandleAsync(ctx, "/hitl", "auto", CancellationToken.None);

        Assert.True(ctx.HitlMode);
        Assert.True(ctx.Hitl.AutoApproveReadOnly);
    }

    [Fact]
    public async Task HitlOn_AfterAuto_WithdrawsTheAutoApprovalButKeepsHitlOn()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-auto-then-on.jsonl"));
        await ReplCommands.HandleAsync(ctx, "/hitl", "auto", CancellationToken.None);

        await ReplCommands.HandleAsync(ctx, "/hitl", "on", CancellationToken.None);

        Assert.True(ctx.HitlMode);
        Assert.False(ctx.Hitl.AutoApproveReadOnly);
    }

    [Fact]
    public async Task HitlOff_AfterAuto_TurnsHitlOff_AndAnAutoFlagCannotRelaxAnythingWhileItIsOff()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-auto-off.jsonl"));
        await ReplCommands.HandleAsync(ctx, "/hitl", "auto", CancellationToken.None);

        await ReplCommands.HandleAsync(ctx, "/hitl", "off", CancellationToken.None);

        Assert.False(ctx.HitlMode);
        Assert.False(ctx.Hitl.RequiresShellApproval("rm -rf build"));   // off means no prompts at all — unchanged
    }

    [Fact]
    public async Task HitlAuto_Twice_IsANoOpAndStaysAuto()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-auto-twice.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/hitl", "auto", CancellationToken.None);
        await ReplCommands.HandleAsync(ctx, "/hitl", "auto", CancellationToken.None);

        Assert.True(ctx.HitlMode);
        Assert.True(ctx.Hitl.AutoApproveReadOnly);
    }

    [Fact]
    public async Task HitlAuto_IsRecordedInTheEventLog()
    {
        var path = Path.Combine(_tempHome, "events-auto-logged.jsonl");
        var ctx = NewContext(path);

        await ReplCommands.HandleAsync(ctx, "/hitl", "auto", CancellationToken.None);

        Assert.Contains(await File.ReadAllLinesAsync(path), l => l.Contains("/hitl auto"));
    }

    [Fact]
    public async Task HitlStatus_WithNoArgument_DoesNotChangeAnything()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-status.jsonl"));
        await ReplCommands.HandleAsync(ctx, "/hitl", "auto", CancellationToken.None);

        await ReplCommands.HandleAsync(ctx, "/hitl", "", CancellationToken.None);

        Assert.True(ctx.HitlMode);
        Assert.True(ctx.Hitl.AutoApproveReadOnly);
    }

    [Fact]
    public void AutoApproveReadOnly_SharedStateAcrossTheApproverClosureAndTheCommand()
    {
        // ReplCommand.cs hands ONE HitlModeState to the ShellPlugin approver closure and to the
        // session context; /hitl auto must be visible to the closure that was built earlier.
        var state = new HitlModeState { Enabled = true };
        var ctx = NewContext(Path.Combine(_tempHome, "events-shared.jsonl"), state);
        Func<string, bool> closure = cmd => state.RequiresShellApproval(cmd);

        Assert.True(closure("ls"));                       // strict: everything asks
        ctx.Hitl.AutoApproveReadOnly = true;
        Assert.False(closure("ls"));                      // now read-only runs
        Assert.True(closure("rm -rf build"));             // anything that can change something still asks
    }

    // RequiresShellApproval — the decision the REPL's approver closure makes for each shell command

    [Theory]
    [InlineData(true,  false, "ls",              true)]    // HITL on, strict: everything asks
    [InlineData(true,  false, "git status",      true)]
    [InlineData(true,  false, "rm x",            true)]
    [InlineData(true,  true,  "ls",              false)]   // HITL on, auto: read-only runs...
    [InlineData(true,  true,  "git status",      false)]
    [InlineData(true,  true,  "git log | head",  false)]
    [InlineData(true,  true,  "rm x",            true)]    // ...and everything else still asks
    [InlineData(true,  true,  "git push",        true)]
    [InlineData(true,  true,  "ls > out.txt",    true)]
    [InlineData(true,  true,  "ls; rm x",        true)]
    [InlineData(true,  true,  "echo \"$(rm x)\"", true)]
    [InlineData(true,  true,  "PATH=./e:$PATH ls", true)]
    [InlineData(true,  true,  "./ls",            true)]
    [InlineData(true,  true,  "",                true)]
    [InlineData(false, false, "rm x",            false)]   // HITL off: nothing asks, auto flag irrelevant
    [InlineData(false, true,  "rm x",            false)]
    public void RequiresShellApproval_Matrix(bool enabled, bool auto, string command, bool expected)
    {
        var state = new HitlModeState { Enabled = enabled, AutoApproveReadOnly = auto };

        Assert.Equal(expected, state.RequiresShellApproval(command));
    }

    // End to end through a real ShellPlugin, with the same closure shape ReplCommand.cs builds

    [Fact]
    public async Task ShellPlugin_InAutoMode_RunsReadOnlyCommandsWithoutPrompting_AndPromptsForTheRest()
    {
        var state = new HitlModeState { Enabled = true, AutoApproveReadOnly = true };
        var prompted = new List<string>();
        using var plugin = new fuseraft.Infrastructure.Plugins.ShellPlugin(approveCommand: cmd =>
        {
            if (!state.RequiresShellApproval(cmd)) return Task.FromResult(true);
            prompted.Add(cmd);
            return Task.FromResult(false);   // the user says no
        });
        var marker = Path.Combine(Path.GetTempPath(), $"fuseraft-hitl-auto-{Guid.NewGuid():N}");

        var readOnly = await plugin.RunAsync("echo read-only-ran");
        var writer   = await plugin.RunAsync($"mkdir {marker}");

        Assert.Contains("read-only-ran", readOnly);
        Assert.Equal([$"mkdir {marker}"], prompted);                 // only the writer reached the user
        Assert.StartsWith("[DENIED]", writer);
        Assert.False(Directory.Exists(marker), "a declined command must not have run");
    }

    [Fact]
    public async Task ShellPlugin_InStrictMode_PromptsForEvenAReadOnlyCommand()
    {
        var state = new HitlModeState { Enabled = true };
        var prompted = new List<string>();
        using var plugin = new fuseraft.Infrastructure.Plugins.ShellPlugin(approveCommand: cmd =>
        {
            if (!state.RequiresShellApproval(cmd)) return Task.FromResult(true);
            prompted.Add(cmd);
            return Task.FromResult(true);
        });

        await plugin.RunAsync("echo hi");

        Assert.Equal(["echo hi"], prompted);
    }

    [Fact]
    public async Task ShellPlugin_HardDenialsStillHappenBeforeAutoApproval()
    {
        // A read-only command that names a credentials file is stopped by the guard, never
        // reaching approval — auto mode can't be used to read a key.
        var state = new HitlModeState { Enabled = true, AutoApproveReadOnly = true };
        var approverCalled = false;
        using var plugin = new fuseraft.Infrastructure.Plugins.ShellPlugin(approveCommand: cmd =>
        {
            approverCalled = true;
            return Task.FromResult(!state.RequiresShellApproval(cmd));
        });

        var result = await plugin.RunAsync("cat /tmp/fuseraft-test-nonexistent/id_rsa");

        Assert.StartsWith("[DENIED]", result);
        Assert.Contains("credentials file", result);
        Assert.False(approverCalled);
    }
}
