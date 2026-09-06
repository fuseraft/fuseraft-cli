using System.Text.Json;
using Microsoft.Extensions.AI;
using fuseraft.Infrastructure.Agents;

namespace FuseraftCli.Tests;

/// <summary>
/// Covers a bug in <c>AgentMiddlewareBuilder.TrimToolResultsToChars</c> (adaptive context-trim
/// stages 1–2): it only truncated <see cref="FunctionResultContent.Result"/> when the value was
/// a plain CLR <c>string</c> (<c>fr.Result is string s</c>). In practice the framework commonly
/// hands back a <see cref="JsonElement"/> instead (e.g. after any JSON round-trip, such as
/// checkpoint persistence) — the old check missed this entirely, silently turning stages 1–2
/// into no-ops and leaving stage 3 (drop everything) as the only adaptive-trim stage that
/// actually reduced anything. Confirmed live: a forced adaptive-trim run showed msgChars
/// completely unchanged across stages 1 and 2, only dropping once stage 3 fired.
/// </summary>
public sealed class AdaptiveTrimMessagesTests
{
    private const string CallId = "call-1";

    private static ChatMessage ToolMessageWith(object? result) =>
        new(ChatRole.Tool, [new FunctionResultContent(CallId, result)]);

    private static string ResultText(ChatMessage msg) =>
        ((FunctionResultContent)msg.Contents[0]).Result switch
        {
            string s => s,
            JsonElement je => je.GetString() ?? je.GetRawText(),
            var other => other?.ToString() ?? string.Empty,
        };

    [Fact]
    public void Stage1_TruncatesPlainStringResult()
    {
        var longResult = new string('x', 10_000);
        var messages = new List<ChatMessage> { ToolMessageWith(longResult) };

        var trimmed = AgentMiddlewareBuilder.AdaptiveTrimMessages(messages, stage: 1);

        var text = ResultText(trimmed[0]);
        Assert.True(text.Length < longResult.Length);
        Assert.Contains("context-trimmed", text);
    }

    [Fact]
    public void Stage1_TruncatesJsonElementStringResult()
    {
        // Simulates the common real-world shape: Result surviving as a JsonElement rather than
        // the original CLR string, e.g. after checkpoint persistence round-trips it through JSON.
        var longResult = new string('x', 10_000);
        var jsonResult = JsonSerializer.SerializeToElement(longResult);
        var messages = new List<ChatMessage> { ToolMessageWith(jsonResult) };

        var trimmed = AgentMiddlewareBuilder.AdaptiveTrimMessages(messages, stage: 1);

        var text = ResultText(trimmed[0]);
        Assert.True(text.Length < longResult.Length);
        Assert.Contains("context-trimmed", text);
    }

    [Fact]
    public void Stage2_TruncatesJsonElementStringResultTighterThanStage1()
    {
        var longResult = new string('x', 10_000);
        var jsonResult = JsonSerializer.SerializeToElement(longResult);
        var messages = new List<ChatMessage> { ToolMessageWith(jsonResult) };

        var stage1 = AgentMiddlewareBuilder.AdaptiveTrimMessages(messages, stage: 1);
        var stage2 = AgentMiddlewareBuilder.AdaptiveTrimMessages(messages, stage: 2);

        Assert.True(ResultText(stage2[0]).Length < ResultText(stage1[0]).Length);
    }

    [Fact]
    public void Stage1_LeavesShortJsonElementResultUnchanged()
    {
        var shortResult = "short result";
        var jsonResult = JsonSerializer.SerializeToElement(shortResult);
        var messages = new List<ChatMessage> { ToolMessageWith(jsonResult) };

        var trimmed = AgentMiddlewareBuilder.AdaptiveTrimMessages(messages, stage: 1);

        Assert.Equal(shortResult, ResultText(trimmed[0]));
    }

    [Fact]
    public void Stage1_FallsBackToToStringForNonStringJsonElement()
    {
        // A tool that returns something JSON-serializes to e.g. a number or object rather than
        // a string. ExtractResultText must not throw and must still measure/truncate sensibly.
        var jsonResult = JsonSerializer.SerializeToElement(new { count = 12345, data = new string('y', 10_000) });
        var messages = new List<ChatMessage> { ToolMessageWith(jsonResult) };

        var trimmed = AgentMiddlewareBuilder.AdaptiveTrimMessages(messages, stage: 1);

        var text = ResultText(trimmed[0]);
        Assert.True(text.Length <= 4_100); // 4000 cap + truncation-note overhead
    }

    [Fact]
    public void Stage3_DropsToolContentRegardlessOfResultType()
    {
        var jsonResult = JsonSerializer.SerializeToElement(new string('x', 10_000));
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent(CallId, "shell_run")]),
            ToolMessageWith(jsonResult),
        };

        var trimmed = AgentMiddlewareBuilder.AdaptiveTrimMessages(messages, stage: 3);

        Assert.DoesNotContain(trimmed, m => m.Role == ChatRole.Tool);
    }
}
