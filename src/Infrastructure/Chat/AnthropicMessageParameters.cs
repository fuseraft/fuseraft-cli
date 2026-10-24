using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.SDK.Common;
using Anthropic.SDK.Extensions;
using Anthropic.SDK.Messaging;
using Microsoft.Extensions.AI;
using AiTextContent = Microsoft.Extensions.AI.TextContent;
using SdkTextContent = Anthropic.SDK.Messaging.TextContent;
using SdkTool = Anthropic.SDK.Common.Tool;
using OutputConfig = Anthropic.SDK.Messaging.OutputConfig;
using ToolResultContent = Anthropic.SDK.Messaging.ToolResultContent;

namespace fuseraft.Infrastructure.Chat;

/// <summary>
/// Converts an <see cref="IChatClient"/> request into Anthropic.SDK's native
/// <see cref="MessageParameters"/>. A port of Anthropic.SDK 5.10.0's
/// <c>ChatClientHelper.CreateMessageParameters</c>, which can't be called here: it was built
/// against Microsoft.Extensions.AI.Abstractions 10.3.0 and reads
/// <c>HostedMcpServerTool.AuthorizationToken</c>, which 10.10 removed (the token is now an
/// <c>Authorization</c> header). The runtime refuses to compile any method that references a
/// missing member, so every call threw <see cref="MissingMethodException"/> — whatever tools
/// the request had. Same behaviour otherwise; drop this once Anthropic.SDK ships a build
/// against the Microsoft.Extensions.AI version fuseraft uses.
/// </summary>
internal static class AnthropicMessageParameters
{
    public static MessageParameters Create(IChatClient client, IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var parameters = options?.RawRepresentationFactory?.Invoke(client) as MessageParameters ?? new MessageParameters();
        parameters.Messages ??= [];
        if (parameters.MaxTokens == 0) parameters.MaxTokens = 4096;

        if (options is not null) ApplyOptions(parameters, options);

        foreach (var chatMessage in messages)
        {
            if (chatMessage.Role == ChatRole.System)
            {
                (parameters.System ??= []).Add(new SystemMessage(string.Concat(chatMessage.Contents.OfType<AiTextContent>())));
                continue;
            }

            // A tool result has to go in a user message, so a message that mixes results with
            // other content is split wherever it switches between the two.
            Message? message = null;
            var inToolResults = false;
            foreach (var content in chatMessage.Contents)
            {
                var isToolResult = content is FunctionResultContent;
                if (message is null || inToolResults != isToolResult)
                {
                    message = new Message
                    {
                        Role    = !isToolResult && chatMessage.Role == ChatRole.Assistant ? RoleType.Assistant : RoleType.User,
                        Content = [],
                    };
                    parameters.Messages.Add(message);
                    inToolResults = isToolResult;
                }
                AddContent(message, content);
            }
        }

        parameters.Messages.RemoveAll(m => m.Content.Count == 0);
        if (!parameters.Messages.Any(m => m.Content.Count > 0))
            parameters.Messages.Add(new Message(RoleType.User, "​"));
        return parameters;
    }

    private static void ApplyOptions(MessageParameters parameters, ChatOptions options)
    {
        parameters.Model = options.ModelId;
        if (options.Instructions is { } instructions)
            (parameters.System ??= []).Add(new SystemMessage(instructions));
        if (options.MaxOutputTokens is { } maxOutputTokens) parameters.MaxTokens = maxOutputTokens;
        if (options.Temperature is { } temperature) parameters.Temperature = (decimal)temperature;
        if (options.TopP is { } topP) parameters.TopP = (decimal)topP;
        if (options.TopK is { } topK) parameters.TopK = topK;
        if (options.StopSequences is not null) parameters.StopSequences = [.. options.StopSequences];

        var jsonSchema = (options.ResponseFormat as ChatResponseFormatJson)?.Schema;
        var strict     = jsonSchema.HasValue || options.GetStrictToolsEnabled();

        if (options.Tools is { Count: > 0 })
        {
            parameters.ToolChoice ??= new ToolChoice();
            if (options.ToolMode is RequiredChatToolMode required)
            {
                parameters.ToolChoice.Type = required.RequiredFunctionName is null ? ToolChoiceType.Any : ToolChoiceType.Tool;
                parameters.ToolChoice.Name = required.RequiredFunctionName;
            }

            var tools = parameters.Tools ??= [];
            foreach (var tool in options.Tools)
            {
                switch (tool)
                {
                    case AIFunctionDeclaration function:
                        var schema = strict
                            ? ToStrictSchema(function.JsonSchema)
                            : JsonSerializer.SerializeToNode(function.JsonSchema.Deserialize<FunctionParameters>());
                        tools.Add(new SdkTool(new Function(function.Name, function.Description, schema)
                        {
                            Strict = strict ? true : null,
                        }));
                        break;
                    case HostedCodeInterpreterTool:
                        tools.Add(ServerTools.GetCodeExecutionTool());
                        break;
                    case HostedWebSearchTool:
                        tools.Add(ServerTools.GetWebSearchTool());
                        break;
                    case HostedMcpServerTool mcp:
                        var server = new MCPServer { Url = mcp.ServerAddress, Name = mcp.ServerName };
                        if (mcp.AllowedTools is not null) server.ToolConfiguration.AllowedTools.AddRange(mcp.AllowedTools);
                        server.AuthorizationToken = BearerToken(mcp.Headers);
                        (parameters.MCPServers ??= []).Add(server);
                        break;
                }
            }
        }

        if (options.GetWebFetchConfiguration() is { } webFetch)
            (parameters.Tools ??= []).Add(ServerTools.GetWebFetchTool(
                webFetch.MaxUses, webFetch.AllowedDomains, webFetch.BlockedDomains,
                webFetch.EnableCitations, webFetch.MaxContentTokens));

        var thinking = options.GetThinkingParameters();
        if (thinking is not null)
        {
            parameters.Thinking = thinking;
            if (thinking.Effort is { } effort)
                (parameters.OutputConfig ??= new OutputConfig()).Effort = effort;
        }
        else if (options.Reasoning?.Effort is { } reasoningEffort && reasoningEffort != ReasoningEffort.None)
        {
            parameters.Thinking = new ThinkingParameters { Type = ThinkingType.adaptive };
            ThinkingEffort? effort = reasoningEffort switch
            {
                ReasoningEffort.Low        => ThinkingEffort.low,
                ReasoningEffort.Medium     => ThinkingEffort.medium,
                ReasoningEffort.High       => ThinkingEffort.high,
                ReasoningEffort.ExtraHigh  => ThinkingEffort.max,
                _                          => null,
            };
            if (effort is not null)
                (parameters.OutputConfig ??= new OutputConfig()).Effort = effort;
        }

        if (jsonSchema is { } responseSchema)
            (parameters.OutputConfig ??= new OutputConfig()).OutputFormat = new OutputFormat
            {
                Type   = "json_schema",
                Schema = JsonDocument.Parse(ToStrictSchema(responseSchema)!.ToJsonString()).RootElement,
            };
    }

    private static void AddContent(Message message, AIContent content)
    {
        switch (content)
        {
            case FunctionResultContent result:
                message.Content.Add(new ToolResultContent
                {
                    ToolUseId = result.CallId,
                    Content   =
                    [
                        new SdkTextContent
                        {
                            Text = result.Result switch
                            {
                                string text => text,
                                null        => string.Empty,
                                var other   => JsonSerializer.Serialize(other),
                            },
                        },
                    ],
                    IsError = result.Exception is not null,
                });
                break;
            case TextReasoningContent reasoning when string.IsNullOrEmpty(reasoning.Text):
                message.Content.Add(new RedactedThinkingContent { Data = reasoning.ProtectedData });
                break;
            case TextReasoningContent reasoning:
                message.Content.Add(new ThinkingContent { Thinking = reasoning.Text, Signature = reasoning.ProtectedData });
                break;
            case AiTextContent text:
                // Anthropic rejects an assistant turn that ends in whitespace.
                var value = message.Role == RoleType.Assistant ? text.Text.TrimEnd() : text.Text;
                if (!string.IsNullOrWhiteSpace(value))
                    message.Content.Add(new SdkTextContent { Text = value });
                break;
            case DataContent data when data.HasTopLevelMediaType("image"):
                message.Content.Add(new ImageContent
                {
                    Source = new ImageSource { Data = Convert.ToBase64String(data.Data.ToArray()), MediaType = data.MediaType },
                });
                break;
            case DataContent data when data.HasTopLevelMediaType("application"):
                message.Content.Add(new DocumentContent
                {
                    Source = new DocumentSource { Data = Convert.ToBase64String(data.Data.ToArray()), MediaType = data.MediaType },
                });
                break;
            case FunctionCallContent call:
                message.Content.Add(new ToolUseContent
                {
                    Id    = call.CallId,
                    Name  = call.Name,
                    Input = JsonSerializer.SerializeToNode(call.Arguments),
                });
                break;
        }
    }

    private static string? BearerToken(IDictionary<string, string>? headers)
    {
        if (headers is null) return null;
        foreach (var (name, value) in headers)
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return value["Bearer ".Length..].Trim();
        return null;
    }

    // Anthropic's structured output requires additionalProperties: false on every object schema.
    private static JsonNode? ToStrictSchema(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText());
        if (node is JsonObject root) CloseObjects(root);
        return node;
    }

    private static void CloseObjects(JsonNode? node)
    {
        if (node is not JsonObject obj) return;
        if (obj.TryGetPropertyValue("type", out var type) && type is JsonValue typeValue
            && typeValue.TryGetValue<string>(out var typeName) && typeName == "object")
            obj["additionalProperties"] = false;

        if (obj.TryGetPropertyValue("properties", out var properties) && properties is JsonObject props)
            foreach (var (_, child) in props) CloseObjects(child);
        if (obj.TryGetPropertyValue("items", out var items))
            CloseObjects(items);
        foreach (var key in new[] { "$defs", "definitions" })
            if (obj.TryGetPropertyValue(key, out var defs) && defs is JsonObject defsObj)
                foreach (var (_, child) in defsObj) CloseObjects(child);
        foreach (var key in new[] { "anyOf", "oneOf", "allOf" })
            if (obj.TryGetPropertyValue(key, out var list) && list is JsonArray array)
                foreach (var child in array) CloseObjects(child);
    }

    // The non-strict tool schema keeps only type, required and properties, as the SDK does.
    private sealed class FunctionParameters
    {
        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string Type { get; set; } = "object";

        [System.Text.Json.Serialization.JsonPropertyName("required")]
        public List<string> Required { get; set; } = [];

        [System.Text.Json.Serialization.JsonPropertyName("properties")]
        public Dictionary<string, JsonElement> Properties { get; set; } = [];
    }
}
