using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary>
/// Covers the /temperature, /top-p, and /seed REPL commands, which mirror /max-tokens: a plain
/// field on <see cref="ReplSessionContext"/> that <see cref="ReplSessionContext.BuildChatOptions"/>
/// only projects onto <see cref="ChatOptions"/> when set, so a session with none of them
/// configured (and no active tools) still gets a null ChatOptions.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplSamplingParamsCommandTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");
    private readonly List<string> _eventsPaths = [];
    private readonly List<ReplSessionContext> _contexts = [];

    public ReplSamplingParamsCommandTests() =>
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

    private ReplSessionContext NewContext(string eventsPath)
    {
        _eventsPaths.Add(eventsPath);
        var ctx = new ReplSessionContext(
            cwd: "/tmp", sessionId: "sampling-command-session", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: null, client: new NoopChatClient(), factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(eventsPath),
            eventsPath: eventsPath,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(Path.GetTempPath(), $"fuseraft-test-mem-{Guid.NewGuid():N}")),
            toolsByCategory: [], systemPrompt: "test system prompt", pendingSave: false,
            adaptiveTrimTracker: new());
        ctx.JsonMode = true; // skip Ansi rendering paths — irrelevant to this test
        _contexts.Add(ctx);
        return ctx;
    }

    // /temperature

    [Fact]
    public async Task Temperature_SetsValueAndProjectsOntoChatOptions()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-temp-set.jsonl"));

        var result = await ReplCommands.HandleAsync(ctx, "/temperature", "0.7", CancellationToken.None);

        Assert.Equal(0.7, ctx.Temperature);
        Assert.Equal(0.7f, ctx.ChatOptions?.Temperature);
        Assert.Equal(CommandOutcome.Continue, result.Outcome);
    }

    [Fact]
    public async Task Temperature_Reset_ClearsValue()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-temp-reset.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/temperature", "1.2", CancellationToken.None);
        Assert.NotNull(ctx.Temperature);

        await ReplCommands.HandleAsync(ctx, "/temperature", "reset", CancellationToken.None);

        Assert.Null(ctx.Temperature);
        Assert.Null(ctx.ChatOptions);
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("2.1")]
    [InlineData("not-a-number")]
    public async Task Temperature_OutOfRangeOrInvalid_LeavesValueUnchanged(string arg)
    {
        var ctx = NewContext(Path.Combine(_tempHome, $"events-temp-invalid-{arg.GetHashCode()}.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/temperature", arg, CancellationToken.None);

        Assert.Null(ctx.Temperature);
    }

    // /top-p

    [Fact]
    public async Task TopP_SetsValueAndProjectsOntoChatOptions()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-topp-set.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/top-p", "0.9", CancellationToken.None);

        Assert.Equal(0.9, ctx.TopP);
        Assert.Equal(0.9f, ctx.ChatOptions?.TopP);
    }

    [Fact]
    public async Task TopP_Reset_ClearsValue()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-topp-reset.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/top-p", "0.5", CancellationToken.None);
        await ReplCommands.HandleAsync(ctx, "/top-p", "reset", CancellationToken.None);

        Assert.Null(ctx.TopP);
        Assert.Null(ctx.ChatOptions);
    }

    [Fact]
    public async Task TopP_OutOfRange_LeavesValueUnchanged()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-topp-invalid.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/top-p", "1.5", CancellationToken.None);

        Assert.Null(ctx.TopP);
    }

    // /seed

    [Fact]
    public async Task Seed_SetsValueAndProjectsOntoChatOptions()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-seed-set.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/seed", "42", CancellationToken.None);

        Assert.Equal(42L, ctx.Seed);
        Assert.Equal(42L, ctx.ChatOptions?.Seed);
    }

    [Fact]
    public async Task Seed_Reset_ClearsValue()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-seed-reset.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/seed", "7", CancellationToken.None);
        await ReplCommands.HandleAsync(ctx, "/seed", "reset", CancellationToken.None);

        Assert.Null(ctx.Seed);
        Assert.Null(ctx.ChatOptions);
    }

    [Fact]
    public async Task Seed_InvalidValue_LeavesValueUnchanged()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-seed-invalid.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/seed", "not-an-int", CancellationToken.None);

        Assert.Null(ctx.Seed);
    }
}
