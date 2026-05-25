using System.Text;
using System.Text.Json.Nodes;

namespace fuseraft.Infrastructure;

/// <summary>
/// Strips the <c>strict</c> field from tool function definitions before sending to APIs
/// that don't support it (e.g. xAI). OpenAI SDK 2.x serialises <c>"strict": false</c>
/// on every function definition; providers that don't recognise the field return 400.
/// </summary>
internal sealed class FunctionStrictStripHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var stripped = StripFunctionStrict(body);
            if (!ReferenceEquals(stripped, body))
            {
                request.Content = new StringContent(stripped, Encoding.UTF8,
                    request.Content.Headers.ContentType?.MediaType ?? "application/json");
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private static string StripFunctionStrict(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            var tools = node?["tools"]?.AsArray();
            if (tools is null) return json;

            bool changed = false;
            foreach (var tool in tools)
            {
                var fn = tool?["function"]?.AsObject();
                if (fn is not null && fn.ContainsKey("strict"))
                {
                    fn.Remove("strict");
                    changed = true;
                }
            }

            return changed ? node!.ToJsonString() : json;
        }
        catch
        {
            return json; // pass through unchanged on any parse error
        }
    }
}
