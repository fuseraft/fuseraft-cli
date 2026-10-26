using System.Net;
using System.Text.RegularExpressions;
using fuseraft.Core.Models.Agents;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Infrastructure.Agents;

/// <summary>
/// Builds the <see cref="HttpClient"/> a <see cref="RemoteAgentConfig"/> agent speaks A2A through:
/// configured headers go only to the configured URL's origin, and are dropped on any redirect that
/// leaves it.
/// </summary>
internal static class RemoteAgentHttpClient
{
    /// <param name="inner">The handler that actually sends requests; must not follow redirects itself. Defaults to a <see cref="SocketsHttpHandler"/>.</param>
    public static HttpClient Create(RemoteAgentConfig config, HttpMessageHandler? inner = null)
    {
        var headers = config.Headers.ToDictionary(h => h.Key, h => ProcessHelper.ExpandEnvTokens(h.Value));
        HttpMessageHandler handler = new OriginBoundRedirectHandler(headers.Keys, inner);
        if (headers.Count > 0)
            handler = new OriginScopedHeadersHandler(new Uri(config.Url), headers, handler);

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds) };
    }

    /// <summary>The <c>${ENV_VAR}</c> tokens in a remote agent's headers whose variable isn't set, each with its header name.</summary>
    public static IReadOnlyList<(string Header, string Variable)> UnsetEnvTokens(RemoteAgentConfig config) =>
        [.. config.Headers
            .SelectMany(h => Regex.Matches(h.Value, @"\$\{([^}]+)\}").Select(m => (h.Key, m.Groups[1].Value)))
            .Where(t => Environment.GetEnvironmentVariable(t.Item2) is null)
            .Distinct()];

    /// <summary>
    /// The base the card path resolves against. The SDK resolves it relative to the URL, so without
    /// a trailing slash <c>http://h/agents/Dev</c> would fetch <c>/agents/.well-known/…</c>.
    /// </summary>
    public static Uri CardBaseUri(string url) => new(url.TrimEnd('/') + "/");

    /// <summary>
    /// A startup error for a card fetch that failed. The A2A SDK reports every HTTP failure as
    /// "HTTP request failed"; the status or connection error is on the inner exception.
    /// </summary>
    public static string DescribeCardFailure(string agentName, string url, Exception ex)
    {
        var cardUrl = new Uri(CardBaseUri(url), ".well-known/agent-card.json").ToString();
        var http = Unwrap(ex).OfType<HttpRequestException>().FirstOrDefault();
        var cause = http switch
        {
            { StatusCode: { } status } when status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"{(int)status} ({status}). Check RemoteAgent.Headers and the token it expands to.",
            { StatusCode: { } status } => $"{(int)status} ({status}).",
            not null => http.Message,
            null => ex.Message,
        };
        return $"Agent '{agentName}': could not fetch the A2A agent card from {cardUrl}: {cause}";

        static IEnumerable<Exception> Unwrap(Exception e)
        {
            for (Exception? x = e; x is not null; x = x.InnerException) yield return x;
        }
    }

    // The agent card names the endpoint the A2A client actually calls, and the card comes from the
    // remote side — a header attached client-wide would follow it to whatever origin it names.
    private sealed class OriginScopedHeadersHandler(
        Uri configuredUrl, IReadOnlyDictionary<string, string> headers, HttpMessageHandler inner)
        : DelegatingHandler(inner)
    {
        private readonly (string, string, int) _origin = OriginBoundRedirectHandler.OriginOf(configuredUrl);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is { } uri && OriginBoundRedirectHandler.OriginOf(uri) == _origin)
            {
                foreach (var (name, value) in headers)
                {
                    request.Headers.Remove(name);
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
