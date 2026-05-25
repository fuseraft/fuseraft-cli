using System.Text;
using System.Text.Json.Nodes;

namespace fuseraft.Infrastructure;

/// <summary>
/// Strips the <c>name</c> field from non-user messages before sending to APIs
/// that only allow <c>name</c> on <c>user</c> role messages (e.g. xAI).
/// MAF sets <c>name</c> on assistant messages for agent identification, which
/// causes a 400 on strict providers.
/// </summary>
internal sealed class MessageNameStripHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var stripped = StripNonUserNames(body);
            if (!ReferenceEquals(stripped, body))
            {
                request.Content = new StringContent(stripped, Encoding.UTF8,
                    request.Content.Headers.ContentType?.MediaType ?? "application/json");
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private static string StripNonUserNames(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            var messages = node?["messages"]?.AsArray();
            if (messages is null) return json;

            bool changed = false;
            foreach (var msg in messages)
            {
                var role = msg?["role"]?.GetValue<string>();
                if (role != "user" && msg?.AsObject().ContainsKey("name") == true)
                {
                    msg.AsObject().Remove("name");
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
