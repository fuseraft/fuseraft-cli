using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary>
/// Covers /safe-mode: blocks Shell/Git/Http by owning plugin (PluginCapabilityMap.GetPlugin),
/// not only by ToolsByCategory dictionary key. Regression for the Extended-bucket gap where
/// git_push / shell_run_background lived under "Extended" and survived category-key disable.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplSafeModeCommandTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");
    private readonly List<string> _eventsPaths = [];
    private readonly List<ReplSessionContext> _contexts = [];

    public ReplSafeModeCommandTests() =>
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

    private static AIFunction FakeTool(string name) =>
        AIFunctionFactory.Create(() => "ok", name, $"Fake tool standing in for {name}.");

    // Mirrors the REPL's real shape with --plugins Extended: Core buckets hold curated tools;
    // Extended holds the rest, including Shell/Git tools that category-key disable alone would miss.
    private ReplSessionContext NewContextWithExtended(string eventsPath)
    {
        var toolsByCategory = new Dictionary<string, List<AIFunction>>(StringComparer.OrdinalIgnoreCase)
        {
            ["FileSystem"] = [FakeTool("read_file"), FakeTool("write_file")],
            ["Shell"]      = [FakeTool("shell_run"), FakeTool("shell_get_env")],
            ["Git"]        = [FakeTool("git_status"), FakeTool("git_commit")],
            ["Http"]       = [FakeTool("http_get"), FakeTool("http_post")],
            ["Extended"]   =
            [
                FakeTool("git_push"),
                FakeTool("git_reset"),
                FakeTool("shell_run_background"),
                FakeTool("delete_file"),
            ],
        };

        return BuildContext(eventsPath, toolsByCategory);
    }

    // Core-only shape (no Extended plugin) - safe-mode must keep its prior behavior.
    private ReplSessionContext NewContextCoreOnly(string eventsPath)
    {
        var toolsByCategory = new Dictionary<string, List<AIFunction>>(StringComparer.OrdinalIgnoreCase)
        {
            ["FileSystem"] = [FakeTool("read_file"), FakeTool("write_file")],
            ["Shell"]      = [FakeTool("shell_run"), FakeTool("shell_get_env")],
            ["Git"]        = [FakeTool("git_status"), FakeTool("git_commit")],
            ["Http"]       = [FakeTool("http_get")],
        };

        return BuildContext(eventsPath, toolsByCategory);
    }

    private ReplSessionContext BuildContext(
        string eventsPath, Dictionary<string, List<AIFunction>> toolsByCategory)
    {
        _eventsPaths.Add(eventsPath);
        var ctx = new ReplSessionContext(
            cwd: "/tmp", sessionId: "safe-mode-session", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: null, client: new NoopChatClient(), factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(eventsPath),
            eventsPath: eventsPath,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(Path.GetTempPath(), $"fuseraft-test-mem-{Guid.NewGuid():N}")),
            toolsByCategory: toolsByCategory, systemPrompt: "test system prompt", pendingSave: false,
            adaptiveTrimTracker: new());
        ctx.JsonMode = true;
        _contexts.Add(ctx);
        return ctx;
    }

    private static List<string> ActiveNames(ReplSessionContext ctx) =>
        [.. ctx.GetActiveTools().Select(f => f.Name)];

    [Fact]
    public async Task SafeModeOn_BlocksCoreShellGitHttpCategories()
    {
        var ctx = NewContextCoreOnly(Path.Combine(_tempHome, "events-core-on.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/safe-mode", "on", CancellationToken.None);
        var names = ActiveNames(ctx);

        Assert.True(ctx.SafeMode);
        Assert.DoesNotContain("shell_run", names);
        Assert.DoesNotContain("git_commit", names);
        Assert.DoesNotContain("http_get", names);
        // FileSystem is never a safe-mode target.
        Assert.Contains("read_file", names);
        Assert.Contains("write_file", names);
    }

    [Fact]
    public async Task SafeModeOn_BlocksExtendedBucketShellAndGitTools()
    {
        // The regression: git_push / shell_run_background live under "Extended", not
        // "Git"/"Shell", so category-key disable alone left them callable.
        var ctx = NewContextWithExtended(Path.Combine(_tempHome, "events-extended-on.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/safe-mode", "on", CancellationToken.None);
        var names = ActiveNames(ctx);

        Assert.DoesNotContain("git_push", names);
        Assert.DoesNotContain("git_reset", names);
        Assert.DoesNotContain("shell_run_background", names);
        // FileSystem-owned tool in Extended is untouched.
        Assert.Contains("delete_file", names);
        Assert.Contains("read_file", names);
        Assert.Contains("write_file", names);
    }

    [Fact]
    public async Task SafeModeOff_RestoresExtendedTools()
    {
        var ctx = NewContextWithExtended(Path.Combine(_tempHome, "events-extended-off.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/safe-mode", "on", CancellationToken.None);
        Assert.DoesNotContain("git_push", ActiveNames(ctx));

        await ReplCommands.HandleAsync(ctx, "/safe-mode", "off", CancellationToken.None);
        var names = ActiveNames(ctx);

        Assert.False(ctx.SafeMode);
        Assert.Contains("git_push", names);
        Assert.Contains("shell_run_background", names);
        Assert.Contains("shell_run", names);
        Assert.Contains("git_commit", names);
    }

    [Fact]
    public async Task SafeModeOff_PreservesPriorCapabilityRestriction()
    {
        // A prior /tools restrict must not be wiped by safe-mode; turning safe mode off
        // should leave the restriction in effect (not restore full Git write access).
        var ctx = NewContextWithExtended(Path.Combine(_tempHome, "events-prior-restrict.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/tools", "restrict Git read", CancellationToken.None);
        Assert.DoesNotContain("git_commit", ActiveNames(ctx));
        Assert.DoesNotContain("git_push", ActiveNames(ctx));
        Assert.Contains("git_status", ActiveNames(ctx));

        await ReplCommands.HandleAsync(ctx, "/safe-mode", "on", CancellationToken.None);
        Assert.DoesNotContain("git_status", ActiveNames(ctx)); // safe-mode blocks all Git

        await ReplCommands.HandleAsync(ctx, "/safe-mode", "off", CancellationToken.None);
        var names = ActiveNames(ctx);

        // Restriction restored/preserved: read-only Git still holds.
        Assert.Contains("git_status", names);
        Assert.DoesNotContain("git_commit", names);
        Assert.DoesNotContain("git_push", names);
        Assert.True(ctx.CapabilityRestrictions.ContainsKey("Git"));
    }

    [Fact]
    public async Task SafeModeOff_RestoresPriorCategoryDisable()
    {
        var ctx = NewContextCoreOnly(Path.Combine(_tempHome, "events-prior-disable.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/tools", "disable Shell", CancellationToken.None);
        Assert.DoesNotContain("shell_run", ActiveNames(ctx));
        Assert.Contains("git_status", ActiveNames(ctx));

        await ReplCommands.HandleAsync(ctx, "/safe-mode", "on", CancellationToken.None);
        await ReplCommands.HandleAsync(ctx, "/safe-mode", "off", CancellationToken.None);

        var names = ActiveNames(ctx);
        // Pre-safe Shell disable is restored; Git (which safe-mode had disabled) comes back.
        Assert.DoesNotContain("shell_run", names);
        Assert.Contains("git_status", names);
        Assert.Contains("http_get", names);
    }

    [Fact]
    public async Task SafeModeOn_AlreadyOn_IsNoOp()
    {
        var ctx = NewContextCoreOnly(Path.Combine(_tempHome, "events-already-on.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/safe-mode", "on", CancellationToken.None);
        var countAfterFirst = ActiveNames(ctx).Count;

        var result = await ReplCommands.HandleAsync(ctx, "/safe-mode", "on", CancellationToken.None);

        Assert.Equal(CommandOutcome.Continue, result.Outcome);
        Assert.True(ctx.SafeMode);
        Assert.Equal(countAfterFirst, ActiveNames(ctx).Count);
    }

    [Fact]
    public void PassesSafeMode_WhenOff_AllowsEverything()
    {
        var ctx = NewContextWithExtended(Path.Combine(_tempHome, "events-pass-off.jsonl"));

        Assert.True(ctx.PassesSafeMode("git_push"));
        Assert.True(ctx.PassesSafeMode("shell_run_background"));
        Assert.True(ctx.PassesSafeMode("delete_file"));
        Assert.True(ctx.PassesSafeMode("read_file"));
    }
}
