using System.Text.Json;
using Microsoft.Extensions.AI;
using fuseraft.Infrastructure.Agents;
using fuseraft.Infrastructure.Chat;

namespace FuseraftCli.Tests;

/// <summary>The model_response event's usage fields, from <see cref="AgentMiddlewareBuilder"/>.</summary>
public class AgentMiddlewareBuilderModelResponseTests
{
    private static ChatResponseUpdate Usage(UsageDetails details) => new(ChatRole.Assistant, [new UsageContent(details)]);

    [Fact]
    public void MergeUsage_InputAndOutputOnSeparateUpdates_KeepsBoth()
    {
        // Anthropic streams the prompt's counts at the start and the output count at the end.
        var merged = AgentMiddlewareBuilder.MergeUsage(Usage(new UsageDetails
        {
            InputTokenCount  = 300,
            AdditionalCounts = new() { ["CacheCreationInputTokens"] = 1_200, [CacheUsageBackfillChatClient.CacheReadInputTokensKey] = 58_000 },
        }), null);
        merged = AgentMiddlewareBuilder.MergeUsage(new ChatResponseUpdate(ChatRole.Assistant, "text"), merged);
        merged = AgentMiddlewareBuilder.MergeUsage(Usage(new UsageDetails { OutputTokenCount = 40 }), merged);

        Assert.Equal(300, merged!.InputTokenCount);
        Assert.Equal(40, merged.OutputTokenCount);
        Assert.Equal(59_500, merged.PromptTokens());
    }

    [Fact]
    public void MergeUsage_NoUsageReported_IsNull()
    {
        Assert.Null(AgentMiddlewareBuilder.MergeUsage(new ChatResponseUpdate(ChatRole.Assistant, "text"), null));
    }

    [Fact]
    public void ModelResponsePayload_ReportsCacheReadsWritesAndThePrefixChange()
    {
        var usage = new UsageDetails
        {
            InputTokenCount  = 60_000,
            OutputTokenCount = 200,
            AdditionalCounts = new()
            {
                [CacheUsageBackfillChatClient.CacheReadInputTokensKey]  = 52_000,
                [CacheUsageBackfillChatClient.CacheWriteInputTokensKey] = 7_500,
            },
        };

        var json = JsonSerializer.SerializeToElement(AgentMiddlewareBuilder.ModelResponsePayload(
            "claude-sonnet-5", ChatFinishReason.ToolCalls, usage, callSeq: 7,
            new RequestChange(ChangedAt: 12, PreviousCount: 40, Cause: PrefixChangeCause.History, ToolsChanged: false),
            attempt: 0, run: "ab12cd34"));

        Assert.Equal(60_000, json.GetProperty("prompt_tokens").GetInt64());
        Assert.Equal(52_000, json.GetProperty("cached_input_tokens").GetInt64());
        Assert.Equal(7_500, json.GetProperty("cache_write_tokens").GetInt64());
        Assert.Equal(12, json.GetProperty("prefix_changed_at").GetInt32());
        Assert.Equal(40, json.GetProperty("previous_message_count").GetInt32());
        Assert.Equal("history", json.GetProperty("prefix_change_cause").GetString());
        Assert.Equal("ab12cd34", json.GetProperty("run").GetString());
    }
}
