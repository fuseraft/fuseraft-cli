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
/// a <see cref="UsageContent"/> chunk) rather than gaps between function-call chunks — a model
/// that chains many consecutive tool calls with no text in between (e.g. retrying a failing
/// shell command) never produces such a gap, which previously left toolRounds stuck at 1
/// regardless of how many iterations the FunctionInvokingChatClient middleware actually ran,
/// silently defeating the hit_iteration_cap warning.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplTurnIterationCapTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");

    public ReplTurnIterationCapTests() =>
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
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

    private sealed class StubChatClient(int rounds) : IChatClient
    {
        public ChatClientMetadata Metadata => new("test", null!, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => ConsecutiveToolCallsThenTextAsync(rounds);

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private static ReplSessionContext NewContext(IChatClient client, string eventsPath)
    {
        var ctx = new ReplSessionContext(
            cwd: "/tmp", sessionId: "iteration-cap-session", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: null, client: client, factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(eventsPath),
            eventsPath: eventsPath,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(Path.GetTempPath(), $"fuseraft-test-mem-{Guid.NewGuid():N}")),
            toolsByCategory: [], systemPrompt: "test system prompt", pendingSave: false);
        ctx.JsonMode = true; // skip Ansi/spinner rendering paths — irrelevant to this test
        return ctx;
    }

    [Fact]
    public async Task ConsecutiveToolCallsAtCap_EmitsHitIterationCapWarning()
    {
        var eventsPath = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        var ctx = NewContext(new StubChatClient(ReplTurn.ChatIterationLimit), eventsPath);

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
}
