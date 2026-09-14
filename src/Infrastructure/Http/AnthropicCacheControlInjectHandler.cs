using System.Text;
using System.Text.Json.Nodes;

namespace fuseraft.Infrastructure;

/// <summary>
/// Adds Anthropic-style <c>cache_control: {"type": "ephemeral"}</c> breakpoints to outgoing
/// OpenAI Chat Completions requests for Claude-family models reached through an
/// OpenAI-compatible gateway (e.g. LiteLLM fronting AWS Bedrock) instead of the native
/// Anthropic Messages API.
///
/// <para>
/// Unlike OpenAI/xAI/DeepSeek — whose caching is automatic and only needs recovering on the
/// response side (see <see cref="fuseraft.Infrastructure.Chat.CacheUsageBackfillChatClient"/>) —
/// Anthropic-family caching via Bedrock is opt-in per request: the caller must mark specific
/// content blocks with <c>cache_control</c>, which LiteLLM translates into Bedrock's native
/// <c>cachePoint</c> format. Without this marker there is nothing for
/// <c>CacheUsageBackfillChatClient</c> to recover — the provider never caches anything in the
/// first place, confirmed against LiteLLM's own docs
/// (https://docs.litellm.ai/docs/completion/prompt_caching).
/// </para>
///
/// <para>
/// Mirrors <see cref="fuseraft.Infrastructure.Chat.AnthropicPromptCachingChatClient"/>'s
/// system + tail-of-history breakpoints (skipping the tool-definition breakpoint — LiteLLM's
/// docs don't confirm <c>cache_control</c> is honored on OpenAI-format tool/function
/// definitions, so this only touches message content, which is documented and was verified
/// live against a captured request: fuseraft's OpenAI-compatible client serializes plain-text
/// message content as a bare JSON string, not a content-parts array, so a string is converted
/// into the single-part array form <c>cache_control</c> requires rather than just marking an
/// existing array's last item.
/// </para>
///
/// <para>
/// Scoped to requests whose JSON body's <c>model</c> field contains "claude" (case-insensitive)
/// and whose path looks like a Chat Completions call — the native Anthropic Messages API
/// (already cached at the object-model level by <c>AnthropicPromptCachingChatClient</c>) posts
/// to a differently-shaped <c>/messages</c> path and is left untouched, and Ollama never reaches
/// this handler at all (it uses <c>OllamaApiClient</c> directly, bypassing this HTTP pipeline).
/// </para>
/// </summary>
internal sealed class AnthropicCacheControlInjectHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null
            && request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) == true)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var injected = TryInjectCacheControl(body);
            if (!ReferenceEquals(injected, body))
                request.Content = new StringContent(injected, Encoding.UTF8,
                    request.Content.Headers.ContentType?.MediaType ?? "application/json");
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private static string TryInjectCacheControl(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            var model = node?["model"]?.GetValue<string>();
            if (model is null || !model.Contains("claude", StringComparison.OrdinalIgnoreCase)) return json;
            if (node!["messages"] is not JsonArray { Count: > 0 } messages) return json;

            // Breakpoint 1: the last system message — covers the (usually large, static)
            // system prompt as a reusable prefix on its own.
            var lastSystem = messages.LastOrDefault(m => (string?)m?["role"] == "system") as JsonObject;
            var markedSystem = lastSystem is not null && MarkForCaching(lastSystem);

            // Breakpoint 2: the tail of the conversation — covers system + tools + the entire
            // history built so far as one growing prefix, same strategy as
            // AnthropicPromptCachingChatClient's extra breakpoint.
            var markedTail = MarkForCaching(messages[^1] as JsonObject);

            return markedSystem || markedTail ? node.ToJsonString() : json;
        }
        catch { return json; } // never let injection crash the request pipeline
    }

    // Marks a message's content for caching. String content (the common case for this SDK's
    // requests) is converted to the one-part array form cache_control requires; existing array
    // content gets the marker on its last item. No-op if already marked or nothing to attach to.
    private static bool MarkForCaching(JsonObject? message)
    {
        if (message is null) return false;
        var content = message["content"];

        if (content is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
        {
            message["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            });
            return true;
        }

        if (content is JsonArray { Count: > 0 } parts
            && parts[^1] is JsonObject block && block["cache_control"] is null)
        {
            block["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            return true;
        }

        return false;
    }
}
