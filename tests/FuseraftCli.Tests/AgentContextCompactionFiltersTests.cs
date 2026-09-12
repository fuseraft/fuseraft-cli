using Microsoft.Extensions.AI;
using fuseraft.Infrastructure.Agents;

namespace FuseraftCli.Tests;

/// <summary>
/// Behavioral contract for the cache-preservation changes to
/// <see cref="AgentContextCompactionFilters"/>: <c>ApplyInTurnFilters</c>'s <c>triggerChars</c>
/// gate on <see cref="AgentContextCompactionFilters.KeepLastToolPairs"/>, and the
/// <c>minBatchChars</c> batching on <see cref="AgentContextCompactionFilters.DropSupersededWritePairs"/>/
/// <see cref="AgentContextCompactionFilters.DropSupersededObservationalPairs"/>. Both exist so a
/// turn below budget resends a byte-identical prefix across rounds instead of rewriting the
/// transcript on every single call — see the constants' own doc comments for the rationale.
/// </summary>
public sealed class AgentContextCompactionFiltersTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    // "custom_tool" is untouched by DropSupersededWritePairs/DropSupersededObservationalPairs/
    // CompressSupersededShellPairs (none of them key on it), so these rounds are only ever
    // affected by the pair-window gate under test.
    private static ChatMessage ToolCall(string callId, int i)
        => new(ChatRole.Assistant,
            [new FunctionCallContent(callId, "custom_tool", new Dictionary<string, object?> { ["i"] = i })]);

    private static ChatMessage ToolResult(string callId, string content)
        => new(ChatRole.Tool, [new FunctionResultContent(callId, content)]);

    private static List<ChatMessage> ToolRounds(int count, string resultText)
    {
        var messages = new List<ChatMessage>(count * 2);
        for (int i = 0; i < count; i++)
        {
            messages.Add(ToolCall($"c{i}", i));
            messages.Add(ToolResult($"c{i}", resultText));
        }
        return messages;
    }

    private static bool ContainsLiteralResult(IEnumerable<ChatMessage> messages, string callId, string expected)
        => messages
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Any(r => r.CallId == callId && (string?)r.Result == expected);

    // ── ApplyInTurnFilters: triggerChars gates KeepLastToolPairs ───────────────

    [Fact]
    public async Task BelowTrigger_LeavesPairSequenceUntouched_EvenPastMaxPairs()
    {
        // 5 rounds, well past maxInTurnToolPairs: 2 — but total content is tiny, so with a
        // huge triggerChars the 90% threshold is nowhere close and the window must not run.
        var messages = ToolRounds(5, "ok");

        var result = (await AgentContextCompactionFilters.ApplyInTurnFilters(
            messages, maxInTurnToolPairs: 2, maxInTurnChars: 0, triggerChars: 1_000_000)).ToList();

        Assert.Equal(messages.Count, result.Count);
        for (int i = 0; i < 5; i++)
            Assert.True(ContainsLiteralResult(result, $"c{i}", "ok"), $"expected c{i}'s result to survive untouched");
    }

    [Fact]
    public async Task AtOrAboveTrigger_CollapsesOldestPairs()
    {
        var messages = ToolRounds(5, "ok");

        // triggerChars small enough that even this tiny transcript clears 90% of it.
        var result = (await AgentContextCompactionFilters.ApplyInTurnFilters(
            messages, maxInTurnToolPairs: 2, maxInTurnChars: 0, triggerChars: 10)).ToList();

        Assert.True(result.Count < messages.Count,
            $"expected collapsing to reduce message count below {messages.Count}, got {result.Count}");
    }

    [Fact]
    public async Task TriggerCharsOmitted_CollapsesUnconditionally_MatchingPreviousBehavior()
    {
        // triggerChars defaults to 0 — callers that haven't been updated to supply a budget
        // must keep getting the old "always collapse past maxInTurnToolPairs" behavior.
        var messages = ToolRounds(5, "ok");

        var result = (await AgentContextCompactionFilters.ApplyInTurnFilters(
            messages, maxInTurnToolPairs: 2, maxInTurnChars: 0)).ToList();

        Assert.True(result.Count < messages.Count,
            $"expected the default (triggerChars: 0) to collapse unconditionally, got {result.Count} messages from {messages.Count}");
    }

    // ── DropSupersededObservationalPairs: minBatchChars batches the rewrite ────

    [Fact]
    public void ObservationalDrop_BelowBatchThreshold_LeavesSupersededResultUntouched()
    {
        var stale = "stale-result"; // far under any reasonable batch threshold
        var messages = new List<ChatMessage>
        {
            ToolCallNamed("c0", "read_file", "path", "a.txt"),
            ToolResult("c0", stale),
            ToolCallNamed("c1", "read_file", "path", "a.txt"),
            ToolResult("c1", "fresh-result"),
        };

        var result = AgentContextCompactionFilters
            .DropSupersededObservationalPairs(messages, minBatchChars: 65_536)
            .ToList();

        Assert.True(ContainsLiteralResult(result, "c0", stale),
            "expected the superseded result to survive untouched below the batch threshold");
    }

    [Fact]
    public void ObservationalDrop_AtOrAboveBatchThreshold_RewritesSupersededResult()
    {
        var stale = new string('x', 70_000); // clears the 65_536 default batch threshold
        var messages = new List<ChatMessage>
        {
            ToolCallNamed("c0", "read_file", "path", "a.txt"),
            ToolResult("c0", stale),
            ToolCallNamed("c1", "read_file", "path", "a.txt"),
            ToolResult("c1", "fresh-result"),
        };

        var result = AgentContextCompactionFilters
            .DropSupersededObservationalPairs(messages, minBatchChars: 65_536)
            .ToList();

        Assert.False(ContainsLiteralResult(result, "c0", stale),
            "expected the superseded result to be rewritten once accumulated staleness clears the batch threshold");
        // The fresh (non-superseded) result must never be touched.
        Assert.True(ContainsLiteralResult(result, "c1", "fresh-result"));
    }

    [Fact]
    public void ObservationalDrop_MinBatchCharsOmitted_RewritesEagerly_MatchingPreviousBehavior()
    {
        var stale = "stale-result";
        var messages = new List<ChatMessage>
        {
            ToolCallNamed("c0", "read_file", "path", "a.txt"),
            ToolResult("c0", stale),
            ToolCallNamed("c1", "read_file", "path", "a.txt"),
            ToolResult("c1", "fresh-result"),
        };

        var result = AgentContextCompactionFilters.DropSupersededObservationalPairs(messages).ToList();

        Assert.False(ContainsLiteralResult(result, "c0", stale),
            "expected the default (minBatchChars: 0) to rewrite as soon as anything is superseded");
    }

    // ── DropSupersededWritePairs: minBatchChars batches the rewrite ────────────

    [Fact]
    public void WriteDrop_BelowBatchThreshold_LeavesSupersededResultUntouched()
    {
        var stale = "old-write-result";
        var messages = new List<ChatMessage>
        {
            ToolCallNamed("c0", "write_file", "path", "a.txt"),
            ToolResult("c0", stale),
            ToolCallNamed("c1", "write_file", "path", "a.txt"),
            ToolResult("c1", "new-write-result"),
        };

        var result = AgentContextCompactionFilters
            .DropSupersededWritePairs(messages, minBatchChars: 65_536)
            .ToList();

        Assert.True(ContainsLiteralResult(result, "c0", stale),
            "expected the superseded write result to survive untouched below the batch threshold");
    }

    [Fact]
    public void WriteDrop_AtOrAboveBatchThreshold_RewritesSupersededResult()
    {
        var stale = new string('x', 70_000);
        var messages = new List<ChatMessage>
        {
            ToolCallNamed("c0", "write_file", "path", "a.txt"),
            ToolResult("c0", stale),
            ToolCallNamed("c1", "write_file", "path", "a.txt"),
            ToolResult("c1", "new-write-result"),
        };

        var result = AgentContextCompactionFilters
            .DropSupersededWritePairs(messages, minBatchChars: 65_536)
            .ToList();

        Assert.False(ContainsLiteralResult(result, "c0", stale),
            "expected the superseded write result to be rewritten once accumulated staleness clears the batch threshold");
    }

    private static ChatMessage ToolCallNamed(string callId, string toolName, string argKey, string argValue)
        => new(ChatRole.Assistant,
            [new FunctionCallContent(callId, toolName, new Dictionary<string, object?> { [argKey] = argValue })]);
}
