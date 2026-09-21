using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary>
/// The REPL thresholds from the global config's <c>repl</c> section actually steer a real turn:
/// loop-guard cutoffs, the context-warning/auto-compact threshold, and stream retries.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplLimitsTurnTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");
    private readonly List<string> _eventsPaths = [];
    private readonly List<ReplSessionContext> _contexts = [];

    public ReplLimitsTurnTests() =>
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
        foreach (var ctx in _contexts) { ctx.Emitter.Dispose(); ctx.Factory.Dispose(); }
        foreach (var path in _eventsPaths) if (File.Exists(path)) File.Delete(path);
    }

    private sealed class ScriptedClient(Func<int, IAsyncEnumerable<ChatResponseUpdate>> stream) : IChatClient
    {
        public int Streams;

        public ChatClientMetadata Metadata => new("test", null!, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => stream(Streams++);

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private static ChatResponseUpdate Usage(int input) => new()
    {
        Role = ChatRole.Assistant,
        Contents = [new UsageContent(new UsageDetails { InputTokenCount = input, OutputTokenCount = 5 })],
    };

    // Same tool + arguments every round, each succeeding — nothing here ever fails.
    private static async IAsyncEnumerable<ChatResponseUpdate> IdenticalCalls(List<int> rounds, int count)
    {
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("Checking.")] };
        for (var i = 0; i < count; i++)
        {
            rounds.Add(i);
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new FunctionCallContent($"c{i}", "read_file", new Dictionary<string, object?> { ["path"] = "notes.md" })],
            };
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new FunctionResultContent($"c{i}", "[OK] unchanged")],
            };
            yield return Usage(10);
        }
    }

    // Distinct arguments each round (so the identical-call cutoff can't fire), each failing.
    private static async IAsyncEnumerable<ChatResponseUpdate> FailingCalls(List<int> rounds, int count)
    {
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("Trying.")] };
        for (var i = 0; i < count; i++)
        {
            rounds.Add(i);
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new FunctionCallContent($"c{i}", "read_file", new Dictionary<string, object?> { ["path"] = $"f{i}.md" })],
            };
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new FunctionResultContent($"c{i}", $"[ERROR] boom {i}")],
            };
            yield return Usage(10);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> TextWithInputTokens(int inputTokens)
    {
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("Done.")] };
        await Task.Yield();
        yield return Usage(inputTokens);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> DropsThenAnswers(int attempt, int dropsBeforeSuccess,
        [EnumeratorCancellation] CancellationToken _ = default)
    {
        await Task.Yield();
        if (attempt < dropsBeforeSuccess) throw new IOException("connection was reset");
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("Recovered.")] };
        yield return Usage(10);
    }

    private (ReplSessionContext Ctx, string EventsPath) NewContext(IChatClient client, ReplDefaultsConfig? repl)
    {
        var eventsPath = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        _eventsPaths.Add(eventsPath);
        var ctx = new ReplSessionContext(
            cwd: "/tmp", sessionId: "limits-session", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: repl is null ? null : new UserConfig { Repl = repl },
            client: client, factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(eventsPath),
            eventsPath: eventsPath,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(Path.GetTempPath(), $"fuseraft-test-mem-{Guid.NewGuid():N}")),
            toolsByCategory: [], systemPrompt: "test system prompt", pendingSave: false,
            adaptiveTrimTracker: new());
        ctx.JsonMode = true;
        _contexts.Add(ctx);
        return (ctx, eventsPath);
    }

    private static Task RunAsync(ReplSessionContext ctx) =>
        ReplTurn.ExecuteAsync(ctx, "go", isStepRequest: false, capturePlan: false, activeStep: null, CancellationToken.None);

    private static JsonElement Payload(string[] events, string message)
    {
        var line = Assert.Single(events, l => l.Contains($"\"{message}\""));
        return JsonDocument.Parse(line).RootElement.GetProperty("payload").Clone();
    }

    // ── loop guards ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    public async Task ConfiguredIdenticalCallLimit_StopsTheTurnAtThatLimit(int limit)
    {
        var rounds = new List<int>();
        var (ctx, eventsPath) = NewContext(
            new ScriptedClient(_ => IdenticalCalls(rounds, count: 12)),
            new ReplDefaultsConfig { MaxIdenticalToolCalls = limit });

        await RunAsync(ctx);

        Assert.Equal(limit, rounds.Count);
        var payload = Payload(await File.ReadAllLinesAsync(eventsPath), "hit_repeated_tool_call_limit");
        Assert.Equal(limit, payload.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task ConfiguredFailureLimit_StopsTheTurnAtThatLimit()
    {
        var rounds = new List<int>();
        var (ctx, eventsPath) = NewContext(
            new ScriptedClient(_ => FailingCalls(rounds, count: 12)),
            new ReplDefaultsConfig { MaxConsecutiveToolFailures = 6 });

        await RunAsync(ctx);

        Assert.Equal(6, rounds.Count);
        var payload = Payload(await File.ReadAllLinesAsync(eventsPath), "hit_consecutive_failure_limit");
        Assert.Equal(6, payload.GetProperty("failures").GetInt32());
    }

    // ── context warning / auto-compact threshold ─────────────────────────────

    // The budget for an unrecognised model ID is 80,000 tokens.
    [Theory]
    [InlineData(50_000, 0.75, false)]   // 62.5 % — below the default
    [InlineData(50_000, 0.50, true)]    // lowered threshold now trips on the same turn
    [InlineData(70_000, 0.75, true)]    // 87.5 % — over the default
    [InlineData(70_000, 0.90, false)]   // raised threshold leaves it alone
    public async Task ContextWarning_FiresAtTheConfiguredThreshold(int inputTokens, double threshold, bool expectWarning)
    {
        // autoCompact off so a crossing only warns — no summarisation call needs a stub.
        var (ctx, eventsPath) = NewContext(
            new ScriptedClient(_ => TextWithInputTokens(inputTokens)),
            new ReplDefaultsConfig { AutoCompact = false, AutoCompactThreshold = threshold });

        await RunAsync(ctx);

        var events = await File.ReadAllLinesAsync(eventsPath);
        Assert.Equal(expectWarning, events.Any(l => l.Contains("\"context_warning\"")));
    }

    [Fact]
    public async Task NoConfiguredThreshold_KeepsTheOriginalSeventyFivePercent()
    {
        var (ctx, eventsPath) = NewContext(
            new ScriptedClient(_ => TextWithInputTokens(61_000)),   // 76.25 %
            new ReplDefaultsConfig { AutoCompact = false });

        await RunAsync(ctx);

        Assert.Contains(await File.ReadAllLinesAsync(eventsPath), l => l.Contains("\"context_warning\""));
    }

    // ── stream retries ───────────────────────────────────────────────────────

    [Fact]
    public async Task ZeroStreamRetries_SurfacesADroppedStreamOnTheFirstAttempt()
    {
        var client = new ScriptedClient(attempt => DropsThenAnswers(attempt, dropsBeforeSuccess: 1));
        var (ctx, _) = NewContext(client, new ReplDefaultsConfig { MaxStreamRetries = 0 });

        await RunAsync(ctx);

        Assert.Equal(1, client.Streams);
    }

    [Fact]
    public async Task OneStreamRetry_RecoversFromASingleDroppedStream()
    {
        var client = new ScriptedClient(attempt => DropsThenAnswers(attempt, dropsBeforeSuccess: 1));
        var (ctx, _) = NewContext(client, new ReplDefaultsConfig { MaxStreamRetries = 1 });

        await RunAsync(ctx);

        Assert.Equal(2, client.Streams);
        Assert.Contains(ctx.History, m => m.Role == ChatRole.Assistant && m.Text.Contains("Recovered."));
    }
}
