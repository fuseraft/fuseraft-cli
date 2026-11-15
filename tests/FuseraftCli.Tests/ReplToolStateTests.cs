using System.Text.Json;
using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;
using fuseraft.Infrastructure.Storage;

namespace FuseraftCli.Tests;

/// <summary>Tests for which chat tools are on: saved defaults, /tools enable|disable|save|reset, and the panel's tools event.</summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplToolStateTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");
    private readonly List<ReplSessionContext> _contexts = [];

    public ReplToolStateTests() =>
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        foreach (var ctx in _contexts) { ctx.Emitter.Dispose(); ctx.Factory.Dispose(); }
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
    }

    private static AIFunction Tool(string name) => AIFunctionFactory.Create(() => "ok", name, $"Does {name}. More detail.");

    private static Dictionary<string, List<AIFunction>> Tools() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["FileSystem"] = [Tool("read_file"), Tool("write_file")],
        ["Shell"]      = [Tool("shell_run")],
        ["Git"]        = [Tool("git_status"), Tool("git_commit")],
        ["Http"]       = [Tool("http_get")],
        ["Extended"]   = [Tool("git_push"), Tool("delete_file")],
        ["mcp:docs"]   = [Tool("search_docs")],
    };

    private ReplSessionContext NewContext(ReplDefaultsConfig? repl = null, IReadOnlySet<string>? flagPlugins = null)
    {
        var ctx = new ReplSessionContext(
            cwd: "/tmp", sessionId: "tools-session", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: new UserConfig { Repl = repl ?? new ReplDefaultsConfig() }, client: new NoopChatClient(), factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(Path.Combine(_tempHome, $"events-{Guid.NewGuid():N}.jsonl")),
            eventsPath: "unused",
            memoryStore: MemoryStore.CreateForTest(Path.Combine(_tempHome, $"mem-{Guid.NewGuid():N}")),
            toolsByCategory: Tools(), systemPrompt: "sys", pendingSave: false,
            adaptiveTrimTracker: new())
        {
            FlagPlugins = flagPlugins ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };
        ReplToolState.ApplyDefaults(ctx, repl);
        _contexts.Add(ctx);
        return ctx;
    }

    private static List<string> Active(ReplSessionContext ctx) => [.. ctx.GetActiveTools().Select(t => t.Name)];

    private static void SaveConfig(ReplDefaultsConfig repl) =>
        UserConfigStore.Save(new UserConfig { ModelId = "test-model", Repl = repl });

    [Fact]
    public void Defaults_LeaveOptionalPluginsOff_UnlessConfigOrFlagTurnsThemOn()
    {
        Assert.DoesNotContain("http_get", Active(NewContext()));
        Assert.DoesNotContain("git_push", Active(NewContext()));
        Assert.Contains("http_get", Active(NewContext(new ReplDefaultsConfig { Plugins = ["Http"] })));
        Assert.Contains("git_push", Active(NewContext(flagPlugins: new HashSet<string>(["extended"], StringComparer.OrdinalIgnoreCase))));
    }

    [Fact]
    public void Defaults_DisabledTools_TakesCategoriesAndSingleTools_AndIgnoresUnknownNames()
    {
        var active = Active(NewContext(new ReplDefaultsConfig { DisabledTools = ["shell", "git_commit", "mcp:docs", "no_such_tool"] }));

        Assert.DoesNotContain("shell_run", active);
        Assert.DoesNotContain("git_commit", active);
        Assert.DoesNotContain("search_docs", active);
        Assert.Contains("git_status", active);
        Assert.Contains("read_file", active);
    }

    [Fact]
    public async Task DisableAndEnable_ASingleTool()
    {
        var ctx = NewContext();

        await ReplCommands.HandleAsync(ctx, "/tools", "disable GIT_COMMIT", CancellationToken.None);
        Assert.DoesNotContain("git_commit", Active(ctx));
        Assert.Contains("git_status", Active(ctx));
        Assert.DoesNotContain(ctx.ChatOptions!.Tools!, t => t.Name == "git_commit");

        await ReplCommands.HandleAsync(ctx, "/tools", "enable git_commit", CancellationToken.None);
        Assert.Contains("git_commit", Active(ctx));
    }

    [Fact]
    public async Task EnablingAnOptionalPlugin_MidChat_AddsItsTools()
    {
        var ctx = NewContext();

        await ReplCommands.HandleAsync(ctx, "/tools", "enable Http", CancellationToken.None);

        Assert.Contains("http_get", Active(ctx));
    }

    [Fact]
    public async Task DisablingUnderSafeMode_StillHoldsAfterSafeModeEnds()
    {
        var ctx = NewContext();
        await ReplCommands.HandleAsync(ctx, "/safe-mode", "on", CancellationToken.None);

        await ReplCommands.HandleAsync(ctx, "/tools", "disable FileSystem", CancellationToken.None);
        await ReplCommands.HandleAsync(ctx, "/safe-mode", "off", CancellationToken.None);

        Assert.DoesNotContain("read_file", Active(ctx));
        Assert.Contains("shell_run", Active(ctx));
    }

    [Fact]
    public async Task Save_WritesTheChatsToolsAsDefaults_AndKeepsEntriesForToolsThisChatLacks()
    {
        SaveConfig(new ReplDefaultsConfig { DisabledTools = ["mcp:other"], Plugins = ["Scratchpad"] });
        var ctx = NewContext(UserConfigStore.Load().Config!.Repl);
        await ReplCommands.HandleAsync(ctx, "/tools", "disable Shell", CancellationToken.None);
        await ReplCommands.HandleAsync(ctx, "/tools", "disable git_commit", CancellationToken.None);
        await ReplCommands.HandleAsync(ctx, "/tools", "enable Http", CancellationToken.None);

        await ReplCommands.HandleAsync(ctx, "/tools", "save", CancellationToken.None);

        var saved = UserConfigStore.Load().Config!.Repl;
        Assert.Equal(["Scratchpad", "Http"], saved.Plugins);
        Assert.Equal(["mcp:other", "Shell", "git_commit"], saved.DisabledTools);
        Assert.True(ReplToolState.MatchesDefaults(ctx, saved));
        Assert.Equal("test-model", UserConfigStore.Load().Config!.ModelId);
    }

    [Fact]
    public async Task Reset_GoesBackToTheSavedDefaults()
    {
        SaveConfig(new ReplDefaultsConfig { DisabledTools = ["git_commit"] });
        var ctx = NewContext(UserConfigStore.Load().Config!.Repl);
        await ReplCommands.HandleAsync(ctx, "/tools", "enable git_commit", CancellationToken.None);
        await ReplCommands.HandleAsync(ctx, "/tools", "disable FileSystem", CancellationToken.None);
        Assert.False(ReplToolState.MatchesDefaults(ctx, UserConfigStore.Load().Config!.Repl));

        await ReplCommands.HandleAsync(ctx, "/tools", "reset", CancellationToken.None);

        Assert.DoesNotContain("git_commit", Active(ctx));
        Assert.Contains("read_file", Active(ctx));
        Assert.True(ReplToolState.MatchesDefaults(ctx, UserConfigStore.Load().Config!.Repl));
    }

    [Fact]
    public async Task ToolsEvent_ListsEachCategoryAndToolWithItsState()
    {
        var ctx = NewContext();
        ctx.JsonMode = true;
        var original = ReplJsonBridge.Writer;
        var writer = new StringWriter();
        ReplJsonBridge.Writer = writer;
        try { await ReplCommands.HandleAsync(ctx, "/tools", "disable git_commit", CancellationToken.None); }
        finally { ReplJsonBridge.Writer = original; }

        var evt = JsonDocument.Parse(writer.ToString().Trim().Split('\n')[^1]).RootElement;
        Assert.Equal("tools", evt.GetProperty("type").GetString());
        Assert.False(evt.GetProperty("matchesDefaults").GetBoolean());
        var categories = evt.GetProperty("categories").EnumerateArray().ToList();
        Assert.Equal(["FileSystem", "Shell", "Git", "Extended", "Http", "mcp:docs"], categories.Select(c => c.GetProperty("name").GetString()));
        var git = categories.Single(c => c.GetProperty("name").GetString() == "Git");
        Assert.True(git.GetProperty("enabled").GetBoolean());
        var commit = git.GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "git_commit");
        Assert.False(commit.GetProperty("enabled").GetBoolean());
        Assert.Equal("Does git_commit.", commit.GetProperty("description").GetString());
        var http = categories.Single(c => c.GetProperty("name").GetString() == "Http");
        Assert.True(http.GetProperty("optional").GetBoolean());
        Assert.False(http.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task UnknownName_ChangesNothing()
    {
        var ctx = NewContext();
        var before = Active(ctx);

        await ReplCommands.HandleAsync(ctx, "/tools", "disable nope", CancellationToken.None);

        Assert.Equal(before, Active(ctx));
    }

    private sealed class NoopChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();
        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }
}
