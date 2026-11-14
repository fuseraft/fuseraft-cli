using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace FuseraftCli.Tests;

/// <summary>
/// In-turn budgets shrink by how far the provider's count of the last request outran the char estimate: grok-4.7's
/// billed input ran 1.5–2x the estimate (xAI bills earlier rounds' hidden reasoning as prompt), so trimming started late.
/// </summary>
public sealed class InputTokenCalibrationTests
{
    [Fact]
    public void BeforeAnyCount_TheBudgetIsUnchanged() =>
        Assert.Equal(10_000, new InputTokenCalibration().Scale(10_000));

    [Theory]
    [InlineData(2_000, 10_000)] // counted half the estimate: never loosens
    [InlineData(5_000, 10_000)] // as estimated
    [InlineData(10_000, 5_000)] // twice the estimate
    [InlineData(1_000_000, 2_500)] // capped at 4x
    public void TheBudget_ShrinksByHowFarTheCountOutranTheEstimate(long promptTokens, int expected)
    {
        var calibration = new InputTokenCalibration();
        calibration.Record(estimatedChars: 5_000 * TokenEstimator.CharsPerToken, promptTokens);

        Assert.Equal(expected, calibration.Scale(10_000));
    }

    // A trimmed request counts lower (xAI adds old reasoning back only to an unchanged history); letting that loosen the
    // budget made requests flip between trimmed and untrimmed, missing the prompt cache each time.
    [Fact]
    public void ALowerLaterCount_DoesNotLoosenTheBudget()
    {
        var calibration = new InputTokenCalibration();
        calibration.Record(4_000, 1_750); // 1.75x
        calibration.Record(4_000, 1_250); // 1.25x

        Assert.Equal(1.75, calibration.Factor);
    }

    [Fact]
    public void ARequestTooSmallToJudge_IsIgnored()
    {
        var calibration = new InputTokenCalibration(minSampleChars: 40_000);
        calibration.Record(8_000, 4_000);   // 2x, but the provider's fixed overhead dominates a request this small
        calibration.Record(40_000, 12_500); // 1.25x

        Assert.Equal(1.25, calibration.Factor);
    }

    [Fact]
    public void NoBudget_StaysNoBudget()
    {
        var calibration = new InputTokenCalibration();
        calibration.Record(4_000, 3_000);

        Assert.Equal(0, calibration.Scale(0));
    }

    // ── Through the middleware ────────────────────────────────────────────────

    private const int TriggerChars = 20_000;

    // 20 rounds of about 610 chars each: about 12,200 estimated chars, under the 18,000 the trigger engages at, and
    // enough rounds that the window's 16-group step moves.
    private static List<ChatMessage> Transcript()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "go") };
        for (var i = 0; i < 20; i++)
        {
            messages.Add(new(ChatRole.Assistant, [new FunctionCallContent($"c{i}", "read_file")]));
            messages.Add(new(ChatRole.Tool, [new FunctionResultContent($"c{i}", $"result-{i} " + new string('x', 590))]));
        }
        return messages;
    }

    private static IChatClient Chain(IChatClient inner) =>
        new AgentMiddlewareBuilder(NullLogger.Instance, changeTracker: null, securityConfig: null, governanceKernel: null)
            .BuildMiddlewareChain(
                chatClient: inner, config: new AgentConfig { Name = "a", Model = new() { ModelId = "m" } }, chatOptions: null,
                maxContextChars: 0, maxInTurnChars: 0, maxInTurnToolPairs: 2, toolSchemaChars: 0, maxPayloadBytes: 0,
                hasHandoff: false, triggerChars: TriggerChars);

    private static bool FirstResultIntact(IEnumerable<ChatMessage> sent) =>
        sent.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Any(r => ToolResultText.ToStringOrDefault(r.Result).StartsWith("result-0 xxx"));

    [Theory]
    [InlineData(3_100, true)]  // the provider counted what fuseraft estimated: still under budget
    [InlineData(6_200, false)] // twice that: the turn is past its real budget, so older results go
    public async Task TheNextRequest_IsTrimmedAgainstTheProvidersCount(long reportedTokens, bool keptWhole)
    {
        var inner = new CountingClient(reportedTokens);
        var client = Chain(inner);

        await client.GetResponseAsync(Transcript());
        await client.GetResponseAsync(Transcript());

        Assert.True(FirstResultIntact(inner.Sent[0]));
        Assert.Equal(keptWhole, FirstResultIntact(inner.Sent[1]));
    }

    [Fact]
    public async Task Streaming_UsesTheProvidersCountToo()
    {
        var inner = new CountingClient(6_200);
        var client = Chain(inner);

        await foreach (var _ in client.GetStreamingResponseAsync(Transcript())) { }
        await foreach (var _ in client.GetStreamingResponseAsync(Transcript())) { }

        Assert.True(FirstResultIntact(inner.Sent[0]));
        Assert.False(FirstResultIntact(inner.Sent[1]));
    }

    private sealed class CountingClient(long promptTokens) : IChatClient
    {
        public List<List<ChatMessage>> Sent { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Sent.Add(messages.ToList());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
            {
                Usage = new UsageDetails { InputTokenCount = promptTokens },
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Sent.Add(messages.ToList());
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Contents = [new UsageContent(new UsageDetails { InputTokenCount = promptTokens })],
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
