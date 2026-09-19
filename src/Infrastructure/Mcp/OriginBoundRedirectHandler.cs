using System.Net;

namespace fuseraft.Infrastructure.Mcp;

/// <summary>
/// Follows HTTP redirects for an MCP HTTP transport, but never carries the server's configured
/// credentials to a different origin.
///
/// <para>
/// The MCP SDK sends <c>McpServerConfig.Headers</c> (<c>X-Api-Key</c>, <c>Authorization</c>, …)
/// as ordinary request headers. .NET's built-in redirect handling only strips
/// <c>Authorization</c> when a redirect changes host, so a custom header such as
/// <c>X-Api-Key</c> would be forwarded verbatim to wherever the server — or an open redirect in
/// front of it — points. This handler turns automatic redirects off on the inner handler and
/// follows them itself, dropping every configured header name (plus <c>Authorization</c>,
/// <c>Proxy-Authorization</c> and <c>Cookie</c>) from any hop whose origin (scheme, host, port)
/// differs from the one originally requested. Once dropped they stay dropped, even if a later
/// hop returns to the original origin. Same-origin redirects — <c>/mcp</c> → <c>/mcp/</c>,
/// http → https on the same host — keep working with their headers intact.
/// </para>
///
/// <para>
/// Redirect semantics follow browsers and .NET's default: 301/302 turn a POST into a GET and drop
/// the body, 303 turns anything but HEAD into a GET, 307/308 replay the method and body. A
/// redirect that would downgrade https to http is not followed (the 3xx is returned to the
/// caller, as .NET does), and at most <see cref="MaxRedirects"/> hops are followed.
/// </para>
/// </summary>
internal sealed class OriginBoundRedirectHandler : DelegatingHandler
{
    internal const int MaxRedirects = 10;

    private static readonly string[] AlwaysProtected = ["Authorization", "Proxy-Authorization", "Cookie"];

    private readonly HashSet<string> _protectedHeaders;

    /// <param name="configuredHeaderNames">Names of the headers configured for this server.</param>
    /// <param name="inner">
    /// The handler that actually sends requests. Must have automatic redirects disabled, or this
    /// handler never sees a redirect to follow. Defaults to a <see cref="SocketsHttpHandler"/>.
    /// </param>
    public OriginBoundRedirectHandler(IEnumerable<string> configuredHeaderNames, HttpMessageHandler? inner = null)
        : base(inner ?? new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        _protectedHeaders = new HashSet<string>(AlwaysProtected, StringComparer.OrdinalIgnoreCase);
        foreach (var name in configuredHeaderNames) _protectedHeaders.Add(name);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null)
            return await base.SendAsync(request, cancellationToken);

        var origin = OriginOf(request.RequestUri);

        // Buffered once so a 307/308 can replay it; MCP bodies are small JSON-RPC messages.
        byte[]? body = null;
        List<KeyValuePair<string, IEnumerable<string>>>? contentHeaders = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            contentHeaders = [.. request.Content.Headers];
        }

        var current = request;
        for (var hop = 0; ; hop++)
        {
            var response = await base.SendAsync(current, cancellationToken);

            if (!IsRedirect(response.StatusCode)
                || response.Headers.Location is not { } location
                || hop >= MaxRedirects)
                return response;

            var target = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
            if (IsSchemeDowngrade(current.RequestUri!, target))
                return response;

            response.Dispose();
            current = BuildRedirect(current, target, response.StatusCode, body, contentHeaders);

            if (OriginOf(target) != origin)
            {
                foreach (var name in _protectedHeaders)
                    current.Headers.Remove(name);
            }
        }
    }

    private static HttpRequestMessage BuildRedirect(
        HttpRequestMessage previous,
        Uri target,
        HttpStatusCode status,
        byte[]? body,
        List<KeyValuePair<string, IEnumerable<string>>>? contentHeaders)
    {
        var preserveMethod = status is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
        var method = preserveMethod || previous.Method == HttpMethod.Head || previous.Method == HttpMethod.Get
            ? previous.Method
            : HttpMethod.Get;

        var next = new HttpRequestMessage(method, target)
        {
            Version       = previous.Version,
            VersionPolicy = previous.VersionPolicy,
        };

        foreach (var header in previous.Headers)
            next.Headers.TryAddWithoutValidation(header.Key, header.Value);

        foreach (var option in (IDictionary<string, object?>)previous.Options)
            ((IDictionary<string, object?>)next.Options)[option.Key] = option.Value;

        if (preserveMethod && body is not null)
        {
            next.Content = new ByteArrayContent(body);
            foreach (var header in contentHeaders!)
                next.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return next;
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    internal static bool IsSchemeDowngrade(Uri from, Uri to) =>
        from.Scheme == Uri.UriSchemeHttps && to.Scheme == Uri.UriSchemeHttp;

    // (scheme, host, port) — the port is the effective one, so http://h and http://h:80 match.
    internal static (string Scheme, string Host, int Port) OriginOf(Uri uri) =>
        (uri.Scheme, uri.IdnHost.ToLowerInvariant(), uri.Port);
}
