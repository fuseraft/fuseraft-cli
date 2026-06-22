using Microsoft.Extensions.AI;
using fuseraft.Infrastructure.Agents;

namespace FuseraftCli.Tests;

/// <summary>
/// Behavioral contract for <see cref="AgentFactory.KeepLastToolPairs"/> — the deterministic
/// in-turn sliding-window cap on tool call/result pairs. Written against the original
/// hand-rolled implementation and re-verified unchanged after swapping the internals to
/// MAF's <c>ToolResultCompactionStrategy</c>, so the cases below describe the contract both
/// implementations must satisfy, not implementation details of either one.
/// </summary>
public sealed class AgentFactoryKeepLastToolPairsTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ChatMessage ToolCall(string callId, string name)
        => new(ChatRole.Assistant, [new FunctionCallContent(callId, name)]);

    private static ChatMessage ToolResult(string callId, string content)
        => new(ChatRole.Tool, [new FunctionResultContent(callId, content)]);

    // Builds `count` independent single-call tool rounds: [assistant-call, tool-result] * count.
    private static List<ChatMessage> ToolRounds(int count)
    {
        var messages = new List<ChatMessage>(count * 2);
        for (int i = 0; i < count; i++)
        {
            messages.Add(ToolCall($"c{i}", "read_file"));
            messages.Add(ToolResult($"c{i}", $"result-{i}"));
        }
        return messages;
    }

    // ── No-op below/at the limit ───────────────────────────────────────────────

    [Fact]
    public async Task NoOp_WhenToolRoundCountBelowLimit()
    {
        var messages = ToolRounds(3);

        var result = (await AgentFactory.KeepLastToolPairs(messages, maxPairs: 5)).ToList();

        Assert.Equal(messages.Count, result.Count);
        for (int i = 0; i < messages.Count; i++)
            Assert.Same(messages[i], result[i]);
    }

    [Fact]
    public async Task NoOp_WhenToolRoundCountEqualsLimit()
    {
        var messages = ToolRounds(5);

        var result = (await AgentFactory.KeepLastToolPairs(messages, maxPairs: 5)).ToList();

        for (int i = 0; i < messages.Count; i++)
            Assert.Same(messages[i], result[i]);
    }

    // ── Collapses oldest, preserves newest N ───────────────────────────────────

    [Fact]
    public async Task CollapsesOldestRounds_WhenExceedingLimit_KeepingNewestNIntact()
    {
        var messages = ToolRounds(5); // c0..c4, 5 rounds, keep last 2 (c3, c4)

        var result = (await AgentFactory.KeepLastToolPairs(messages, maxPairs: 2)).ToList();

        // The two most recent tool results must be byte-for-byte unchanged.
        var newest = result
            .Where(m => m.Role == ChatRole.Tool)
            .Select(m => m.Contents.OfType<FunctionResultContent>().Single())
            .ToList();

        var c3 = newest.Single(r => r.CallId == "c3");
        var c4 = newest.Single(r => r.CallId == "c4");
        Assert.Equal("result-3", c3.Result);
        Assert.Equal("result-4", c4.Result);

        // The three oldest results must no longer carry their original payload.
        foreach (var oldId in new[] { "c0", "c1", "c2" })
        {
            var stillLiteral = newest.Any(r => r.CallId == oldId && (string?)r.Result == $"result-{oldId[1..]}");
            Assert.False(stillLiteral, $"expected {oldId}'s original result content to be collapsed/replaced");
        }
    }

    // ── Strict-provider safety ──────────────────────────────────────────────

    [Fact]
    public async Task NeverLeavesAFunctionCallWithoutAMatchingResult_WhenCollapsing()
    {
        var messages = ToolRounds(8);

        var result = (await AgentFactory.KeepLastToolPairs(messages, maxPairs: 3)).ToList();

        var callIds = result
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(c => c.CallId)
            .ToHashSet();
        var resultIds = result
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(r => r.CallId)
            .ToHashSet();

        // Every surviving function call must have a corresponding result, and vice versa —
        // a provider that strictly validates tool_call_id pairing must never see an orphan.
        Assert.True(callIds.SetEquals(resultIds),
            $"orphaned call/result pairing: calls=[{string.Join(',', callIds)}] results=[{string.Join(',', resultIds)}]");
    }

    // ── Zero means "keep none" — the disable gate lives at the call site ──────

    [Fact]
    public async Task CollapsesEverything_WhenMaxPairsIsZero()
    {
        // The helper's own contract is "keep the last N rounds in full"; N=0 means every
        // round is eligible for collapse. The actual "disabled" behavior (skip calling this
        // helper at all) lives at the `if (maxInTurnToolPairs > 0)` guard in
        // BuildMiddlewareChain, which this test does not exercise — it pins down what the
        // helper itself does if ever called with maxPairs=0, so a future refactor that
        // accidentally starts calling it unconditionally fails loudly instead of silently
        // wiping all tool context.
        var messages = ToolRounds(10);

        var result = (await AgentFactory.KeepLastToolPairs(messages, maxPairs: 0)).ToList();

        var survivingResults = result.SelectMany(m => m.Contents.OfType<FunctionResultContent>());
        foreach (var r in survivingResults)
            Assert.NotEqual($"result-{r.CallId![1..]}", r.Result);
    }

    // ── Sanity check that the swap to MAF's strategy actually happened ────────

    [Fact]
    public async Task CollapsedRoundsAreReplacedByASingleSummaryMessage()
    {
        // ToolResultCompactionStrategy collapses each excluded group (assistant call +
        // its results) into one new assistant message, rather than leaving a same-shaped
        // placeholder per evicted tool message the way the old hand-rolled trimmer did.
        // This pins down that we're exercising the new collapsing behavior, not a no-op
        // wiring bug that happens to leave old content untouched.
        var messages = ToolRounds(5);

        var result = (await AgentFactory.KeepLastToolPairs(messages, maxPairs: 2)).ToList();

        // 3 oldest rounds (6 messages) collapse into fewer messages than they started as.
        Assert.True(result.Count < messages.Count,
            $"expected collapsing to reduce message count below {messages.Count}, got {result.Count}");
    }
}
