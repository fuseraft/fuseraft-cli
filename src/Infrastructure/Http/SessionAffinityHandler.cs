using fuseraft.Infrastructure.Context;

namespace fuseraft.Infrastructure;

/// <summary>
/// Names the session on each request so a gateway or provider can send its requests to the server that holds its
/// prompt cache: <c>x-litellm-session-id</c>, which LiteLLM's session affinity pins a deployment by, and, for xAI,
/// <c>x-grok-conv-id</c>, which xAI routes by. A cache entry lives on the server that wrote it, so requests spread
/// across servers each start cold. Providers that don't know a header ignore it.
/// </summary>
internal sealed class SessionAffinityHandler : DelegatingHandler
{
    internal const string LiteLlmHeader = "x-litellm-session-id";
    internal const string XaiHeader = "x-grok-conv-id";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (RequestSession.Id.Value is { Length: > 0 } session)
        {
            request.Headers.TryAddWithoutValidation(LiteLlmHeader, session);
            if (IsXai(request.RequestUri))
                request.Headers.TryAddWithoutValidation(XaiHeader, session);
        }
        return base.SendAsync(request, cancellationToken);
    }

    private static bool IsXai(Uri? uri) =>
        uri?.Host is { } host
        && (host.Equals("x.ai", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".x.ai", StringComparison.OrdinalIgnoreCase));
}
