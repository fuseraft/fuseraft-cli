using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;
using fuseraft.Infrastructure.Tools;

namespace FuseraftCli.Tests;

[Collection("FuseraftHomeEnv")]
public sealed class ReplTurnPasteOffloadTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");
    private readonly string _artifacts = Path.Combine(Path.GetTempPath(), $"fuseraft-test-paste-{Guid.NewGuid():N}");
    private readonly List<ReplSessionContext> _contexts = [];

    public ReplTurnPasteOffloadTests() =>
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        foreach (var dir in new[] { _tempHome, _artifacts })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        foreach (var ctx in _contexts)
        {
            ctx.Emitter.Dispose();
            ctx.Factory.Dispose();
        }
    }

    private ReplSessionContext NewContext(bool fileTools = true, bool store = true)
    {
        var eventsPath = Path.Combine(_tempHome, $"events-{Guid.NewGuid():N}.jsonl");
        Directory.CreateDirectory(_tempHome);
        var tools = new Dictionary<string, List<AIFunction>>();
        if (fileTools)
            tools["FileSystem"] = [AIFunctionFactory.Create(() => "", "read_file"), AIFunctionFactory.Create(() => "", "grep_file")];
        var emitter = new EventEmitter(eventsPath);
        var ctx = new ReplSessionContext(
            cwd: "/tmp", sessionId: "paste-session", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: null, client: new NoopChatClient(), factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(), emitter: emitter, eventsPath: eventsPath,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(_tempHome, "mem")),
            toolsByCategory: tools, systemPrompt: "sys", pendingSave: false, adaptiveTrimTracker: new());
        ctx.JsonMode = true;
        if (store) ctx.ToolArtifactStore = new ToolResultArtifactStore(_artifacts, emitter);
        _contexts.Add(ctx);
        return ctx;
    }

    private sealed class NoopChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private static string BuildLog(int lines = 3_000) =>
        "Why does this build fail?\n" +
        string.Concat(Enumerable.Range(0, lines).Select(i => $"  Project{i % 31}.csproj : warning CS0618: obsolete member {i}\n")) +
        "Build FAILED. error CS0246: The type or namespace name 'PayPeriod' could not be found";

    [Fact]
    public void OffloadLargePaste_SavesTheWholeText_AndKeepsItsStartAndEndInTheMessage()
    {
        var ctx = NewContext();
        var log = BuildLog();

        var sent = ReplTurn.OffloadLargePaste(ctx, log);

        Assert.True(sent.Length < 8_000, $"{sent.Length} chars sent");
        Assert.Contains("Why does this build fail?", sent);
        Assert.Contains("'PayPeriod' could not be found", sent);
        Assert.Contains("read_file", sent);
        var saved = Directory.GetFiles(_artifacts, "*.txt").Single();
        Assert.Contains(saved, sent);
        Assert.Equal(log, File.ReadAllText(saved));
    }

    [Fact]
    public void OffloadLargePaste_LeavesOrdinaryMessagesAlone()
    {
        var ctx = NewContext();
        var message = "Fix the failing test in FooTests. " + new string('x', 5_000);

        Assert.Equal(message, ReplTurn.OffloadLargePaste(ctx, message));
        Assert.False(Directory.Exists(_artifacts));
    }

    [Fact]
    public void OffloadLargePaste_WithoutFileTools_SendsTheTextWhole()
    {
        // The model couldn't read the file back, so the paste has to go in the message.
        var log = BuildLog();

        Assert.Equal(log, ReplTurn.OffloadLargePaste(NewContext(fileTools: false), log));
        Assert.Equal(log, ReplTurn.OffloadLargePaste(NewContext(store: false), log));
    }
}
