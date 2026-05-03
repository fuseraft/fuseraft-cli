using Microsoft.Extensions.AI;
using fuseraft.Core.Models;
using fuseraft.Orchestration;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="ContextWindowFilter"/>.
///
/// Tests use simple helper factories to build <see cref="ChatMessage"/> instances
/// whose role and content mirror the shapes produced by the orchestration loop:
/// <list type="bullet">
///   <item>User messages — task description, error injections, turn-boundary markers.</item>
///   <item>Text-bearing assistant messages — agent summaries and handoffs.</item>
///   <item>Tool-call-frame assistant messages — assistant messages with no text that
///     contain only a function-call request.</item>
///   <item>Tool-result messages — <c>ChatRole.Tool</c> result payloads.</item>
/// </list>
/// </summary>
public sealed class ContextWindowFilterTests
{
    // Helpers

    private static ChatMessage User(string text)
        => new(ChatRole.User, text);

    private static ChatMessage Text(string author, string text)
        => new(ChatRole.Assistant, text) { AuthorName = author };

    /// <summary>Simulates a tool-call frame: assistant message with empty text.</summary>
    private static ChatMessage ToolFrame(string author)
        => new(ChatRole.Assistant, "") { AuthorName = author };

    /// <summary>
    /// Simulates a mixed assistant message: text + a function-call in the same turn.
    /// This is the shape that causes orphaned tool_use ids when TextOnly strips the
    /// corresponding ChatRole.Tool result but keeps the full assistant message.
    /// </summary>
    private static ChatMessage MixedFrame(string author, string text)
        => new(ChatRole.Assistant,
            [
                new TextContent(text),
                new FunctionCallContent("call-id-1", "read_file",
                    new Dictionary<string, object?> { ["path"] = "brief.json" })
            ]) { AuthorName = author };

    /// <summary>Simulates a tool result payload.</summary>
    private static ChatMessage ToolResult(string result = "ok")
        => new(ChatRole.Tool, result);

    // Null / empty

    [Fact]
    public void Null_window_returns_history_unchanged()
    {
        var history = new List<ChatMessage> { User("task"), Text("Planner", "brief done") };

        var result = ContextWindowFilter.Apply(history, null);

        Assert.Equal(history, result);
    }

    [Fact]
    public void Empty_history_returns_empty()
    {
        var result = ContextWindowFilter.Apply([], new ContextWindowConfig { TextOnly = true });

        Assert.Empty(result);
    }

    // TextOnly

    [Fact]
    public void TextOnly_strips_tool_result_messages()
    {
        var history = new List<ChatMessage>
        {
            User("task"),
            ToolFrame("Developer"),
            ToolResult("pytest output"),
            Text("Developer", "HANDOFF TO TESTER"),
        };

        var result = ContextWindowFilter.Apply(history, new ContextWindowConfig { TextOnly = true });

        Assert.DoesNotContain(result, m => m.Role == ChatRole.Tool);
    }

    [Fact]
    public void TextOnly_strips_tool_call_frames()
    {
        var history = new List<ChatMessage>
        {
            User("task"),
            ToolFrame("Developer"),  // empty-text assistant message
            ToolResult("ok"),
            Text("Developer", "HANDOFF TO TESTER"),
        };

        var result = ContextWindowFilter.Apply(history, new ContextWindowConfig { TextOnly = true });

        // Only the User and the text-bearing assistant message should remain.
        Assert.All(result, m =>
            Assert.True(
                m.Role == ChatRole.User ||
                (m.Role == ChatRole.Assistant && !string.IsNullOrEmpty(m.Text)),
                $"Unexpected message role={m.Role} text='{m.Text}'"));
    }

    [Fact]
    public void TextOnly_preserves_text_bearing_assistant_messages()
    {
        var history = new List<ChatMessage>
        {
            Text("Planner", "here is the brief"),
            ToolFrame("Developer"),
            ToolResult("go test output"),
            Text("Developer", "HANDOFF TO TESTER"),
            Text("Tester", "HANDOFF TO REVIEWER"),
        };

        var result = ContextWindowFilter.Apply(history, new ContextWindowConfig { TextOnly = true });

        Assert.Contains(result, m => m.AuthorName == "Planner" && m.Text == "here is the brief");
        Assert.Contains(result, m => m.AuthorName == "Developer" && m.Text == "HANDOFF TO TESTER");
        Assert.Contains(result, m => m.AuthorName == "Tester" && m.Text == "HANDOFF TO REVIEWER");
    }

    [Fact]
    public void TextOnly_preserves_all_user_messages()
    {
        var history = new List<ChatMessage>
        {
            User("task description"),
            ToolFrame("Developer"),
            ToolResult("ok"),
            User("[fuseraft: Developer → Tester]"),
            Text("Tester", "HANDOFF TO REVIEWER"),
            User("[fuseraft: Tester → Reviewer]"),
        };

        var result = ContextWindowFilter.Apply(history, new ContextWindowConfig { TextOnly = true });

        Assert.Equal(3, result.Count(m => m.Role == ChatRole.User));
    }

    [Fact]
    public void TextOnly_false_with_no_other_filters_returns_full_history()
    {
        var history = new List<ChatMessage>
        {
            User("task"),
            ToolFrame("Developer"),
            ToolResult("output"),
            Text("Developer", "handoff"),
        };

        var result = ContextWindowFilter.Apply(history, new ContextWindowConfig { TextOnly = false });

        Assert.Equal(history.Count, result.Count);
    }

    // ExcludeAgents

    [Fact]
    public void ExcludeAgents_strips_named_agent_text_messages()
    {
        var history = new List<ChatMessage>
        {
            User("task"),
            Text("Planner", "brief"),
            Text("Developer", "summary"),
            Text("Tester", "HANDOFF TO REVIEWER"),
        };

        var result = ContextWindowFilter.Apply(history, new ContextWindowConfig
        {
            ExcludeAgents = ["Developer"]
        });

        Assert.DoesNotContain(result, m => m.AuthorName == "Developer");
        Assert.Contains(result, m => m.AuthorName == "Planner");
        Assert.Contains(result, m => m.AuthorName == "Tester");
    }

    [Fact]
    public void ExcludeAgents_is_case_insensitive()
    {
        var history = new List<ChatMessage>
        {
            Text("developer", "lower-case author"),
            Text("DEVELOPER", "upper-case author"),
            Text("Tester", "handoff"),
        };

        var result = ContextWindowFilter.Apply(history, new ContextWindowConfig
        {
            ExcludeAgents = ["Developer"]
        });

        Assert.DoesNotContain(result, m =>
            string.Equals(m.AuthorName, "developer", StringComparison.OrdinalIgnoreCase)
            && m.Role == ChatRole.Assistant);
        Assert.Contains(result, m => m.AuthorName == "Tester");
    }

    [Fact]
    public void ExcludeAgents_implicitly_strips_tool_results_to_avoid_orphans()
    {
        // Even without TextOnly, ExcludeAgents must strip ChatRole.Tool messages
        // because we can't tell which agent produced them.
        var history = new List<ChatMessage>
        {
            User("task"),
            ToolFrame("Developer"),
            ToolResult("pytest output"),
            Text("Developer", "summary"),
            Text("Tester", "HANDOFF TO REVIEWER"),
        };

        var result = ContextWindowFilter.Apply(history, new ContextWindowConfig
        {
            ExcludeAgents = ["Developer"]
        });

        Assert.DoesNotContain(result, m => m.Role == ChatRole.Tool);
        Assert.DoesNotContain(result, m => m.AuthorName == "Developer");
    }

    [Fact]
    public void ExcludeAgents_multiple_agents_all_stripped()
    {
        var history = new List<ChatMessage>
        {
            Text("Planner", "brief"),
            Text("Developer", "dev work"),
            Text("Tester", "test report"),
            Text("Reviewer", "reviewing"),
        };

        var result = ContextWindowFilter.Apply(history, new ContextWindowConfig
        {
            ExcludeAgents = ["Developer", "Tester"]
        });

        Assert.DoesNotContain(result, m => m.AuthorName == "Developer");
        Assert.DoesNotContain(result, m => m.AuthorName == "Tester");
        Assert.Contains(result, m => m.AuthorName == "Planner");
        Assert.Contains(result, m => m.AuthorName == "Reviewer");
    }

    // MaxTailMessages

    [Fact]
    public void MaxTailMessages_limits_to_last_N_messages()
    {
        var history = Enumerable.Range(1, 10)
            .Select(i => User($"msg {i}"))
            .ToList();

        var result = ContextWindowFilter.Apply(history, new ContextWindowConfig
        {
            MaxTailMessages = 3
        });

        Assert.Equal(3, result.Count);
        Assert.Equal("msg 8", result[0].Text);
        Assert.Equal("msg 9", result[1].Text);
        Assert.Equal("msg 10", result[2].Text);
    }

    [Fact]
    public void MaxTailMessages_of_zero_means_no_limit()
    {
        var history = Enumerable.Range(1, 20).Select(i => User($"msg {i}")).ToList();

        var result = ContextWindowFilter.Apply(history, new ContextWindowConfig
        {
            MaxTailMessages = 0
        });

        Assert.Equal(20, result.Count);
    }

    [Fact]
    public void MaxTailMessages_count_lte_limit_returns_all()
    {
        var history = new List<ChatMessage> { User("a"), User("b"), User("c") };

        var result = ContextWindowFilter.Apply(history, new ContextWindowConfig
        {
            MaxTailMessages = 10
        });

        Assert.Equal(3, result.Count);
    }

    // Combined

    [Fact]
    public void TextOnly_and_MaxTailMessages_combined()
    {
        // Build a history with tool noise + real messages. After TextOnly strips tool
        // messages, MaxTailMessages should act on the clean result.
        var history = new List<ChatMessage>
        {
            User("task"),
            ToolFrame("Developer"), ToolResult("r1"),
            Text("Developer", "dev summary"),
            ToolFrame("Tester"), ToolResult("r2"),
            Text("Tester", "tester handoff"),
            User("[fuseraft: Tester → Reviewer]"),
        };

        // After TextOnly: task, dev summary, tester handoff, boundary = 4 messages
        // MaxTailMessages = 2: tester handoff + boundary
        var result = ContextWindowFilter.Apply(history, new ContextWindowConfig
        {
            TextOnly = true,
            MaxTailMessages = 2
        });

        Assert.Equal(2, result.Count);
        Assert.Equal("tester handoff", result[0].Text);
        Assert.Equal("[fuseraft: Tester → Reviewer]", result[1].Text);
    }

    [Fact]
    public void ExcludeAgents_and_MaxTailMessages_combined()
    {
        var history = new List<ChatMessage>
        {
            User("task"),
            Text("Planner", "brief"),
            Text("Developer", "dev summary 1"),
            Text("Developer", "dev summary 2"),
            Text("Tester", "HANDOFF TO REVIEWER"),
            User("[fuseraft: Tester → Reviewer]"),
        };

        // Exclude Developer → removes 2 messages, leaving 4.
        // MaxTailMessages = 2 → keep last 2 of the 4.
        var result = ContextWindowFilter.Apply(history, new ContextWindowConfig
        {
            ExcludeAgents = ["Developer"],
            MaxTailMessages = 2
        });

        Assert.Equal(2, result.Count);
        Assert.Equal("HANDOFF TO REVIEWER", result[0].Text);
        Assert.Equal("[fuseraft: Tester → Reviewer]", result[1].Text);
    }

    [Fact]
    public void TextOnly_ExcludeAgents_MaxTailMessages_all_combined()
    {
        var history = new List<ChatMessage>
        {
            User("task"),
            Text("Planner", "brief"),
            ToolFrame("Developer"), ToolResult("tool noise"),
            Text("Developer", "dev summary"),
            ToolFrame("Tester"), ToolResult("more noise"),
            Text("Tester", "HANDOFF TO REVIEWER"),
            User("[fuseraft: Tester → Reviewer]"),
        };

        // TextOnly removes 4 tool messages → 5 remain: task, brief, dev summary, handoff, boundary
        // ExcludeAgents removes Developer → 4 remain: task, brief, handoff, boundary
        // MaxTailMessages = 2 → handoff + boundary
        var result = ContextWindowFilter.Apply(history, new ContextWindowConfig
        {
            TextOnly        = true,
            ExcludeAgents   = ["Developer"],
            MaxTailMessages = 2
        });

        Assert.Equal(2, result.Count);
        Assert.Equal("HANDOFF TO REVIEWER", result[0].Text);
        Assert.Equal("[fuseraft: Tester → Reviewer]", result[1].Text);
    }

    // Mixed messages (text + tool-call in one assistant turn)

    [Fact]
    public void TextOnly_strips_tool_call_content_from_mixed_messages_to_avoid_orphaned_tool_use_ids()
    {
        // Regression: a mixed assistant message (text + FunctionCallContent) must be
        // reduced to its text portion when TextOnly is true. Keeping the full message
        // while dropping the ChatRole.Tool result leaves orphaned tool_use ids and
        // causes HTTP 400 from strict providers such as Bedrock.
        var history = new List<ChatMessage>
        {
            User("task"),
            MixedFrame("Planner", "I'll read the files now"),
            ToolResult("file contents"),
            Text("Planner", "HANDOFF TO DEVELOPER"),
        };

        var result = ContextWindowFilter.Apply(history, new ContextWindowConfig { TextOnly = true });

        // Tool results must be gone.
        Assert.DoesNotContain(result, m => m.Role == ChatRole.Tool);

        // No assistant message may carry FunctionCallContent.
        Assert.All(result.Where(m => m.Role == ChatRole.Assistant), m =>
            Assert.DoesNotContain(m.Contents, c => c is FunctionCallContent));

        // The text portion of the mixed message must be preserved.
        Assert.Contains(result, m =>
            m.Role == ChatRole.Assistant && m.Text == "I'll read the files now");

        // The pure-text handoff is also preserved.
        Assert.Contains(result, m =>
            m.AuthorName == "Planner" && m.Text == "HANDOFF TO DEVELOPER");
    }

    [Fact]
    public void TextOnly_mixed_message_author_is_preserved_after_stripping_tool_calls()
    {
        var history = new List<ChatMessage>
        {
            MixedFrame("Developer", "Running tests now"),
            ToolResult("ok"),
        };

        var result = ContextWindowFilter.Apply(history, new ContextWindowConfig { TextOnly = true });

        var kept = Assert.Single(result, m => m.Role == ChatRole.Assistant);
        Assert.Equal("Developer", kept.AuthorName);
        Assert.Equal("Running tests now", kept.Text);
    }

    // Real-world scenario

    [Fact]
    public void TextOnly_dramatically_reduces_developer_tool_noise()
    {
        // Simulate a Developer turn that runs 10 shell commands (tool frame + result each)
        // plus two file writes, then emits a text summary. The Reviewer should only see
        // the Planner's brief discussion and the Developer's summary text.
        var history = new List<ChatMessage> { User("build a Go API") };

        history.Add(Text("Planner", "here is the brief"));
        history.Add(User("[fuseraft: Planner → Developer]"));

        // 10 shell commands × 2 messages + 2 file writes × 2 messages = 24 tool messages
        for (int i = 0; i < 12; i++)
        {
            history.Add(ToolFrame("Developer"));
            history.Add(ToolResult($"output {i}"));
        }
        history.Add(Text("Developer", "HANDOFF TO TESTER: all tests pass"));
        history.Add(User("[fuseraft: Developer → Tester]"));
        history.Add(Text("Tester", "HANDOFF TO REVIEWER"));
        history.Add(User("[fuseraft: Tester → Reviewer]"));

        var full   = history.Count;                  // 30 messages
        var result = ContextWindowFilter.Apply(history, new ContextWindowConfig { TextOnly = true });
        var clean  = result.Count;                   // should be 6: task + planner + boundary + dev text + tester text + boundary

        Assert.True(clean < full / 3,
            $"Expected significant reduction (got {clean}/{full} messages)");

        Assert.DoesNotContain(result, m => m.Role == ChatRole.Tool);
        Assert.Contains(result, m => m.AuthorName == "Planner");
        Assert.Contains(result, m => m.AuthorName == "Developer");
        Assert.Contains(result, m => m.AuthorName == "Tester");
    }
}
