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

    // The shape of a live reply that patched a file and ran the tests in one go. Anthropic.SDK's stream kept only the last
    // tool_use block, so the patch was dropped, the tests kept failing, and the model concluded it had forgotten to patch.
    private const string TwoToolCallsStream = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_2","type":"message","role":"assistant","model":"claude-test","content":[],"stop_reason":null,"usage":{"input_tokens":10,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Fixing it."}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_a","name":"patch_file","input":{}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"path\": \"cart.py\", "}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"newText\": \"x\"}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: content_block_start
        data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_b","name":"shell_run","input":{}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"command\": \"make test\"}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":2}

        event: content_block_start
        data: {"type":"content_block_start","index":3,"content_block":{"type":"tool_use","id":"toolu_c","name":"git_status","input":{}}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":3}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":40}}

        event: message_stop
        data: {"type":"message_stop"}


        """;

    [Fact]
    public async Task GetStreamingResponseAsync_KeepsEveryToolCallInAReply()
    {
        var handler = new RecordingHandler(TwoToolCallsStream, "text/event-stream");
        using var client = NewClient(handler);
        var tools = new[] { "patch_file", "shell_run", "git_status" }
            .Select(n => (AITool)AIFunctionFactory.Create(() => "", n, n)).ToList();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync([new(ChatRole.User, "fix it")], new ChatOptions { Tools = tools }))
            updates.Add(u);

        var calls = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().ToList();
        Assert.Equal(["patch_file", "shell_run", "git_status"], calls.Select(c => c.Name));
        Assert.Equal(["toolu_a", "toolu_b", "toolu_c"], calls.Select(c => c.CallId));
        Assert.Equal("cart.py", calls[0].Arguments!["path"]!.ToString());
        Assert.Equal("make test", calls[1].Arguments!["command"]!.ToString());
        Assert.Empty(calls[2].Arguments!);
        Assert.Equal("Fixing it.", string.Concat(updates.SelectMany(u => u.Contents).OfType<TextContent>().Select(t => t.Text)));
    }

    private static AnthropicPromptCachingChatClient NewClient(RecordingHandler handler) =>
        new(new AnthropicClient("test-key", new HttpClient(handler)), "claude-default");

    private sealed class RecordingHandler(string reply, string mediaType = "application/json") : HttpMessageHandler
    {
        public JsonNode? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply, Encoding.UTF8, mediaType),
            };
        }
    }
}
