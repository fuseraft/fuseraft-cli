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

    // Distinct, succeeding calls in the shape a provider streams them: the round's call and its
    // usage, then (once the invocation loop has run the tool) the tool's result.
    private static async IAsyncEnumerable<ChatResponseUpdate> CostlyRounds(List<int> rounds, int count, int inputPerRound)
    {
        for (var i = 0; i < count; i++)
        {
            rounds.Add(i);
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent($"c{i}", "read_file", new Dictionary<string, object?> { ["path"] = $"f{i}.md" }),
                    new UsageContent(new UsageDetails { InputTokenCount = inputPerRound, OutputTokenCount = 5 }),
                ],
            };
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Tool,
                Contents = [new FunctionResultContent($"c{i}", $"[OK] contents of f{i}.md")],
            };
        }
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("Done.")] };
        yield return Usage(inputPerRound);
    }

    // FailingCalls without its opening text — a turn the failure cutoff stops before the model
    // has said anything at all.
    private static async IAsyncEnumerable<ChatResponseUpdate> SilentFailingCalls(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new FunctionCallContent($"c{i}", "read_file", new Dictionary<string, object?> { ["path"] = $"f{i}.md" })],
            };
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Tool,
                Contents = [new FunctionResultContent($"c{i}", $"[ERROR] boom {i}")],
            };
            yield return Usage(10);
        }
    }

    private static bool HasResult(ReplSessionContext ctx, string callId) =>
        ctx.History.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any(r => r.CallId == callId);

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

    // ── per-turn input-token budget / stopped turns ──────────────────────────

    [Fact]
    public async Task TurnTokenBudget_StopsAtTheRoundThatCrossesIt_KeepingThatRoundsResults()
    {
        var rounds = new List<int>();
        var client = new ScriptedClient(_ => CostlyRounds(rounds, count: 10, inputPerRound: 300_000));
        var (ctx, eventsPath) = NewContext(client, new ReplDefaultsConfig { MaxTurnInputTokens = 1_000_000 });

        await RunAsync(ctx);

        // 4 × 300k crosses 1M; the 4th round's tool already ran, so its result is kept, and the
        // 5th model call is never made.
        Assert.Equal(4, rounds.Count);
        Assert.True(HasResult(ctx, "c3"));
        Assert.False(HasResult(ctx, "c4"));
        var payload = Payload(await File.ReadAllLinesAsync(eventsPath), "hit_turn_token_budget");
        Assert.Equal(1_000_000, payload.GetProperty("limit").GetInt32());
        Assert.Equal(1_200_000, payload.GetProperty("input_tokens").GetInt64());
    }

    [Fact]
    public async Task TurnTokenBudget_StoppedTurnWithNoText_KeepsItsWorkAndRunsNoRetryTurn()
    {
        var rounds = new List<int>();
        var client = new ScriptedClient(_ => CostlyRounds(rounds, count: 10, inputPerRound: 300_000));
        var (ctx, eventsPath) = NewContext(client, new ReplDefaultsConfig { MaxTurnInputTokens = 1_000_000 });

        await RunAsync(ctx);

        Assert.Equal(1, client.Streams);
        Assert.Equal(ChatRole.Assistant, ctx.History[^1].Role);
        Assert.StartsWith(ReplTurn.StoppedEarlyNotePrefix, ctx.History[^1].Text);
        Assert.Contains("input-token budget", ctx.History[^1].Text);
        Assert.DoesNotContain(await File.ReadAllLinesAsync(eventsPath), l => l.Contains("\"correction_injected\""));
    }

    [Fact]
    public async Task TurnUnderTheTokenBudget_FinishesNormally()
    {
        var rounds = new List<int>();
        var (ctx, eventsPath) = NewContext(
            new ScriptedClient(_ => CostlyRounds(rounds, count: 3, inputPerRound: 10)), repl: null);

        await RunAsync(ctx);

        Assert.Equal(3, rounds.Count);
        Assert.Contains(ctx.History, m => m.Role == ChatRole.Assistant && m.Text.Contains("Done."));
        Assert.DoesNotContain(await File.ReadAllLinesAsync(eventsPath), l => l.Contains("\"hit_turn_token_budget\""));
    }

    [Fact]
    public void StopNote_JoinsAClosingTextMessage_RatherThanAddingASecondAssistantMessage()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "go"),
            new(ChatRole.Assistant, "Continuing with the remaining files."),
        };

        ReplTurn.AppendStoppedEarlyNote(history, "it reached the per-turn input-token budget");

        Assert.Equal(2, history.Count);
        Assert.Contains("Continuing with the remaining files.", history[^1].Text);
        Assert.Contains("input-token budget", history[^1].Text);
    }

    // Previously the failure cutoff on a turn with no text fell into the empty-reply retry: the
    // stopped turn's tool calls were dropped and a whole extra turn ran.
    [Fact]
    public async Task FailureCutoffWithNoText_KeepsTheToolCallsAndRunsNoRetryTurn()
    {
        var client = new ScriptedClient(_ => SilentFailingCalls(count: 12));
        var (ctx, eventsPath) = NewContext(client, repl: null);

        await RunAsync(ctx);

        Assert.Equal(1, client.Streams);
        Assert.True(HasResult(ctx, "c0"));
        Assert.StartsWith(ReplTurn.StoppedEarlyNotePrefix, ctx.History[^1].Text);
        Assert.Contains("tool calls in a row failed", ctx.History[^1].Text);
        Assert.Contains(await File.ReadAllLinesAsync(eventsPath), l => l.Contains("\"hit_consecutive_failure_limit\""));
    }

    // ── context warning / auto-compact threshold ─────────────────────────────

    // The provider's input count is taken after in-turn trimming, so it can sit far below the
    // history the REPL is actually keeping; the larger of the two has to decide.
    [Fact]
    public async Task ContextWarning_UsesTheKeptHistory_WhenTheProviderCountIsSmaller()
    {
        var (ctx, eventsPath) = NewContext(
            new ScriptedClient(_ => TextWithInputTokens(10)),
            new ReplDefaultsConfig { AutoCompact = false });
        ctx.History.Add(new ChatMessage(ChatRole.User, new string('x', 280_000)));   // ~70k of the 80k budget
        ctx.History.Add(new ChatMessage(ChatRole.Assistant, "ok"));

        await RunAsync(ctx);

        var payload = Payload(await File.ReadAllLinesAsync(eventsPath), "context_warning");
        Assert.False(payload.GetProperty("is_actual").GetBoolean());
        Assert.True(payload.GetProperty("estimated_tokens").GetInt32() >= 70_000);
    }

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
