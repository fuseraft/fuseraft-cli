using System.Text;
using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary>Covers the <c>memory.autoSave</c> gate on <see cref="ReplTurn.SaveMemoriesBeforeDiscardAsync"/>.</summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplMemoryAutoSaveTests : IAsyncDisposable
{
    private const string Completion =
        """{"id":"x","object":"chat.completion","created":1,"model":"test-model","choices":[{"index":0,"message":{"role":"assistant","content":"[]"},"finish_reason":"stop"}]}""";

    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");
    private readonly LoopbackHttpServer _server = new(async (_, res) =>
    {
        res.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(Completion);
        await res.OutputStream.WriteAsync(bytes);
    });
    private ReplSessionContext? _ctx;

    public ReplMemoryAutoSaveTests() =>
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _ctx?.Emitter.Dispose();
        _ctx?.Factory.Dispose();
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
    }

    private ReplSessionContext NewContext(bool? autoSave)
    {
        var eventsPath = Path.Combine(_tempHome, "events.jsonl");
        Directory.CreateDirectory(_tempHome);
        _ctx = new ReplSessionContext(
            cwd: _tempHome, sessionId: "autosave-session", startedAt: DateTime.UtcNow,
            modelId: "test-model",
            modelConfig: new() { ModelId = "test-model", Provider = "openai", Endpoint = $"{_server.BaseUrl}/v1", ApiKey = "test-key" },
            userCfg: new UserConfig { Memory = new MemoryExtractionConfig { AutoSave = autoSave } },
            client: null!, factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(eventsPath),
            eventsPath: eventsPath,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(_tempHome, "mem")),
            toolsByCategory: [], systemPrompt: "test", pendingSave: false,
            adaptiveTrimTracker: new());
        _ctx.JsonMode = true;
        _ctx.History.Add(new ChatMessage(ChatRole.User, "Always use ./build.sh, never dotnet build."));
        _ctx.History.Add(new ChatMessage(ChatRole.Assistant, "Understood."));
        _ctx.TurnIndex = 1;
        return _ctx;
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task AutoSaveOffOrUnset_MakesNoExtractionCall(bool? autoSave)
    {
        var ctx = NewContext(autoSave);

        await ReplTurn.SaveMemoriesBeforeDiscardAsync(ctx);

        Assert.Empty(_server.Requests);
        Assert.True(ReplTurn.HasUnextractedTurns(ctx));
    }

    [Fact]
    public async Task AutoSaveOn_ExtractsTheUnseenTurns()
    {
        var ctx = NewContext(autoSave: true);

        await ReplTurn.SaveMemoriesBeforeDiscardAsync(ctx);

        Assert.Single(_server.Requests);
        Assert.False(ReplTurn.HasUnextractedTurns(ctx));
    }
}
