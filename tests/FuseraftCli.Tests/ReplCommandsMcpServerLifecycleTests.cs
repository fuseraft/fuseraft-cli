using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;
using fuseraft.Infrastructure.Storage;

namespace FuseraftCli.Tests;

/// <summary>/mcp remove and /mcp reconnect for servers that didn't connect when the session started.</summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplCommandsMcpServerLifecycleTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");
    private readonly List<ReplSessionContext> _contexts = [];

    public ReplCommandsMcpServerLifecycleTests() =>
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ManagedMcpServers.EnvVar, null);
        foreach (var ctx in _contexts)
        {
            ctx.Emitter.Dispose();
            ctx.Factory.Dispose();
        }
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
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

    private ReplSessionContext NewContext(Dictionary<string, List<AIFunction>> toolsByCategory)
    {
        Directory.CreateDirectory(_tempHome);
        var eventsPath = Path.Combine(_tempHome, $"events-{Guid.NewGuid():N}.jsonl");
        var ctx = new ReplSessionContext(
            cwd: _tempHome, sessionId: "mcp-remove-session", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: null, client: new NoopChatClient(), factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(eventsPath),
            eventsPath: eventsPath,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(_tempHome, "mem")),
            toolsByCategory: toolsByCategory, systemPrompt: "test system prompt", pendingSave: false,
            adaptiveTrimTracker: new());
        ctx.JsonMode = true;
        _contexts.Add(ctx);
        return ctx;
    }

    private static McpServerConfig Saved(string name) =>
        new() { Name = name, Transport = "http", Url = "http://127.0.0.1:1/mcp" };

    [Fact]
    public async Task Remove_SavedServerThatIsNotConnected_ForgetsIt()
    {
        ReplMcpServerStore.Save([Saved("Sql"), Saved("Jira")]);
        var ctx = NewContext(new(StringComparer.OrdinalIgnoreCase));

        await ReplCommands.HandleAsync(ctx, "/mcp", "remove sql", CancellationToken.None);

        Assert.Equal(["Jira"], ReplMcpServerStore.Load().Select(s => s.Name));
    }

    [Fact]
    public async Task Remove_ConnectedServer_DropsToolsAndSavedEntry()
    {
        ReplMcpServerStore.Save([Saved("Sql")]);
        var ctx = NewContext(new(StringComparer.OrdinalIgnoreCase)
        {
            ["mcp:Sql"] = [AIFunctionFactory.Create(() => "ok", "query_database")],
        });

        await ReplCommands.HandleAsync(ctx, "/mcp", "remove Sql", CancellationToken.None);

        Assert.Empty(ReplMcpServerStore.Load());
        Assert.False(ctx.ToolsByCategory.ContainsKey("mcp:Sql"));
    }

    [Fact]
    public async Task Remove_UnknownServer_LeavesSavedServersAlone()
    {
        ReplMcpServerStore.Save([Saved("Sql")]);
        var ctx = NewContext(new(StringComparer.OrdinalIgnoreCase));

        await ReplCommands.HandleAsync(ctx, "/mcp", "remove Nope", CancellationToken.None);

        Assert.Equal(["Sql"], ReplMcpServerStore.Load().Select(s => s.Name));
    }

    [Fact]
    public async Task Reconnect_LauncherServerThatDidNotConnect_TriesToConnectIt()
    {
        Environment.SetEnvironmentVariable(ManagedMcpServers.EnvVar,
            "[{\"Name\":\"Jira\",\"Transport\":\"http\",\"Url\":\"http://127.0.0.1:1/mcp\"}]");
        var ctx = NewContext(new(StringComparer.OrdinalIgnoreCase));

        await ReplCommands.HandleAsync(ctx, "/mcp", "reconnect Jira", CancellationToken.None);

        // Only an attempt to connect creates the manager; an unknown name returns before that.
        Assert.NotNull(ctx.McpManager);
        Assert.False(ctx.ToolsByCategory.ContainsKey("mcp:Jira"));
    }

    [Fact]
    public async Task Reconnect_UnknownServer_DoesNotTryToConnect()
    {
        var ctx = NewContext(new(StringComparer.OrdinalIgnoreCase));

        await ReplCommands.HandleAsync(ctx, "/mcp", "reconnect Nope", CancellationToken.None);

        Assert.Null(ctx.McpManager);
    }
}
