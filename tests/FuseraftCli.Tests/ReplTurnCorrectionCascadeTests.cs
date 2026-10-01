using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
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
/// chain from round 0 and run it a second time.
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

    // Every turn answers with the same text and calls no tool.
    private sealed class FixedReplyClient(string reply) : IChatClient
    {
        public int Streams;

        public ChatClientMetadata Metadata => new("test", null!, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Streams++;
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

    private ReplSessionContext NewContext(IChatClient client)
    {
        var eventsPath = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        _eventsPaths.Add(eventsPath);
        var ctx = new ReplSessionContext(
            cwd: "/tmp", sessionId: "correction-cascade-session", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: null, client: client, factory: new ChatClientFactory(),
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

    private static TodoPlugin OpenTodo()
    {
        var todo = new TodoPlugin();
        todo.Write("""[{"content":"Update foo.cs","status":"pending"}]""");
        return todo;
    }

    [Fact]
    public async Task MutationCorrection_IsNotFollowedByASecondTodoChainFromTheSameTurn()
    {
        // Claims a file change without a write tool, and never closes the todo item.
        var client = new FixedReplyClient("I updated src/foo.cs.");
        var ctx = NewContext(client);
        ctx.Todo = OpenTodo();

        await ReplTurn.ExecuteAsync(
            ctx, "update foo", isStepRequest: false, capturePlan: false, activeStep: null, CancellationToken.None);

        // The user's turn, its mutation correction, and that correction's own todo chain
        // (MaxTodoCorrectionRounds nudges; no subagent, so no critic turn). Not a second chain.
        Assert.Equal(2 + ReplTurn.MaxTodoCorrectionRounds, client.Streams);
    }

    [Fact]
    public async Task TodoChain_StillRunsWhenNoEarlierCorrectionFired()
    {
        var client = new FixedReplyClient("Working on it.");
        var ctx = NewContext(client);
        ctx.Todo = OpenTodo();

        await ReplTurn.ExecuteAsync(
            ctx, "update foo", isStepRequest: false, capturePlan: false, activeStep: null, CancellationToken.None);

        Assert.Equal(1 + ReplTurn.MaxTodoCorrectionRounds, client.Streams);
    }
}
