using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Core.Models.Config;
using fuseraft.Core.Models.Session;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;
using Microsoft.Extensions.AI;

namespace FuseraftCli.Tests;

/// <summary>Tests for <see cref="ReplAutoTitle"/>: a chat is named once from its first exchange, and a title the user sets wins.</summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplAutoTitleTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");

    public ReplAutoTitleTests() =>
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
    }

    private static ReplSessionContext NewContext(UserConfig? userCfg = null, bool withExchange = true)
    {
        var ctx = new ReplSessionContext(
            cwd: "/tmp", sessionId: "auto-session", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: userCfg, client: new FakeClient("unused"), factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl")),
            eventsPath: "unused", memoryStore: MemoryStore.CreateForTest(Path.Combine(Path.GetTempPath(), $"fuseraft-test-mem-{Guid.NewGuid():N}")),
            toolsByCategory: [], systemPrompt: "test system prompt", pendingSave: false,
            adaptiveTrimTracker: new()) { NoBanner = true };
        if (withExchange)
        {
            ctx.History.Add(new ChatMessage(ChatRole.User, "Why does my Dockerfile rebuild every layer?"));
            ctx.History.Add(new ChatMessage(ChatRole.Assistant, "Because COPY . . comes before the dependency install."));
            ctx.TurnIndex = 1;
        }
        return ctx;
    }

    [Fact]
    public async Task NamesTheChatOnce_AndSavesItWhileTheReplWaits()
    {
        var ctx = NewContext();
        ctx.AwaitingInput = true;
        var client = new FakeClient("\"Docker layer caching.\"");

        await ReplAutoTitle.MaybeStart(ctx, () => client)!;

        Assert.Equal("Docker layer caching", ctx.AutoTitle);
        Assert.Contains("Dockerfile rebuild", client.LastPrompt);
        Assert.Contains("COPY . .", client.LastPrompt);
        Assert.Equal("Docker layer caching", (await ReplSessionSnapshot.LoadAsync("auto-session"))!.AutoTitle);
        Assert.Null(ReplAutoTitle.MaybeStart(ctx, () => client));
    }

    [Fact]
    public void WaitsForTheFirstReply() =>
        Assert.Null(ReplAutoTitle.MaybeStart(NewContext(withExchange: false), () => new FakeClient("x")));

    [Fact]
    public void SkipsAChatTheUserAlreadyNamed()
    {
        var ctx = NewContext();
        ctx.Title = "Mine";

        Assert.Null(ReplAutoTitle.MaybeStart(ctx, () => new FakeClient("x")));
    }

    [Fact]
    public void OffWithTheSetting() =>
        Assert.Null(ReplAutoTitle.MaybeStart(
            NewContext(new UserConfig { Repl = new ReplDefaultsConfig { AutoTitle = false } }), () => new FakeClient("x")));

    [Fact]
    public async Task DropsATitleForAConversationThatIsGone()
    {
        var ctx = NewContext();
        var client = new FakeClient("Docker layer caching") { Gate = new TaskCompletionSource() };

        var running = ReplAutoTitle.MaybeStart(ctx, () => client)!;
        await ReplCommands.HandleAsync(ctx, "/clear", "", CancellationToken.None);
        client.Gate.SetResult();
        await running;

        Assert.Null(ctx.AutoTitle);
    }

    [Fact]
    public async Task ClearingTheUsersTitle_FallsBackToTheGeneratedOne()
    {
        var ctx = NewContext();
        ctx.AutoTitle = "Docker layer caching";
        await ReplCommands.HandleAsync(ctx, "/title", "Mine", CancellationToken.None);

        Assert.Contains("generated title", ReplCommands.RunMidTurn(ctx, "/title --clear"));
        Assert.Null(ctx.Title);
        Assert.Equal("Docker layer caching", ctx.AutoTitle);
    }

    [Theory]
    [InlineData("Title: Docker layer caching", "Docker layer caching")]
    [InlineData("**Fixing the login bug**\n\nHope that helps!", "Fixing the login bug")]
    [InlineData("  `Rust lifetimes`.  ", "Rust lifetimes")]
    [InlineData("**Async streams.**", "Async streams")]
    [InlineData("", null)]
    [InlineData("This chat is about a user who wants to know why their Docker build keeps rebuilding", null)]
    [InlineData("Supercalifragilisticexpialidocious antidisestablishmentarianism pneumonoultramicroscopic", "Supercalifragilisticexpialidocious")]
    public void Clean_MakesAOneLineTitle(string raw, string? expected) =>
        Assert.Equal(expected, ReplAutoTitle.Clean(raw));

    [Fact]
    public void FirstExchange_SkipsTheSystemPromptAndToolOnlyReplies()
    {
        List<ChatMessage> history =
        [
            new(ChatRole.System, "sys"),
            new(ChatRole.User, "list the files"),
            new(ChatRole.Assistant, [new FunctionCallContent("1", "list_files")]),
            new(ChatRole.Tool, [new FunctionResultContent("1", "a.txt")]),
            new(ChatRole.Assistant, "There is one file, a.txt."),
        ];

        Assert.Equal(("list the files", "There is one file, a.txt."), ReplAutoTitle.FirstExchange(history));
    }

    private sealed class FakeClient(string reply) : IChatClient
    {
        public TaskCompletionSource? Gate { get; init; }
        public string LastPrompt { get; private set; } = "";

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastPrompt = string.Join("\n", messages.Select(m => m.Text));
            if (Gate is not null) await Gate.Task;
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, reply));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }
}
