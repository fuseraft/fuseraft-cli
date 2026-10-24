using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Anthropic.SDK;
using fuseraft.Infrastructure.Chat;
using Microsoft.Extensions.AI;

namespace FuseraftCli.Tests;

/// <summary>
/// End-to-end requests through <see cref="AnthropicPromptCachingChatClient"/> against a canned
/// Messages API. Anthropic.SDK's own request builder threw MissingMethodException on every call
/// once Microsoft.Extensions.AI moved to 10.10 (see <see cref="AnthropicMessageParameters"/>),
/// and nothing exercised this path, so the provider was broken without a failing test.
/// </summary>
public sealed class AnthropicPromptCachingChatClientTests
{
    private const string TextReply = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-test",
         "content":[{"type":"text","text":"hello there"}],
         "stop_reason":"end_turn","usage":{"input_tokens":12,"output_tokens":3}}
        """;

    [Fact]
    public async Task GetResponseAsync_SendsRequestAndReturnsReply()
    {
        var handler = new RecordingHandler(TextReply);
        using var client = NewClient(handler);

        var response = await client.GetResponseAsync(
            [new(ChatRole.System, "be brief"), new(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "claude-test", MaxOutputTokens = 100 });

        Assert.Equal("hello there", response.Text);
        var body = handler.Body!;
        Assert.Equal("claude-test", (string?)body["model"]);
        Assert.Equal(100, (int?)body["max_tokens"]);
        Assert.Contains("be brief", body["system"]!.ToJsonString());
        var message = Assert.Single(body["messages"]!.AsArray())!;
        Assert.Equal("user", (string?)message["role"]);
        Assert.Equal("ephemeral", (string?)message["content"]![0]!["cache_control"]!["type"]);
    }

    [Fact]
    public async Task GetResponseAsync_SendsToolsCallsAndResults()
    {
        var handler = new RecordingHandler(TextReply);
        using var client = NewClient(handler);
        var tool = AIFunctionFactory.Create((string path) => path, "read_file", "Reads a file.");

        await client.GetResponseAsync(
        [
            new(ChatRole.User, "read it"),
            new(ChatRole.Assistant, [new FunctionCallContent("call_1", "read_file", new Dictionary<string, object?> { ["path"] = "a.txt" })]),
            new(ChatRole.Tool, [new FunctionResultContent("call_1", "contents")]),
        ],
        new ChatOptions { ModelId = "claude-test", Tools = [tool] });

        var body = handler.Body!;
        Assert.Equal("read_file", (string?)body["tools"]![0]!["name"]);
        var messages = body["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        Assert.Equal("tool_use", (string?)messages[1]!["content"]![0]!["type"]);
        Assert.Equal("user", (string?)messages[2]!["role"]);
        Assert.Equal("tool_result", (string?)messages[2]!["content"]![0]!["type"]);
        Assert.Equal("call_1", (string?)messages[2]!["content"]![0]!["tool_use_id"]);
    }

    [Fact]
    public async Task GetResponseAsync_ReasoningEffortTurnsOnAdaptiveThinking()
    {
        var handler = new RecordingHandler(TextReply);
        using var client = NewClient(handler);

        await client.GetResponseAsync(
            [new(ChatRole.User, "think")],
            new ChatOptions { ModelId = "claude-test", Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } });

        Assert.Equal("adaptive", (string?)handler.Body!["thinking"]!["type"]);
        Assert.Equal("high", (string?)handler.Body!["output_config"]!["effort"]);
    }

    [Fact]
    public async Task GetResponseAsync_JsonSchemaResponseFormat_SendsClosedSchema()
    {
        var handler = new RecordingHandler(TextReply);
        using var client = NewClient(handler);
        var schema = System.Text.Json.JsonDocument.Parse(
            """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""").RootElement;

        await client.GetResponseAsync(
            [new(ChatRole.User, "name it")],
            new ChatOptions { ModelId = "claude-test", ResponseFormat = ChatResponseFormat.ForJsonSchema(schema) });

        var format = handler.Body!["output_config"]!["format"]!;
        Assert.Equal("json_schema", (string?)format["type"]);
        Assert.False((bool?)format["schema"]!["additionalProperties"]);
    }

    [Fact]
    public async Task GetResponseAsync_NoModelInOptions_SendsTheClientsModel()
    {
        var handler = new RecordingHandler(TextReply);
        using var client = NewClient(handler);

        await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Equal("claude-default", (string?)handler.Body!["model"]);
    }

    private static AnthropicPromptCachingChatClient NewClient(RecordingHandler handler) =>
        new(new AnthropicClient("test-key", new HttpClient(handler)), "claude-default");

    private sealed class RecordingHandler(string reply) : HttpMessageHandler
    {
        public JsonNode? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply, Encoding.UTF8, "application/json"),
            };
        }
    }
}
