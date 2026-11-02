using System.Text.Json;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Core.Models.Session;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;
using Microsoft.Extensions.AI;

namespace FuseraftCli.Tests;

/// <summary>Tests for <c>/title</c>: the chat's name is saved with its snapshot, and clearing it falls back to the first message.</summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplTitleCommandTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");

    public ReplTitleCommandTests() =>
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
    }

    private static ReplSessionContext NewContext() => new(
        cwd: "/tmp", sessionId: "titled-session", startedAt: DateTime.UtcNow,
        modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
        userCfg: null, client: new StubChatClient(), factory: new ChatClientFactory(),
        keyStore: new UnavailableKeyStore(),
        emitter: new EventEmitter(Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl")),
        eventsPath: "unused", memoryStore: MemoryStore.CreateForTest(Path.Combine(Path.GetTempPath(), $"fuseraft-test-mem-{Guid.NewGuid():N}")),
        toolsByCategory: [], systemPrompt: "test system prompt", pendingSave: false,
        adaptiveTrimTracker: new()) { NoBanner = true };

    [Fact]
    public async Task Title_SetsAndSavesImmediately()
    {
        var ctx = NewContext();

        await ReplCommands.HandleAsync(ctx, "/title", "  Fix the   login\n bug ", CancellationToken.None);

        Assert.Equal("Fix the login bug", ctx.Title);
        var snap = await ReplSessionSnapshot.LoadAsync("titled-session");
        Assert.Equal("Fix the login bug", snap!.Title);
    }

    [Fact]
    public async Task Title_Clear_RemovesItFromTheSnapshot()
    {
        var ctx = NewContext();
        await ReplCommands.HandleAsync(ctx, "/title", "Named", CancellationToken.None);

        await ReplCommands.HandleAsync(ctx, "/title", "--clear", CancellationToken.None);

        Assert.Null(ctx.Title);
        var snap = await ReplSessionSnapshot.LoadAsync("titled-session");
        Assert.Null(snap!.Title);
        var json = await File.ReadAllTextAsync(Path.Combine(FuseraftPaths.GlobalReplSessions, "repl-titled-session.json"));
        Assert.DoesNotContain("\"Title\"", json);
    }

    [Fact]
    public async Task Title_WithNoArgument_LeavesTitleUnchanged()
    {
        var ctx = NewContext();
        ctx.Title = "Keep me";

        await ReplCommands.HandleAsync(ctx, "/title", "", CancellationToken.None);

        Assert.Equal("Keep me", ctx.Title);
    }

    [Fact]
    public async Task Fork_CarriesTheTitle()
    {
        var ctx = NewContext();
        ctx.Title = "Branch me";

        await ReplCommands.HandleAsync(ctx, "/fork", "", CancellationToken.None);

        var file = Assert.Single(Directory.GetFiles(FuseraftPaths.GlobalReplSessions, "repl-*.json"));
        var snap = await ReplSessionSnapshot.LoadAsync(Path.GetFileNameWithoutExtension(file)["repl-".Length..]);
        Assert.Equal("Branch me", snap!.Title);
    }

    // A client that keeps the session ID to resume the chat later (the VS Code panel across a reload) learns of a
    // switch from the title event, which names the session the chat now runs as.
    [Fact]
    public async Task ForkSwitch_InJsonMode_EmitsTitleWithTheForksSessionId()
    {
        var ctx = NewContext();
        ctx.JsonMode = true;
        ctx.Title = "Branch me";

        var events = await CaptureBridgeAsync(() => ReplCommands.HandleAsync(ctx, "/fork", "switch", CancellationToken.None));

        var title = Assert.Single(events, e => e.GetProperty("type").GetString() == "title");
        Assert.NotEqual("titled-session", ctx.SessionId);
        Assert.Equal(ctx.SessionId, title.GetProperty("sessionId").GetString());
        Assert.Equal("Branch me", title.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Switch_InJsonMode_EmitsTitleWithTheNewSessionId()
    {
        await ReplSessionSnapshot.SaveAsync(ReplSessionSnapshot.Capture(
            "other-session", "test-model", "/tmp", 1, [new ChatMessage(ChatRole.User, "hi")], DateTime.UtcNow, title: "Other chat"));
        var ctx = NewContext();
        ctx.JsonMode = true;

        var events = await CaptureBridgeAsync(() => ReplCommands.HandleAsync(ctx, "/switch", "other-session", CancellationToken.None));

        var title = Assert.Single(events, e => e.GetProperty("type").GetString() == "title");
        Assert.Equal("other-session", title.GetProperty("sessionId").GetString());
        Assert.Equal("Other chat", title.GetProperty("title").GetString());
    }

    private static async Task<List<JsonElement>> CaptureBridgeAsync(Func<Task> act)
    {
        var original = ReplJsonBridge.Writer;
        var writer = new StringWriter();
        ReplJsonBridge.Writer = writer;
        try { await act(); }
        finally { ReplJsonBridge.Writer = original; }
        return [.. writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())];
    }

    [Fact]
    public async Task Clear_DropsTheTitle()
    {
        var ctx = NewContext();
        ctx.Title = "Old conversation";

        await ReplCommands.HandleAsync(ctx, "/clear", "", CancellationToken.None);

        Assert.Null(ctx.Title);
    }

    [Fact]
    public void Snapshot_RoundTripsTitle()
    {
        var snap = ReplSessionSnapshot.Capture(
            "s", "m", "/tmp", 1, [new ChatMessage(ChatRole.User, "hi")], DateTime.UtcNow, title: "My chat");

        Assert.Equal("My chat", snap.Title);
    }

    [Fact]
    public async Task RunMidTurn_Title_AppliesAndIsSavedBeforeTheNextSave()
    {
        var ctx = NewContext();

        var message = ReplCommands.RunMidTurn(ctx, "/title Mid turn");

        Assert.Contains("Mid turn", message);
        Assert.Equal("Mid turn", ctx.Title);
        Assert.True(ctx.TitleUnsaved);
        Assert.False(File.Exists(Path.Combine(FuseraftPaths.GlobalReplSessions, "repl-titled-session.json")));

        await ReplTurn.SaveSnapshotAsync(ctx);

        Assert.False(ctx.TitleUnsaved);
        Assert.Equal("Mid turn", (await ReplSessionSnapshot.LoadAsync("titled-session"))!.Title);
    }

    [Theory]
    [InlineData("/hitl on", true)]
    [InlineData("/HITL  Off", false)]
    public void RunMidTurn_Hitl_TogglesTheSharedFlag(string line, bool expected)
    {
        var ctx = NewContext();
        ctx.HitlMode = !expected;
        ctx.Hitl.AutoApproveReadOnly = true;

        Assert.Contains("HITL mode", ReplCommands.RunMidTurn(ctx, line));
        Assert.Equal(expected, ctx.Hitl.Enabled);
        if (expected) Assert.False(ctx.Hitl.AutoApproveReadOnly);
    }

    [Theory]
    [InlineData("/compact")]
    [InlineData("/clear")]
    [InlineData("/titles x")]
    [InlineData("/hitl")]
    [InlineData("/hitl auto")]
    [InlineData("/hitl on now")]
    public void RunMidTurn_OtherCommandsWait(string line) =>
        Assert.Null(ReplCommands.RunMidTurn(NewContext(), line));

    [Theory]
    [InlineData("   ", null)]
    [InlineData("a\tb\r\nc", "a b c")]
    public void NormalizeTitle_CollapsesWhitespace(string input, string? expected) =>
        Assert.Equal(expected, ReplCommands.NormalizeTitle(input));

    private sealed class StubChatClient : IChatClient
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
