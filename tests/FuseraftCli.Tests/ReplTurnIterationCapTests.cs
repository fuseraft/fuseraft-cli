using System.Text.Json;
using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary>
/// Regression tests for tool_rounds/hit_iteration_cap accounting in <see cref="ReplTurn"/>.
/// toolRounds must count actual model round trips (one per underlying LLM call, signalled by
/// a <see cref="UsageContent"/> chunk or a non-null <c>FinishReason</c>) rather than gaps
/// between function-call chunks — a model that chains many consecutive tool calls with no text
/// in between (e.g. retrying a failing shell command) never produces such a gap, which
/// previously left toolRounds stuck at 1 regardless of how many iterations the
/// FunctionInvokingChatClient middleware actually ran, silently defeating the
/// hit_iteration_cap warning. FinishReason is the fallback signal for providers (e.g. Ollama)
/// that never report streaming usage at all.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplTurnIterationCapTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");
    private readonly List<string> _eventsPaths = [];
    private readonly List<ReplSessionContext> _contexts = [];

    public ReplTurnIterationCapTests() =>
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

    // Simulates a model that chains `rounds` consecutive tool calls — one FunctionCallContent
    // plus one trailing UsageContent per underlying LLM call, with no text chunk in between —
    // then finally responds with plain text once no tools remain (mirrors the streaming shape
    // observed when FunctionInvokingChatClient's MaximumIterationsPerRequest is hit).
    private static async IAsyncEnumerable<ChatResponseUpdate> ConsecutiveToolCallsThenTextAsync(int rounds)
    {
        for (var i = 0; i < rounds; i++)
        {
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent($"call-{i}", "shell_run"),
                    new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }),
                ],
            };
            await Task.Yield();
        }

        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new TextContent("I'll try a different approach."),
                new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }),
            ],
        };
    }

    // Same shape as ConsecutiveToolCallsThenTextAsync but never emits UsageContent — mirrors a
    // provider like Ollama that reports no streaming usage at all. Each round instead carries a
    // FinishReason (ToolCalls while a tool call is pending, Stop on the final text chunk), which
    // must be enough on its own to advance toolRounds so the cap warning still fires.
    private static async IAsyncEnumerable<ChatResponseUpdate> ConsecutiveToolCallsThenTextNoUsageAsync(int rounds)
    {
        for (var i = 0; i < rounds; i++)
        {
            yield return new ChatResponseUpdate
            {
                Role         = ChatRole.Assistant,
                FinishReason = ChatFinishReason.ToolCalls,
                Contents     = [new FunctionCallContent($"call-{i}", "shell_run")],
            };
            await Task.Yield();
        }

        yield return new ChatResponseUpdate
        {
            Role         = ChatRole.Assistant,
            FinishReason = ChatFinishReason.Stop,
            Contents     = [new TextContent("I'll try a different approach.")],
        };
    }

    // Two text-only rounds with no FunctionCallContent between them at all — mirrors the
    // FunctionInvokingChatClient middleware stripping tools on the forced last iteration (see
    // ReplTurn's hit_iteration_cap comment) and the model narrating text-only round after
    // text-only round, or a malformed tool-call attempt that never surfaces as a valid
    // FunctionCallContent. The only round-boundary signal here is UsageContent.
    private static async IAsyncEnumerable<ChatResponseUpdate> TextOnlyRoundsAsync(params string[] rounds)
    {
        foreach (var text in rounds)
        {
            yield return new ChatResponseUpdate
            {
                Role     = ChatRole.Assistant,
                Contents = [new TextContent(text), new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 })],
            };
            await Task.Yield();
        }
    }

    private sealed class StubChatClient(int rounds, bool withUsage = true) : IChatClient
    {
        public ChatClientMetadata Metadata => new("test", null!, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => withUsage
                ? ConsecutiveToolCallsThenTextAsync(rounds)
                : ConsecutiveToolCallsThenTextNoUsageAsync(rounds);

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private sealed class TextOnlyStubChatClient(params string[] rounds) : IChatClient
    {
        public ChatClientMetadata Metadata => new("test", null!, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => TextOnlyRoundsAsync(rounds);

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private ReplSessionContext NewContext(IChatClient client, string eventsPath)
    {
        _eventsPaths.Add(eventsPath);
        var ctx = new ReplSessionContext(
            cwd: "/tmp", sessionId: "iteration-cap-session", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: null, client: client, factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(eventsPath),
            eventsPath: eventsPath,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(Path.GetTempPath(), $"fuseraft-test-mem-{Guid.NewGuid():N}")),
            toolsByCategory: [], systemPrompt: "test system prompt", pendingSave: false,
            adaptiveTrimTracker: new());
        ctx.JsonMode = true; // skip Ansi/spinner rendering paths — irrelevant to this test
        _contexts.Add(ctx);
        return ctx;
    }

    [Fact]
    public async Task ConsecutiveToolCallsAtCap_EmitsHitIterationCapWarning()
    {
        var eventsPath = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        // rounds = ChatIterationLimit - 1 tool-call rounds, plus the stub's own trailing text
        // round, lands toolRounds exactly on ChatIterationLimit — pinning the >= boundary
        // itself rather than overshooting it, so a future `>=` -> `>` regression would be caught.
        var ctx = NewContext(new StubChatClient(ReplTurn.ChatIterationLimit - 1), eventsPath);

        await ReplTurn.ExecuteAsync(
            ctx, "fix it", isStepRequest: false, capturePlan: false, activeStep: null, CancellationToken.None);

        var events = await File.ReadAllLinesAsync(eventsPath);
        Assert.Contains(events, l => l.Contains("\"hit_iteration_cap\":true"));
    }

    [Fact]
    public async Task ConsecutiveToolCallsBelowCap_DoesNotEmitWarning()
    {
        var eventsPath = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        var ctx = NewContext(new StubChatClient(3), eventsPath);

        await ReplTurn.ExecuteAsync(
            ctx, "fix it", isStepRequest: false, capturePlan: false, activeStep: null, CancellationToken.None);

        var events = await File.ReadAllLinesAsync(eventsPath);
        Assert.DoesNotContain(events, l => l.Contains("\"hit_iteration_cap\":true"));
    }

    // Regression coverage for providers that never emit UsageContent on streaming responses
    // (e.g. Ollama) — see ReplSessionContext's usage-tracking comment. toolRounds must still
    // advance from FinishReason alone, or hit_iteration_cap silently stops firing for these
    // providers no matter how many tool rounds actually run.
    [Fact]
    public async Task ConsecutiveToolCallsAtCap_NoUsageContent_StillEmitsHitIterationCapWarning()
    {
        var eventsPath = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        var ctx = NewContext(new StubChatClient(ReplTurn.ChatIterationLimit - 1, withUsage: false), eventsPath);

        await ReplTurn.ExecuteAsync(
            ctx, "fix it", isStepRequest: false, capturePlan: false, activeStep: null, CancellationToken.None);

        var events = await File.ReadAllLinesAsync(eventsPath);
        Assert.Contains(events, l => l.Contains("\"hit_iteration_cap\":true"));
    }

    // Regression coverage for the run-together-narration bug: 78edb6b inserted a paragraph
    // break only when a round followed a FunctionCallContent, but a round boundary can also
    // occur with no tool call at all (the FunctionInvokingChatClient middleware stripping tools
    // on the forced last iteration is exactly this shape) — those boundaries must still get a
    // separator, or two consecutive rounds' narration glues together mid-sentence.
    [Fact]
    public async Task TextOnlyRoundsWithNoFunctionCall_GetParagraphBreakBetweenThem()
    {
        var eventsPath = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        var ctx = NewContext(
            new TextOnlyStubChatClient("Shell calls were getting mangled.", "Retrying with a minimal command."),
            eventsPath);

        await ReplTurn.ExecuteAsync(
            ctx, "fix it", isStepRequest: false, capturePlan: false, activeStep: null, CancellationToken.None);

        var content = await ReadAssistantResponseContentAsync(eventsPath);
        Assert.Contains("mangled.\n\nRetrying", content);
        Assert.DoesNotContain("mangled.Retrying", content);
    }

    private static async Task<string> ReadAssistantResponseContentAsync(string eventsPath)
    {
        foreach (var line in await File.ReadAllLinesAsync(eventsPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("event_type", out var et) &&
                et.GetString() == fuseraft.Core.Events.EventTypes.AssistantResponse &&
                doc.RootElement.TryGetProperty("payload", out var payload) &&
                payload.TryGetProperty("content", out var content))
                return content.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    // Round 0 is pure narration (no tool call) so responseText ends up non-empty and the
    // consecutive-failure warning block — gated on responseText.Length > 0, same as
    // hit_iteration_cap — actually fires, mirroring how a real model narrates before acting.
    // Rounds 1..N are FunctionCallContent+FunctionResultContent pairs whose result string is
    // given verbatim by `results`; a "[ERROR]"/"[FAIL]"/etc.-prefixed one counts as a tool
    // failure per ReplTurn.IsToolFailure, anything else resets the streak.
    private static async IAsyncEnumerable<ChatResponseUpdate> ToolCallResultRoundsAsync(
        List<int> roundsStarted, params string[] results)
    {
        yield return new ChatResponseUpdate
        {
            Role     = ChatRole.Assistant,
            Contents = [new TextContent("Let me check."), new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 })],
        };
        await Task.Yield();

        for (var i = 0; i < results.Length; i++)
        {
            roundsStarted.Add(i);
            yield return new ChatResponseUpdate
            {
                Role     = ChatRole.Assistant,
                Contents = [new FunctionCallContent($"call-{i}", "shell_run")],
            };
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionResultContent($"call-{i}", results[i]),
                    new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }),
                ],
            };
            await Task.Yield();
        }
    }

    private sealed class ToolCallResultStubChatClient(List<int> roundsStarted, params string[] results) : IChatClient
    {
        public ChatClientMetadata Metadata => new("test", null!, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => ToolCallResultRoundsAsync(roundsStarted, results);

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    // Regression coverage for replacing the flat round cap with a Cline/Codex-style
    // consecutive-failure cutoff: a genuinely stuck tool-call loop must stop itself well before
    // ChatIterationLimit, after MaxConsecutiveToolFailures failures in a row.
    [Fact]
    public async Task ConsecutiveToolFailures_StopsAfterThreshold_AndEmitsWarning()
    {
        var eventsPath = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        var roundsStarted = new List<int>();
        var ctx = NewContext(
            new ToolCallResultStubChatClient(roundsStarted,
                "[ERROR] boom 1", "[ERROR] boom 2", "[ERROR] boom 3", "[ERROR] boom 4", "[ERROR] boom 5"),
            eventsPath);

        await ReplTurn.ExecuteAsync(
            ctx, "fix it", isStepRequest: false, capturePlan: false, activeStep: null, CancellationToken.None);

        // The 4th and 5th failing rounds the stub had queued up were never even started —
        // proves the loop broke early rather than the stub simply running out of rounds.
        Assert.Equal(ReplTurn.MaxConsecutiveToolFailures, roundsStarted.Count);

        var events = await File.ReadAllLinesAsync(eventsPath);
        Assert.Contains(events, l => l.Contains("\"hit_consecutive_failure_limit\""));
    }

    // A success must reset the consecutive-failure streak — mirrors Cline's MistakeTracker
    // (consecutiveMistakes = 0 on any non-failing result). Never more than two failures in a
    // row here, so all six rounds must run even though total failures exceed the threshold.
    [Fact]
    public async Task ToolFailures_InterspersedWithSuccess_DoesNotTripCutoff()
    {
        var eventsPath = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        var roundsStarted = new List<int>();
        var ctx = NewContext(
            new ToolCallResultStubChatClient(roundsStarted,
                "[ERROR] boom", "[ERROR] boom", "[OK] fixed", "[ERROR] boom", "[ERROR] boom", "[OK] fixed"),
            eventsPath);

        await ReplTurn.ExecuteAsync(
            ctx, "fix it", isStepRequest: false, capturePlan: false, activeStep: null, CancellationToken.None);

        Assert.Equal(6, roundsStarted.Count);

        var events = await File.ReadAllLinesAsync(eventsPath);
        Assert.DoesNotContain(events, l => l.Contains("\"hit_consecutive_failure_limit\""));
    }
}
