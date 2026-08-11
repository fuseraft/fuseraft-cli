using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Core.Models.Session;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Regression tests for <c>/fork</c> dropping the session's todo list. <c>CmdForkAsync</c>
/// (<see cref="ReplCommands"/>) used to build its <see cref="ReplSessionSnapshot"/> by hand and
/// never passed <c>todoItems</c>, unlike <c>ReplTurn.SaveSnapshotAsync</c> — so a fork (and any
/// <c>--resume</c> of it) silently lost every todo_write the model had made, even though
/// <see cref="ReplSessionSnapshotTests"/> already proved the snapshot type itself round-trips
/// the field correctly. Isolates <c>FUSERAFT_HOME</c> so the fork's snapshot file lands in a
/// throwaway temp dir instead of the user's real <c>~/.fuseraft/repl-sessions</c>.
/// </summary>
public sealed class ReplForkTodoPersistenceTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");

    public ReplForkTodoPersistenceTests() =>
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
    }

    private static ReplSessionContext NewContext() => new(
        cwd: "/tmp", sessionId: "source-session", startedAt: DateTime.UtcNow,
        modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
        userCfg: null, client: new StubChatClient(), factory: new ChatClientFactory(),
        keyStore: new UnavailableKeyStore(),
        emitter: new EventEmitter(Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl")),
        eventsPath: "unused", memoryStore: MemoryStore.CreateForTest(Path.Combine(Path.GetTempPath(), $"fuseraft-test-mem-{Guid.NewGuid():N}")),
        toolsByCategory: [], systemPrompt: "test system prompt", pendingSave: false);

    /// <summary>Loads the single snapshot file /fork wrote (the isolated temp dir starts empty
    /// and /fork never re-saves the source session), regardless of its randomly generated ID.</summary>
    private async Task<ReplSessionSnapshot> LoadForkedSnapshotAsync()
    {
        var file = Assert.Single(Directory.GetFiles(FuseraftPaths.GlobalReplSessions, "repl-*.json"));
        var forkId = Path.GetFileNameWithoutExtension(file)["repl-".Length..];
        var snapshot = await ReplSessionSnapshot.LoadAsync(forkId);
        Assert.NotNull(snapshot);
        return snapshot!;
    }

    [Fact]
    public async Task Fork_WithActiveTodoItems_PersistsThemInSnapshot()
    {
        var ctx = NewContext();
        ctx.Todo = new TodoPlugin();
        ctx.Todo.Write("""[{"content":"step A","status":"completed"},{"content":"step B","status":"pending"}]""");

        await ReplCommands.HandleAsync(ctx, "/fork", "", CancellationToken.None);

        var snapshot = await LoadForkedSnapshotAsync();

        Assert.NotNull(snapshot.TodoItems);
        Assert.Equal(2, snapshot.TodoItems!.Length);
        Assert.Equal("step A",     snapshot.TodoItems[0].Content);
        Assert.Equal("completed",  snapshot.TodoItems[0].Status);
        Assert.Equal("step B",     snapshot.TodoItems[1].Content);
        Assert.Equal("pending",    snapshot.TodoItems[1].Status);
    }

    [Fact]
    public async Task Fork_WithEmptyTodoList_SnapshotTodoItemsIsNull()
    {
        var ctx = NewContext();
        ctx.Todo = new TodoPlugin();

        await ReplCommands.HandleAsync(ctx, "/fork", "", CancellationToken.None);

        var snapshot = await LoadForkedSnapshotAsync();

        Assert.Null(snapshot.TodoItems);
    }

    [Fact]
    public async Task Fork_WithNoTodoPlugin_DoesNotThrow()
    {
        var ctx = NewContext();
        ctx.Todo = null; // e.g. a session started with --no-tools

        var ex = await Record.ExceptionAsync(() =>
            ReplCommands.HandleAsync(ctx, "/fork", "", CancellationToken.None));

        Assert.Null(ex);
        var snapshot = await LoadForkedSnapshotAsync();
        Assert.Null(snapshot.TodoItems);
    }

    private sealed class StubChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("test", null!, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => EmptyAsync();

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }

        private static async IAsyncEnumerable<ChatResponseUpdate> EmptyAsync()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
