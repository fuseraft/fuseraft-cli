using System.Text;
using System.Text.Json.Nodes;

namespace fuseraft.Infrastructure;

/// <summary>
/// Normalizes empty or missing <c>finish_reason</c> values in chat completion responses.
/// Some providers (e.g. xAI reasoning models) return <c>"finish_reason": ""</c> on intermediate
/// or reasoning-only choices. The OpenAI SDK's deserializer throws
/// <see cref="ArgumentOutOfRangeException"/> on any value it doesn't recognise, including the
/// empty string. This handler rewrites <c>""</c> to <c>"stop"</c> so the SDK can proceed.
/// </summary>
internal sealed class FinishReasonNormalizerHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.Content is null) return response;

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return response;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var patched = PatchFinishReason(body);
        if (ReferenceEquals(patched, body)) return response;

        response.Content = new StringContent(patched, Encoding.UTF8,
            response.Content.Headers.ContentType?.MediaType ?? "application/json");
        return response;
    }

    private static string PatchFinishReason(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            var choices = node?["choices"]?.AsArray();
            if (choices is null) return json;

            bool changed = false;
            foreach (var choice in choices)
            {
                var fr = choice?["finish_reason"];
                if (fr is not null && fr.GetValueKind() == System.Text.Json.JsonValueKind.String
                    && string.IsNullOrEmpty(fr.GetValue<string>()))
                {
                    choice!.AsObject()["finish_reason"] = JsonNode.Parse("\"stop\"");
                    changed = true;
                }
            }

            return changed ? node!.ToJsonString() : json;
        }
        catch
        {
            return json;
        }
    }
}
