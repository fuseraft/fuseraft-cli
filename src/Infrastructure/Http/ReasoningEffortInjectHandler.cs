using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace fuseraft.Infrastructure;

/// <summary>
/// Injects a <c>"reasoning": {"effort": "..."}</c> object into outgoing chat completion
/// requests for models configured with <see cref="fuseraft.Core.Models.ModelConfig.ReasoningEffort"/>.
///
/// <para>
/// The xAI API (grok-4.3+) controls reasoning depth via a top-level <c>reasoning</c>
/// object in the request body. The OpenAI SDK has no first-class abstraction for this
/// parameter, so it is injected at the HTTP layer before the request is sent.
/// </para>
///
/// <para>
/// The handler reads the <c>model</c> field from the JSON body and looks it up in
/// <paramref name="modelEfforts"/> — a dictionary populated by
/// <see cref="fuseraft.Infrastructure.ChatClientFactory"/> as clients are created.
/// Requests for models without a registered effort are passed through unchanged.
/// </para>
/// </summary>
internal sealed class ReasoningEffortInjectHandler(
    ConcurrentDictionary<string, string> modelEfforts) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null && modelEfforts.Count > 0)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var injected = TryInjectReasoning(body);
            if (!ReferenceEquals(injected, body))
                request.Content = new StringContent(injected, Encoding.UTF8,
                    request.Content.Headers.ContentType?.MediaType ?? "application/json");
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private string TryInjectReasoning(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            var model = node?["model"]?.GetValue<string>();
            if (model is null || !modelEfforts.TryGetValue(model, out var effort)) return json;
            if (node!["reasoning"] is not null) return json; // already set by caller
            node["reasoning"] = new JsonObject { ["effort"] = effort };
            return node.ToJsonString();
        }
        catch { return json; } // never let injection crash the request pipeline
    }
}
