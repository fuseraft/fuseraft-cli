using Microsoft.Extensions.AI;
using fuseraft.Core.Models;
using fuseraft.Orchestration;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="ToolResultWindowTrimmer"/> — both the original
/// <c>Apply</c> contract and the new <c>ApplyWithManifest</c> extension.
/// </summary>
public sealed class ToolResultWindowTrimmerTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ChatMessage ToolCall(string callId, string name,
        Dictionary<string, object?>? args = null)
        => new(ChatRole.Assistant,
            [new FunctionCallContent(callId, name, args)]);

    private static ChatMessage ToolResult(string callId, string content)
        => new(ChatRole.Tool,
            [new FunctionResultContent(callId, content)]);

    private static ContextBudgetConfig Budget(int maxTokens, int window = 1)
        => new() { MaxToolResultTokens = maxTokens, InTurnToolWindow = window };

    // ── Apply — existing contract (regression guard) ──────────────────────────

    [Fact]
    public void Apply_returns_same_reference_when_budget_not_exceeded()
    {
        var context = new List<ChatMessage>
        {
            ToolCall("c1", "read_file"),
            ToolResult("c1", new string('x', 100)),
        };
        var budget = Budget(maxTokens: 1_000);

        var result = ToolResultWindowTrimmer.Apply(context, budget);

        Assert.Same(context, result);
    }

    [Fact]
    public void Apply_tombstones_oldest_results_when_budget_exceeded()
    {
        // Two results, each ~250 tokens (1 000 chars / 4). Budget = 300 tokens,
        // window = 1 so the first result is evicted.
        var context = new List<ChatMessage>
        {
            ToolCall("c1", "read_file"),
            ToolResult("c1", new string('a', 1_000)),
            ToolCall("c2", "shell_run"),
            ToolResult("c2", new string('b', 1_000)),
        };
        var budget = Budget(maxTokens: 300, window: 1);

        var result = ToolResultWindowTrimmer.Apply(context, budget);

        var first  = result[1].Contents.OfType<FunctionResultContent>().Single();
        var second = result[3].Contents.OfType<FunctionResultContent>().Single();

        Assert.StartsWith(ToolResultWindowTrimmer.TombstonePrefix, first.Result?.ToString());
        Assert.DoesNotContain(ToolResultWindowTrimmer.TombstonePrefix, second.Result?.ToString() ?? "");
    }

    // ── Apply — item 3: enriched tombstone includes tool label ────────────────

    [Fact]
    public void Apply_tombstone_includes_tool_label_from_preceding_call()
    {
        var context = new List<ChatMessage>
        {
            ToolCall("c1", "read_file", new() { ["path"] = "src/Foo.cs" }),
            ToolResult("c1", new string('a', 1_000)),
            ToolCall("c2", "shell_run"),
            ToolResult("c2", new string('b', 200)),
        };

        var result = ToolResultWindowTrimmer.Apply(context, Budget(100, window: 1));

        var tombstone = result[1].Contents.OfType<FunctionResultContent>().Single().Result?.ToString();
        Assert.Contains("read_file(src/Foo.cs)", tombstone);
    }

    [Fact]
    public void Apply_tombstone_falls_back_to_call_id_when_no_preceding_call()
    {
        var context = new List<ChatMessage>
        {
            ToolResult("orphan", new string('a', 1_000)),
            ToolCall("c2", "shell_run"),
            ToolResult("c2", new string('b', 200)),
        };

        var result = ToolResultWindowTrimmer.Apply(context, Budget(100, window: 1));

        var tombstone = result[0].Contents.OfType<FunctionResultContent>().Single().Result?.ToString();
        Assert.Contains("orphan", tombstone);
    }

    // ── Apply — item 4: tombstone includes content preview ────────────────────

    [Fact]
    public void Apply_tombstone_includes_content_preview()
    {
        const string distinctStart = "UNIQUE_CONTENT_START";
        var context = new List<ChatMessage>
        {
            ToolCall("c1", "read_file"),
            ToolResult("c1", distinctStart + new string('x', 1_000)),
            ToolCall("c2", "shell_run"),
            ToolResult("c2", new string('b', 200)),
        };

        var result = ToolResultWindowTrimmer.Apply(context, Budget(100, window: 1));

        var tombstone = result[1].Contents.OfType<FunctionResultContent>().Single().Result?.ToString();
        Assert.Contains(distinctStart, tombstone);
        Assert.Contains("Preview:", tombstone);
    }

    [Fact]
    public void Apply_tombstone_truncates_preview_at_excerpt_limit()
    {
        // Content is much longer than ExcerptChars — tombstone must end with the ellipsis marker.
        var longContent = new string('z', 2_000);
        var context = new List<ChatMessage>
        {
            ToolCall("c1", "read_file"),
            ToolResult("c1", longContent),
            ToolCall("c2", "shell_run"),
            ToolResult("c2", new string('b', 200)),
        };

        var result = ToolResultWindowTrimmer.Apply(context, Budget(100, window: 1));

        var tombstone = result[1].Contents.OfType<FunctionResultContent>().Single().Result?.ToString();
        Assert.NotNull(tombstone);
        Assert.Contains("…", tombstone);
        // The full 2 000-char content must NOT appear verbatim in the tombstone.
        Assert.DoesNotContain(longContent, tombstone);
    }

    [Fact]
    public void Apply_tombstone_includes_re_read_hint()
    {
        // Every tombstone should guide the model toward targeted reads.
        var context = new List<ChatMessage>
        {
            ToolCall("c1", "read_file", new() { ["path"] = "src/Foo.cs" }),
            ToolResult("c1", new string('a', 1_000)),
            ToolCall("c2", "shell_run"),
            ToolResult("c2", new string('b', 200)),
        };

        var result = ToolResultWindowTrimmer.Apply(context, Budget(100, window: 1));

        var tombstone = result[1].Contents.OfType<FunctionResultContent>().Single().Result?.ToString();
        Assert.Contains("targeted ranges", tombstone);
    }

    // ── ApplyWithManifest — null manifest when nothing evicted ────────────────

    [Fact]
    public void ApplyWithManifest_returns_null_manifest_when_budget_not_exceeded()
    {
        var context = new List<ChatMessage>
        {
            ToolCall("c1", "read_file"),
            ToolResult("c1", new string('x', 40)),
        };

        var (messages, manifest) = ToolResultWindowTrimmer.ApplyWithManifest(context, Budget(1_000));

        Assert.Same(context, messages);
        Assert.Null(manifest);
    }

    [Fact]
    public void ApplyWithManifest_returns_null_manifest_when_budget_disabled()
    {
        var context = new List<ChatMessage>
        {
            ToolCall("c1", "read_file"),
            ToolResult("c1", new string('x', 10_000)),
        };

        var (messages, manifest) = ToolResultWindowTrimmer.ApplyWithManifest(context, Budget(0));

        Assert.Null(manifest);
    }

    // ── ApplyWithManifest — manifest content when evictions occur ─────────────

    [Fact]
    public void ApplyWithManifest_returns_non_null_manifest_when_evictions_occur()
    {
        var context = new List<ChatMessage>
        {
            ToolCall("c1", "read_file"),
            ToolResult("c1", new string('a', 1_000)),
            ToolCall("c2", "shell_run"),
            ToolResult("c2", new string('b', 1_000)),
        };

        var (_, manifest) = ToolResultWindowTrimmer.ApplyWithManifest(context, Budget(300, window: 1));

        Assert.NotNull(manifest);
    }

    [Fact]
    public void ApplyWithManifest_manifest_lists_superseded_call()
    {
        var context = new List<ChatMessage>
        {
            ToolCall("c1", "read_file"),
            ToolResult("c1", new string('a', 1_000)),
            ToolCall("c2", "shell_run"),
            ToolResult("c2", new string('b', 1_000)),
        };

        var (_, manifest) = ToolResultWindowTrimmer.ApplyWithManifest(context, Budget(300, window: 1));

        Assert.Contains("Superseded", manifest);
        Assert.Contains("read_file", manifest);
    }

    [Fact]
    public void ApplyWithManifest_manifest_lists_active_call()
    {
        var context = new List<ChatMessage>
        {
            ToolCall("c1", "read_file"),
            ToolResult("c1", new string('a', 1_000)),
            ToolCall("c2", "shell_run"),
            ToolResult("c2", new string('b', 1_000)),
        };

        var (_, manifest) = ToolResultWindowTrimmer.ApplyWithManifest(context, Budget(300, window: 1));

        Assert.Contains("Active tool results", manifest);
        Assert.Contains("shell_run", manifest);
    }

    // ── Label formatting ──────────────────────────────────────────────────────

    [Fact]
    public void ApplyWithManifest_formats_label_with_path_argument()
    {
        var context = new List<ChatMessage>
        {
            ToolCall("c1", "read_file", new() { ["path"] = "src/Foo.cs" }),
            ToolResult("c1", new string('a', 1_000)),
            ToolCall("c2", "read_file"),
            ToolResult("c2", new string('b', 200)),
        };

        var (_, manifest) = ToolResultWindowTrimmer.ApplyWithManifest(context, Budget(100, window: 1));

        Assert.Contains("read_file(src/Foo.cs)", manifest);
    }

    [Fact]
    public void ApplyWithManifest_formats_label_with_command_argument()
    {
        var context = new List<ChatMessage>
        {
            ToolCall("c1", "shell_run", new() { ["command"] = "dotnet build" }),
            ToolResult("c1", new string('a', 1_000)),
            ToolCall("c2", "shell_run"),
            ToolResult("c2", new string('b', 200)),
        };

        var (_, manifest) = ToolResultWindowTrimmer.ApplyWithManifest(context, Budget(100, window: 1));

        Assert.Contains("shell_run(dotnet build)", manifest);
    }

    [Fact]
    public void ApplyWithManifest_truncates_long_argument_in_label()
    {
        var longPath = new string('z', 80);
        var context = new List<ChatMessage>
        {
            ToolCall("c1", "read_file", new() { ["path"] = longPath }),
            ToolResult("c1", new string('a', 1_000)),
            ToolCall("c2", "read_file"),
            ToolResult("c2", new string('b', 200)),
        };

        var (_, manifest) = ToolResultWindowTrimmer.ApplyWithManifest(context, Budget(100, window: 1));

        Assert.NotNull(manifest);
        // Label must be truncated — the full 80-char path should not appear verbatim
        Assert.DoesNotContain(longPath, manifest);
        Assert.Contains("read_file(", manifest);
        Assert.Contains("…", manifest);
    }

    [Fact]
    public void ApplyWithManifest_falls_back_to_call_id_when_no_matching_call_in_context()
    {
        // ToolResult with no preceding ToolCall in this slice — fallback to callId.
        var context = new List<ChatMessage>
        {
            ToolResult("orphan-call", new string('a', 1_000)),
            ToolCall("c2", "shell_run"),
            ToolResult("c2", new string('b', 200)),
        };

        var (_, manifest) = ToolResultWindowTrimmer.ApplyWithManifest(context, Budget(100, window: 1));

        Assert.NotNull(manifest);
        Assert.Contains("orphan-call", manifest);
    }

    // ── ApplyWithManifest — all results evicted (window = 0) ──────────────────

    [Fact]
    public void ApplyWithManifest_manifest_with_all_results_evicted_shows_only_superseded()
    {
        // window = 0 retains nothing — every result is evicted once budget is exceeded.
        var context = new List<ChatMessage>
        {
            ToolCall("c1", "read_file"),
            ToolResult("c1", new string('a', 1_000)),
            ToolCall("c2", "shell_run"),
            ToolResult("c2", new string('b', 1_000)),
        };

        var (_, manifest) = ToolResultWindowTrimmer.ApplyWithManifest(context, Budget(100, window: 0));

        Assert.NotNull(manifest);
        Assert.Contains("Superseded", manifest);
        Assert.DoesNotContain("Active tool results", manifest);
    }

    // ── Apply — returns same reference when budget disabled ───────────────────

    [Fact]
    public void Apply_returns_same_reference_when_budget_disabled()
    {
        var context = new List<ChatMessage>
        {
            ToolCall("c1", "read_file"),
            ToolResult("c1", new string('x', 10_000)),
        };

        var result = ToolResultWindowTrimmer.Apply(context, Budget(0));

        Assert.Same(context, result);
    }
}
