using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary>
/// Covers /tools restrict and /tools unrestrict — the REPL's fine-grained per-plugin capability
/// gate, reusing PluginCapabilityMap.IsAllowed (the same enforcement function
/// AgentConfig.Capabilities is filtered through in orchestration) instead of REPL's own
/// whole-category /safe-mode / /tools disable toggles.
///
/// <see cref="Restrict_AppliesAcrossCategoryBuckets"/> is the key differentiator from
/// /safe-mode: filtering happens per-tool by PluginCapabilityMap.GetPlugin(toolName), not by
/// which ReplSessionContext.ToolsByCategory dictionary key currently holds the tool — so a
/// restricted plugin's tools sitting in the "Extended" bucket are covered too.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplToolsRestrictCommandTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");
    private readonly List<string> _eventsPaths = [];
    private readonly List<ReplSessionContext> _contexts = [];

    public ReplToolsRestrictCommandTests() =>
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

    // Mirrors the REPL's real shape: "Git" holds the curated Core git tools; "Extended" holds
    // the rest — including git_push, a Git-plugin tool that isn't in the "Git" dictionary key.
    private ReplSessionContext NewContext(string eventsPath)
    {
        var toolsByCategory = new Dictionary<string, List<AIFunction>>(StringComparer.OrdinalIgnoreCase)
        {
            ["FileSystem"] = [FakeTool("read_file"), FakeTool("write_file")],
            ["Git"]        = [FakeTool("git_status"), FakeTool("git_diff"), FakeTool("git_commit")],
            ["Extended"]   = [FakeTool("git_push"), FakeTool("delete_file")],
        };

        _eventsPaths.Add(eventsPath);
        var ctx = new ReplSessionContext(
            cwd: "/tmp", sessionId: "tools-restrict-session", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: null, client: new NoopChatClient(), factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(eventsPath),
            eventsPath: eventsPath,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(Path.GetTempPath(), $"fuseraft-test-mem-{Guid.NewGuid():N}")),
            toolsByCategory: toolsByCategory, systemPrompt: "test system prompt", pendingSave: false,
            adaptiveTrimTracker: new());
        ctx.JsonMode = true; // skip Ansi rendering paths — irrelevant to this test
        _contexts.Add(ctx);
        return ctx;
    }

    private static List<string> ActiveNames(ReplSessionContext ctx) =>
        [.. ctx.GetActiveTools().Select(f => f.Name)];

    [Fact]
    public void GetActiveTools_NoRestrictions_ReturnsEveryTool()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-none.jsonl"));

        var names = ActiveNames(ctx);

        Assert.Contains("git_commit", names);
        Assert.Contains("git_push", names);
        Assert.Contains("write_file", names);
        Assert.Equal(7, names.Count);
    }

    [Fact]
    public async Task Restrict_FiltersToolsByCapabilityTag()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-restrict.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/tools", "restrict Git read", CancellationToken.None);
        var names = ActiveNames(ctx);

        Assert.Contains("git_status", names);
        Assert.Contains("git_diff", names);
        Assert.DoesNotContain("git_commit", names);
    }

    [Fact]
    public async Task Restrict_AppliesAcrossCategoryBuckets()
    {
        // git_push lives in the "Extended" dictionary key, not "Git" — restricting the Git
        // *plugin* to read must still remove it, unlike a category-keyed disable would.
        var ctx = NewContext(Path.Combine(_tempHome, "events-cross-bucket.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/tools", "restrict Git read", CancellationToken.None);
        var names = ActiveNames(ctx);

        Assert.DoesNotContain("git_push", names);
        // delete_file is a FileSystem tool sitting in "Extended" too — unaffected by a Git-only restriction.
        Assert.Contains("delete_file", names);
    }

    [Fact]
    public async Task Restrict_DoesNotAffectOtherPlugins()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-other-plugins.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/tools", "restrict Git read", CancellationToken.None);
        var names = ActiveNames(ctx);

        Assert.Contains("read_file", names);
        Assert.Contains("write_file", names);
    }

    [Fact]
    public async Task RestrictThenUnrestrict_RestoresFullSet()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-unrestrict.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/tools", "restrict Git read", CancellationToken.None);
        Assert.DoesNotContain("git_commit", ActiveNames(ctx));

        await ReplCommands.HandleAsync(ctx, "/tools", "unrestrict Git", CancellationToken.None);
        Assert.Contains("git_commit", ActiveNames(ctx));
        Assert.Empty(ctx.CapabilityRestrictions);
    }

    [Fact]
    public async Task Restrict_MultipleTags_AllowsAnyOfThem()
    {
        // read+write covers every FileSystem tool in the fixture except delete_file (tagged
        // "delete"), which lives in the "Extended" bucket — proving both the multi-tag OR
        // parsing and the cross-bucket reach in one assertion.
        var ctx = NewContext(Path.Combine(_tempHome, "events-multi-tag.jsonl"));

        await ReplCommands.HandleAsync(ctx, "/tools", "restrict FileSystem read write", CancellationToken.None);
        var names = ActiveNames(ctx);

        Assert.Contains("read_file", names);
        Assert.Contains("write_file", names);
        Assert.DoesNotContain("delete_file", names);
    }

    [Fact]
    public async Task Restrict_UnknownPluginName_DoesNotThrowAndMatchesNothing()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-unknown-plugin.jsonl"));
        var before = ActiveNames(ctx);

        var result = await ReplCommands.HandleAsync(ctx, "/tools", "restrict NotAPlugin read", CancellationToken.None);

        Assert.Equal(CommandOutcome.Continue, result.Outcome);
        // Nothing in the fixture is tagged under "NotAPlugin", so every tool passes through.
        Assert.Equal(before.Count, ActiveNames(ctx).Count);
    }

    [Fact]
    public async Task Unrestrict_WithNoActiveRestriction_ReportsNothingToRemove()
    {
        var ctx = NewContext(Path.Combine(_tempHome, "events-unrestrict-noop.jsonl"));

        var result = await ReplCommands.HandleAsync(ctx, "/tools", "unrestrict Git", CancellationToken.None);

        Assert.Equal(CommandOutcome.Continue, result.Outcome);
        Assert.Empty(ctx.CapabilityRestrictions);
    }
}
