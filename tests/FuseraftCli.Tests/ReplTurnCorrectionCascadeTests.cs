using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// The post-turn corrections in <see cref="ReplTurn"/> (mutation claimed without a write tool,
/// critic rejection, todo list still open) run at most one per turn. Each one that fires runs a
/// whole new turn that makes its own checks, so the turn that fired it must not go on to run its
/// remaining checks against its own, now stale, response — that used to restart the todo-nudge
/// chain from round 0 and run it a second time. Also which todo lists get nudged at all: only
/// one written during the current request, and never after <c>/clear</c>.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplTurnCorrectionCascadeTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");
    private readonly List<string> _eventsPaths = [];
    private readonly List<ReplSessionContext> _contexts = [];

    public ReplTurnCorrectionCascadeTests() =>
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
        foreach (var ctx in _contexts) { ctx.Emitter.Dispose(); ctx.Factory.Dispose(); }
        foreach (var path in _eventsPaths) if (File.Exists(path)) File.Delete(path);
    }

    // Every turn answers with the same text and calls no tool. duringFirstTurn stands in for a
    // tool the model calls in the first turn (e.g. todo_write), run while that turn streams.
    private sealed class FixedReplyClient(string reply, Action? duringFirstTurn = null) : IChatClient
    {
        public int Streams;

        public ChatClientMetadata Metadata => new("test", null!, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Streams++ == 0) duringFirstTurn?.Invoke();
            return Reply();
        }

        private async IAsyncEnumerable<ChatResponseUpdate> Reply()
        {
            await Task.Yield();
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent(reply)] };
        }

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private ReplSessionContext NewContext(IChatClient client, ReplDefaultsConfig? repl = null)
    {
        var eventsPath = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        _eventsPaths.Add(eventsPath);
        var ctx = new ReplSessionContext(
            cwd: "/tmp", sessionId: "correction-cascade-session", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: repl is null ? null : new UserConfig { Repl = repl }, client: client, factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(eventsPath),
            eventsPath: eventsPath,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(Path.GetTempPath(), $"fuseraft-test-mem-{Guid.NewGuid():N}")),
            toolsByCategory: [], systemPrompt: "test system prompt", pendingSave: false,
            adaptiveTrimTracker: new());
        ctx.JsonMode = true;
        _contexts.Add(ctx);
        return ctx;
    }

    private static void WriteOpenItem(TodoPlugin todo) =>
        todo.Write("""[{"content":"Update foo.cs","status":"pending"}]""");

    private static Task RunAsync(ReplSessionContext ctx) =>
        ReplTurn.ExecuteAsync(
            ctx, "update foo", isStepRequest: false, capturePlan: false, activeStep: null, CancellationToken.None);

    [Fact]
    public async Task MutationCorrection_IsNotFollowedByASecondTodoChainFromTheSameTurn()
    {
        // Claims a file change without a write tool, and never closes the todo item.
        var todo   = new TodoPlugin();
        var client = new FixedReplyClient("I updated src/foo.cs.", () => WriteOpenItem(todo));
        var ctx = NewContext(client);
        ctx.Todo = todo;

        await RunAsync(ctx);

        // The user's turn, its mutation correction, and that correction's own todo chain
        // (MaxTodoNudges nudges; no subagent, so no critic turn). Not a second chain.
        Assert.Equal(2 + ctx.Limits.MaxTodoNudges, client.Streams);
    }

    [Fact]
    public async Task TodoChain_StillRunsWhenNoEarlierCorrectionFired()
    {
        var todo   = new TodoPlugin();
        var client = new FixedReplyClient("Working on it.", () => WriteOpenItem(todo));
        var ctx = NewContext(client);
        ctx.Todo = todo;

        await RunAsync(ctx);

        Assert.Equal(1 + ctx.Limits.MaxTodoNudges, client.Streams);
    }

    // ── which todo lists get nudged ──────────────────────────────────────────

    // A list an earlier request left open (or a resumed session restored) used to send nudges
    // after every later answer, however unrelated.
    [Fact]
    public async Task ListLeftOpenByAnEarlierRequest_IsNotNudged()
    {
        var todo = new TodoPlugin();
        WriteOpenItem(todo);
        var client = new FixedReplyClient("The build uses Cake.");
        var ctx = NewContext(client);
        ctx.Todo = todo;

        await RunAsync(ctx);

        Assert.Equal(1, client.Streams);
    }

    [Fact]
    public async Task ZeroTodoNudges_TurnsTheNudgesOff()
    {
        var todo   = new TodoPlugin();
        var client = new FixedReplyClient("Working on it.", () => WriteOpenItem(todo));
        var ctx = NewContext(client, new ReplDefaultsConfig { MaxTodoNudges = 0 });
        ctx.Todo = todo;

        await RunAsync(ctx);

        Assert.Equal(1, client.Streams);
    }

    [Fact]
    public async Task ConfiguredTodoNudges_SetsHowManyRun()
    {
        var todo   = new TodoPlugin();
        var client = new FixedReplyClient("Working on it.", () => WriteOpenItem(todo));
        var ctx = NewContext(client, new ReplDefaultsConfig { MaxTodoNudges = 4 });
        ctx.Todo = todo;

        await RunAsync(ctx);

        Assert.Equal(1 + 4, client.Streams);
    }

    [Fact]
    public async Task Clear_EmptiesTheTodoList()
    {
        var todo = new TodoPlugin();
        WriteOpenItem(todo);
        var ctx = NewContext(new FixedReplyClient("unused"));
        ctx.Todo = todo;

        await ReplCommands.HandleAsync(ctx, "/clear", string.Empty, CancellationToken.None);

        Assert.Empty(todo.Snapshot());
    }
}
