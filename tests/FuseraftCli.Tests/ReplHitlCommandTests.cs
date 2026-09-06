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
}
