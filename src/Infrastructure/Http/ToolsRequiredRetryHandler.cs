using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace fuseraft.Infrastructure;

/// <summary>
/// Detects the LiteLLM/Bedrock "tools= param required" 400 error and retries the request
/// with a no-op placeholder tool injected, matching what <c>litellm.modify_params = True</c>
/// does on the proxy side.
///
/// <para>
/// Bedrock requires the <c>tools</c> array to be present whenever any tool-calling-related
/// parameter is included in the request. When fuseraft-cli is pointed at a LiteLLM proxy
/// fronting Bedrock, and the proxy cannot be reconfigured, this handler intercepts the 400
/// and retries with a minimal dummy tool so the provider accepts the request.
/// </para>
///
/// <para>
/// The handler only retries when the request body contained no tools (empty or absent array).
/// If tools were already present the error has a different root cause and the original 400
/// is returned as-is.
/// </para>
/// </summary>
internal sealed class ToolsRequiredRetryHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Buffer the body before sending so we can patch and re-send on error.
        string? originalBody = null;
        string mediaType = "application/json";
        if (request.Content is not null)
        {
            mediaType = request.Content.Headers.ContentType?.MediaType ?? mediaType;
            originalBody = await request.Content.ReadAsStringAsync(cancellationToken);
            request.Content = new StringContent(originalBody, Encoding.UTF8, mediaType);
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode != HttpStatusCode.BadRequest || originalBody is null)
            return response;

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
        // Rebuild so the caller can still read the body.
        response.Content = new StringContent(errorBody, Encoding.UTF8,
            response.Content.Headers.ContentType?.MediaType ?? "application/json");

        if (!errorBody.Contains("tools=", StringComparison.Ordinal))
            return response;

        var patched = InjectNoOpTool(originalBody);
        if (patched is null)
            return response;

        Console.Error.WriteLine("[tools-retry] Bedrock/LiteLLM requires tools= — injecting no-op placeholder and retrying.");
        request.Content = new StringContent(patched, Encoding.UTF8, mediaType);
        return await base.SendAsync(request, cancellationToken);
    }

    private static string? InjectNoOpTool(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is null) return null;

            // Only inject when tools is absent or empty — if tools are already present
            // the error has a different root cause and we should not retry.
            if (node["tools"] is JsonArray existing && existing.Count > 0)
                return null;

            node["tools"] = new JsonArray { BuildNoOpTool() };
            return node.ToJsonString();
        }
        catch
        {
            return null;
        }
    }

    private static JsonNode BuildNoOpTool() =>
        JsonNode.Parse("""
        {
          "type": "function",
          "function": {
            "name": "no_op",
            "description": "Placeholder required by this provider.",
            "parameters": { "type": "object", "properties": {} }
          }
        }
        """)!;
}
