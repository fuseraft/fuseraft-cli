using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary>A reply that says a tool was blocked when no call this turn failed is sent back once.</summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplTurnRefusalClaimTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");
    private readonly List<ReplSessionContext> _contexts = [];

    public ReplTurnRefusalClaimTests() =>
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        foreach (var ctx in _contexts) { ctx.Emitter.Dispose(); ctx.Factory.Dispose(); }
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
    }

    // Each turn's first stream optionally runs one tool call with the given result, then every stream answers with `reply`.
    private sealed class ScriptedClient(string reply, string? toolResult = null) : IChatClient
    {
        public int Streams;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            Reply(Streams++ == 0 ? toolResult : null);

        private async IAsyncEnumerable<ChatResponseUpdate> Reply(string? result)
        {
            await Task.Yield();
            if (result is not null)
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new FunctionCallContent("call-1", "delete_file", new Dictionary<string, object?> { ["path"] = "6" })],
                };
                yield return new ChatResponseUpdate { Role = ChatRole.Tool, Contents = [new FunctionResultContent("call-1", result)] };
            }
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent(reply)] };
        }

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private ReplSessionContext NewContext(IChatClient client)
    {
        Directory.CreateDirectory(_tempHome);
        var eventsPath = Path.Combine(_tempHome, $"events-{Guid.NewGuid():N}.jsonl");
        var ctx = new ReplSessionContext(
            cwd: "/tmp", sessionId: "refusal-claim-session", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: null, client: client, factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(), emitter: new EventEmitter(eventsPath), eventsPath: eventsPath,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(_tempHome, "mem")),
            toolsByCategory: [], systemPrompt: "sys", pendingSave: false, adaptiveTrimTracker: new());
        ctx.JsonMode = true;
        _contexts.Add(ctx);
        return ctx;
    }

    private static Task RunAsync(ReplSessionContext ctx) =>
        ReplTurn.ExecuteAsync(ctx, "delete it", isStepRequest: false, capturePlan: false, activeStep: null, CancellationToken.None);

    private static bool HasCorrection(ReplSessionContext ctx) =>
        ctx.History.Any(m => m.Role == ChatRole.User && m.Text.StartsWith(ReplTurn.NoDenialCorrectionPrefix, StringComparison.Ordinal));

    [Fact]
    public async Task ClaimedRefusalWithNothingDenied_IsCorrectedOnce()
    {
        // Grok's reply after calling only get_file_info, with HITL off.
        var client = new ScriptedClient("I can't delete it from here — that call was blocked.");
        var ctx = NewContext(client);

        await RunAsync(ctx);

        Assert.True(HasCorrection(ctx));
        Assert.Equal(2, client.Streams);
    }

    [Fact]
    public async Task ClaimedRefusalAfterADeniedCall_IsLeftAlone()
    {
        var client = new ScriptedClient("That call was blocked, so I stopped.", "[DENIED] File delete blocked by user.");
        var ctx = NewContext(client);

        await RunAsync(ctx);

        Assert.False(HasCorrection(ctx));
    }

    [Theory]
    [InlineData("I can't delete it from here — that call was blocked.")]
    [InlineData("I can’t commit or push from here — those actions were blocked.")]
    [InlineData("The tool was denied, so I didn't retry it.")]
    [InlineData("It was blocked by the approval settings.")]
    public void RecognizesRefusalClaims(string text) =>
        Assert.True(ReplTurn.ClaimsToolRefusal(text));

    [Theory]
    [InlineData("I deleted the file.")]
    [InlineData("The UI thread is blocked while the query runs, so the form freezes.")]
    [InlineData("Access to the share is denied for service accounts; the job needs a domain account.")]
    [InlineData("The push was rejected because the branch is behind.")]
    public void LeavesOtherTextAlone(string text) =>
        Assert.False(ReplTurn.ClaimsToolRefusal(text));
}
