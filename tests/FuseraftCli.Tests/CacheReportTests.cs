using System.Text.Json;
using fuseraft.Cli.Commands.Log;

namespace FuseraftCli.Tests;

public class CacheReportTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private static JsonElement Event(string type, object payload, double minutes = 0) =>
        JsonSerializer.SerializeToElement(new
        {
            ts = Start.AddMinutes(minutes).ToString("O"),
            session = "abc12345",
            event_type = type,
            payload,
        });

    private static JsonElement Request(
        long prompt, long cached, int previous, string? cause = null, double minutes = 0, string? run = null, long written = 0) =>
        Event("model_response", new
        {
            model = "claude-sonnet-5",
            prompt_tokens = prompt,
            cached_input_tokens = cached,
            cache_write_tokens = written,
            prefix_change_cause = cause,
            previous_message_count = previous,
            run,
        }, minutes);

    [Fact]
    public void Totals_AddUpTheSessionsRequests()
    {
        var report = CacheReport.Analyze(
        [
            Event("session_start", new { model = "claude-sonnet-5" }),
            Request(10_000, 0, 0, written: 9_000),
            Request(12_000, 10_000, 2, minutes: 1),
            Event("turn_end", new { }),
        ]);

        Assert.Equal((2, 1, 22_000L, 10_000L, 9_000L), (report.Requests, report.Turns, report.PromptTokens, report.CachedTokens, report.WrittenTokens));
        Assert.Equal("claude-sonnet-5", report.Model);
    }

    [Fact]
    public void Misses_AreSortedByWhy()
    {
        var report = CacheReport.Analyze(
        [
            Request(10_000, 0, 0),                                          // first request
            Request(11_000, 1_000, 2, minutes: 1),                          // nothing changed
            Request(12_000, 1_000, 4, minutes: 10),                         // idle over 5 min
            Request(13_000, 1_000, 6, cause: "stale_drop", minutes: 11),
            Request(14_000, 1_000, 8, cause: "in_turn_trim", minutes: 12),
            Request(15_000, 1_000, 10, cause: "tools", minutes: 13),
            Request(16_000, 1_000, 12, cause: "overflow_trim", minutes: 14),
            Request(17_000, 1_000, 14, cause: "history", minutes: 15),
            Request(18_000, 17_000, 16, minutes: 16),                       // a hit
        ]);

        Assert.Equal(8, report.MissCount);
        foreach (var reason in new[]
                 {
                     CacheReport.FirstRequest, CacheReport.Unchanged, CacheReport.Idle, CacheReport.StaleDrop,
                     CacheReport.InTurnTrim, CacheReport.Tools, CacheReport.Overflow, CacheReport.History,
                 })
            Assert.Equal(1, report.Misses[reason].Count);
        Assert.Equal(10_000, report.Misses[CacheReport.FirstRequest].Uncached);
    }

    [Fact]
    public void HistoryRewrite_AfterACompaction_IsCountedAsCompaction()
    {
        var report = CacheReport.Analyze(
        [
            Request(10_000, 0, 0),
            Request(90_000, 80_000, 2, minutes: 1),
            Request(95_000, 90_000, 4, minutes: 2),
            Event("compaction", new { before_tokens = 112_000, after_tokens = 20_000 }, minutes: 3),
            Request(22_000, 2_000, 6, cause: "history", minutes: 3),
            Event("history_trimmed", new { messages_removed = 4 }, minutes: 4),
            Request(30_000, 2_000, 4, cause: "history", minutes: 4),
        ]);

        Assert.Equal(1, report.Misses[CacheReport.Compaction].Count);
        Assert.Equal(1, report.Misses[CacheReport.HistoryTrim].Count);
        Assert.Equal((1, 112_000L), (report.Compactions, report.CompactedFromTokens));
        Assert.Equal([3], report.RequestsPerCompaction);
    }

    [Fact]
    public void Subagents_AreTrackedApartFromTheMainThread()
    {
        var report = CacheReport.Analyze(
        [
            Request(10_000, 9_000, 2),
            Request(4_000, 0, 0, run: "r1", minutes: 1),
            Request(5_000, 4_000, 2, run: "r1", minutes: 7),   // the main thread's clock, not the subagent's, would say idle
        ]);

        Assert.Equal((1, 2), (report.Requests, report.SubagentRequests));
        Assert.Equal(1, report.Misses[CacheReport.FirstRequest].Count);
        Assert.False(report.Misses.ContainsKey(CacheReport.Idle));
    }

    [Fact]
    public void SmallPrompts_AreNotMisses()
    {
        Assert.Equal(0, CacheReport.Analyze([Request(800, 0, 0)]).MissCount);
    }

    [Fact]
    public void OlderLogs_WithoutCacheFields_AreUnmeasured()
    {
        var report = CacheReport.Analyze([Event("model_response", new { model = "grok-4.7", input_tokens = 9_000 })]);

        Assert.Equal((1, 0L), (report.Unmeasured, report.PromptTokens));
        Assert.Contains("No cache data", report.Render());
    }

    [Fact]
    public void Combine_AddsSessionsTogether()
    {
        var one = CacheReport.Analyze([Request(10_000, 0, 0)]);
        var two = CacheReport.Analyze([Request(20_000, 0, 0), Request(21_000, 20_000, 2, minutes: 1)]);

        var total = CacheReport.Combine([one, two]);

        Assert.Equal((2, 3, 51_000L), (total.Sessions, total.Requests, total.PromptTokens));
        Assert.Equal(2, total.Misses[CacheReport.FirstRequest].Count);
    }

    [Fact]
    public void Render_StatesTheBudgetAndWhereItCompacts()
    {
        var text = CacheReport.Analyze([Request(10_000, 0, 0)]).Render((150_000, "estimated from the model name"), 0.75);

        Assert.Contains("150K (estimated from the model name) · compacts at 75% (112.5K)", text);
        Assert.Contains("first request", text);
    }
}
