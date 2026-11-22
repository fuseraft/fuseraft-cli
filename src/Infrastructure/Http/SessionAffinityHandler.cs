using System.Text.Json;
using fuseraft.Infrastructure.Context;

namespace fuseraft.Infrastructure;

/// <summary>
/// Names the session on each request so a gateway or provider can send its requests to the server that holds its
/// prompt cache: <c>x-litellm-session-id</c>, which LiteLLM's session affinity pins a deployment by, and, for xAI
/// or any Grok model behind a gateway, <c>x-grok-conv-id</c>, which Grok's cache routes by. A cache entry lives on
/// the server that wrote it, so requests spread across servers each start cold. Providers that don't know a header
/// ignore it.
/// </summary>
internal sealed class SessionAffinityHandler : DelegatingHandler
{
    internal const string LiteLlmHeader = "x-litellm-session-id";
    internal const string XaiHeader = "x-grok-conv-id";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (RequestSession.Id.Value is { Length: > 0 } session)
        {
            request.Headers.TryAddWithoutValidation(LiteLlmHeader, session);
            if (IsXai(request.RequestUri) || await IsGrokRequestAsync(request, cancellationToken))
                request.Headers.TryAddWithoutValidation(XaiHeader, session);
        }
        return await base.SendAsync(request, cancellationToken);
    }

    private static bool IsXai(Uri? uri) =>
        uri?.Host is { } host
        && (host.Equals("x.ai", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".x.ai", StringComparison.OrdinalIgnoreCase));

    private static async Task<bool> IsGrokRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is null) return false;
        var body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        return ModelOf(body)?.Contains("grok", StringComparison.OrdinalIgnoreCase) == true;
    }

    // Reads only as far as the top-level "model" field, since a request body can run to megabytes.
    internal static string? ModelOf(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isModel = reader.ValueTextEquals("model"u8);
                if (!reader.Read()) return null;
                if (isModel) return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                reader.Skip();
            }
        }
        catch (JsonException) { }
        return null;
    }
}
