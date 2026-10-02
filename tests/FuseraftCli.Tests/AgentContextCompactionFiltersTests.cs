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

    // ── cacheStep: trimming past budget keeps most rounds' prefix stable ──────

    // What a provider's prefix cache compares: each message's role and contents, in order.
    private static string Fingerprint(ChatMessage m)
        => m.Role + ":" + string.Join("|", m.Contents.Select(c => c switch
        {
            FunctionCallContent fc   => $"call {fc.CallId} {fc.Name} {string.Join(",", fc.Arguments?.Select(kv => $"{kv.Key}={kv.Value}") ?? [])}",
            FunctionResultContent fr => $"result {fr.CallId} {fr.Result}",
            TextContent t            => $"text {t.Text}",
            _                        => c.GetType().Name,
        }));

    // Grows a transcript one tool round at a time, as a long REPL turn does, and counts the rounds
    // whose filtered request does NOT begin with the previous round's filtered request — each of
    // those is a prompt-cache miss on nearly the whole request.
    private static async Task<int> CountPrefixChanges(int rounds, int maxPairs, int maxChars, int cacheStep)
    {
        var transcript = new List<ChatMessage> { new(ChatRole.User, "do the task") };
        List<string>? previous = null;
        int changes = 0;
        for (int i = 0; i < rounds; i++)
        {
            transcript.Add(ToolCall($"c{i}", i));
            transcript.Add(ToolResult($"c{i}", new string((char)('a' + i % 26), 1_000)));

            var current = (await AgentContextCompactionFilters.ApplyInTurnFilters(
                    transcript, maxPairs, maxChars, triggerChars: 10, cacheStep: cacheStep))
                .Select(Fingerprint).ToList();
            if (previous is not null && !current.Take(previous.Count).SequenceEqual(previous))
                changes++;
            previous = current;
        }
        return changes;
    }

    [Fact]
    public async Task PairWindow_WithoutCacheStep_ChangesThePrefixEveryRoundPastTheWindow()
    {
        // Baseline the step exists to fix: a window held at exactly 4 slides on every round.
        Assert.True(await CountPrefixChanges(rounds: 40, maxPairs: 4, maxChars: 0, cacheStep: 1) >= 30);
    }

    [Fact]
    public async Task PairWindow_WithCacheStep_ChangesThePrefixOnlyOncePerStep()
    {
        Assert.True(await CountPrefixChanges(rounds: 40, maxPairs: 4, maxChars: 0, cacheStep: 8) <= 40 / 8);
    }

    [Fact]
    public async Task CharBudget_WithCacheStep_ChangesThePrefixOnlyOncePerStep()
    {
        // No pair window; 1k-char results against a 10k budget force the char trim on most rounds.
        Assert.True(await CountPrefixChanges(rounds: 40, maxPairs: 0, maxChars: 10_000, cacheStep: 1) >= 25);
        Assert.True(await CountPrefixChanges(rounds: 40, maxPairs: 0, maxChars: 10_000, cacheStep: 8) <= 40 / 8);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void CharBudget_StaysUnderBudgetWhateverTheStep(int step)
    {
        var messages = ToolRounds(30, new string('r', 1_000));

        var result = AgentContextCompactionFilters.TrimInTurnContext(messages, maxChars: 10_000, step: step).ToList();

        Assert.True(AgentContextCompactionFilters.EstimateTotalChars(result) <= 10_000);
    }

    [Theory]
    [InlineData(10, 12)]   // under the floor: the floor
    [InlineData(12, 12)]
    [InlineData(13, 13)]   // the window floats up to floor + step - 1 …
    [InlineData(19, 19)]
    [InlineData(20, 12)]   // … then drops back, collapsing a whole step of groups at once
    [InlineData(27, 19)]
    public void StableWindowSize_FloatsBetweenTheFloorAndAStepAboveIt(int groups, int expected)
    {
        // One user message plus tool rounds; a system message must not count as a group.
        var messages = new List<ChatMessage> { new(ChatRole.System, "sys"), new(ChatRole.User, "go") };
        messages.AddRange(ToolRounds(groups - 1, "ok"));

        Assert.Equal(expected, AgentContextCompactionFilters.StableWindowSize(messages, minPreserved: 12, step: 8));
    }
}
