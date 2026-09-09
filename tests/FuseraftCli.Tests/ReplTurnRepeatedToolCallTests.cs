using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary>
/// Regression tests for <see cref="ReplTurn.MaxConsecutiveIdenticalToolCalls"/> — the
/// repeated-identical-tool-call cutoff added alongside <see cref="ReplTurn.MaxConsecutiveToolFailures"/>
/// once the free-form chat round cap (<see cref="ReplTurn.ChatIterationLimit"/>) became
/// effectively unbounded. A model re-running the exact same call (same name, same arguments)
/// forever produces no failure signal at all — each call can "succeed" every time — so neither
/// backstop above ever engaged for that failure mode. See
/// ReplTurnIterationCapTests.ManyConsecutiveToolCalls_NeverEmitsIterationCapWarning for the
/// companion "many *varied* calls never false-positive" coverage.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplTurnRepeatedToolCallTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");
    private readonly List<string> _eventsPaths = [];
    private readonly List<ReplSessionContext> _contexts = [];

    public ReplTurnRepeatedToolCallTests() =>
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

    // Narration round, then `count` FunctionCallContent+FunctionResultContent pairs using the
    // *same* tool name and arguments every time, all succeeding — isolates
    // MaxConsecutiveIdenticalToolCalls from MaxConsecutiveToolFailures (nothing here ever fails).
    private static async IAsyncEnumerable<ChatResponseUpdate> IdenticalToolCallRoundsAsync(
        List<int> roundsStarted, int count)
    {
        yield return new ChatResponseUpdate
        {
            Role     = ChatRole.Assistant,
            Contents = [new TextContent("Checking."), new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 })],
        };
        await Task.Yield();

        for (var i = 0; i < count; i++)
        {
            roundsStarted.Add(i);
            yield return new ChatResponseUpdate
            {
                Role     = ChatRole.Assistant,
                Contents = [new FunctionCallContent($"call-{i}", "read_file", new Dictionary<string, object?> { ["path"] = "notes.md" })],
            };
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionResultContent($"call-{i}", "[OK] file contents unchanged"),
                    new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }),
                ],
            };
            await Task.Yield();
        }
    }

    // Same shape, but each round's arguments differ (a genuinely varied — not looping — call
    // pattern), so MaxConsecutiveIdenticalToolCalls must never trip here.
    private static async IAsyncEnumerable<ChatResponseUpdate> VariedToolCallRoundsAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return new ChatResponseUpdate
            {
                Role     = ChatRole.Assistant,
                Contents = [new FunctionCallContent($"call-{i}", "read_file", new Dictionary<string, object?> { ["path"] = $"file-{i}.md" })],
            };
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionResultContent($"call-{i}", "[OK] read"),
                    new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }),
                ],
            };
            await Task.Yield();
        }

        yield return new ChatResponseUpdate
        {
            Role     = ChatRole.Assistant,
            Contents = [new TextContent("Done."), new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 })],
        };
    }

    private sealed class RepeatedCallStubChatClient(List<int> roundsStarted, int count) : IChatClient
    {
        public ChatClientMetadata Metadata => new("test", null!, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => IdenticalToolCallRoundsAsync(roundsStarted, count);

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private sealed class VariedCallStubChatClient(int count) : IChatClient
    {
        public ChatClientMetadata Metadata => new("test", null!, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => VariedToolCallRoundsAsync(count);

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private ReplSessionContext NewContext(IChatClient client, string eventsPath)
    {
        _eventsPaths.Add(eventsPath);
        var ctx = new ReplSessionContext(
            cwd: "/tmp", sessionId: "repeated-call-session", startedAt: DateTime.UtcNow,
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

    [Fact]
    public async Task IdenticalConsecutiveToolCalls_StopsAfterThreshold_AndEmitsWarning()
    {
        var eventsPath = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        var roundsStarted = new List<int>();
        // Queue up more identical calls than the threshold so a premature stop (or none at all)
        // shows up as a wrong count rather than the stub simply running out.
        var ctx = NewContext(new RepeatedCallStubChatClient(roundsStarted, count: 10), eventsPath);

        await ReplTurn.ExecuteAsync(
            ctx, "check the file", isStepRequest: false, capturePlan: false, activeStep: null, CancellationToken.None);

        Assert.Equal(ReplTurn.MaxConsecutiveIdenticalToolCalls, roundsStarted.Count);

        var events = await File.ReadAllLinesAsync(eventsPath);
        Assert.Contains(events, l => l.Contains("\"hit_repeated_tool_call_limit\""));
    }

    [Fact]
    public async Task VariedToolCalls_NeverTripsRepeatedCallCutoff()
    {
        var eventsPath = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        var ctx = NewContext(new VariedCallStubChatClient(count: 20), eventsPath);

        await ReplTurn.ExecuteAsync(
            ctx, "check the files", isStepRequest: false, capturePlan: false, activeStep: null, CancellationToken.None);

        var events = await File.ReadAllLinesAsync(eventsPath);
        Assert.DoesNotContain(events, l => l.Contains("\"hit_repeated_tool_call_limit\""));
    }
}
