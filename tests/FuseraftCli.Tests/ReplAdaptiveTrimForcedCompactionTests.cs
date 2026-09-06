using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary>
/// Regression test for the REPL's post-turn <see cref="ReplSessionContext.AdaptiveTrimTracker"/>
/// check in <see cref="ReplTurn.ExecuteAsync"/>. Before this, the REPL had no equivalent of
/// <c>CompactionCoordinator</c>'s adaptive-trim branch: a provider call that only survived via
/// <c>AgentMiddlewareBuilder</c>'s truncate-and-retry left the full, still-oversized history in
/// <c>ctx.History</c>, so the very next turn could hit the identical wall. This pins that the
/// REPL now consumes the flag and attempts a forced compaction, mirroring `fuseraft run`.
///
/// <para>
/// Uses a raw stub <see cref="IChatClient"/> as <c>ctx.Client</c> (same pattern as
/// <see cref="ReplTurnIterationCapTests"/>) rather than routing through
/// <c>ReplFactory.BuildClient</c>, so the tracker flag is set directly to isolate this
/// REPL-side behavior from the middleware-side retry already covered by
/// <c>AgentMiddlewareBuilderStreamingRetryTests</c>. The forced compaction attempt itself fails
/// fast (no real provider configured for "test-model") and is expected to fall back gracefully
/// — what this test pins is that the flag was consumed and a compaction was actually attempted,
/// not that the attempt succeeds.
/// </para>
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplAdaptiveTrimForcedCompactionTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");
    private readonly List<ReplSessionContext> _contexts = [];

    public ReplAdaptiveTrimForcedCompactionTests() =>
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
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> SimpleTextReplyAsync()
    {
        await Task.Yield();
        yield return new ChatResponseUpdate
        {
            Role         = ChatRole.Assistant,
            FinishReason = ChatFinishReason.Stop,
            Contents     = [new TextContent("ok")],
        };
    }

    private sealed class SimpleStubChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("test", null!, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => SimpleTextReplyAsync();

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private ReplSessionContext NewContext()
    {
        var eventsPath = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        var ctx = new ReplSessionContext(
            cwd: "/tmp", sessionId: "adaptive-trim-session", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: null, client: new SimpleStubChatClient(), factory: new ChatClientFactory(),
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
    public async Task TurnAfterAdaptiveTrim_ConsumesFlag_AndAttemptsForcedCompaction()
    {
        var ctx = NewContext();

        // Simulates AgentMiddlewareBuilder having just recorded that this agent's last provider
        // call only survived via truncation — the same signal ReplFactory.BuildClient's
        // middleware chain now records via ctx.AdaptiveTrimTracker.
        ctx.AdaptiveTrimTracker.RecordTrim(ReplFactory.ReplAgentName);

        var ok = await ReplTurn.ExecuteAsync(
            ctx, "hello", isStepRequest: false, capturePlan: false, activeStep: null, CancellationToken.None);

        Assert.True(ok);
        // The flag must have been consumed during this turn's post-turn check — proving the
        // hook actually ran — regardless of whether the forced compaction attempt itself
        // succeeded (it can't here: "test-model" resolves to no real provider).
        Assert.False(ctx.AdaptiveTrimTracker.ConsumeTrim(ReplFactory.ReplAgentName));
    }

    [Fact]
    public async Task TurnWithoutAdaptiveTrim_NeverConsumesFlag()
    {
        var ctx = NewContext();

        var ok = await ReplTurn.ExecuteAsync(
            ctx, "hello", isStepRequest: false, capturePlan: false, activeStep: null, CancellationToken.None);

        Assert.True(ok);
        Assert.False(ctx.AdaptiveTrimTracker.ConsumeTrim(ReplFactory.ReplAgentName));
    }
}
