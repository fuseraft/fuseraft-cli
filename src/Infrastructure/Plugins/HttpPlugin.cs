using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Gives agents the ability to make HTTP requests to external APIs and web pages.
///
/// When <paramref name="allowedHosts"/> is non-empty, every outbound request is validated
/// against the list before being sent. Requests to unlisted hosts — including loopback,
/// link-local, and RFC-1918 private ranges — are rejected, preventing SSRF attacks and
/// unintended data exfiltration.
///
/// When <paramref name="allowedHosts"/> is null or empty, all hosts are permitted (default).
///
/// Named API profiles (<paramref name="apiProfiles"/>) bundle a base URL and default headers
/// so agents can make authenticated calls without embedding credentials in their instructions.
/// </summary>
public sealed class HttpPlugin : IDisposable
{
    // Shared client for the no-arg constructor path — avoids a new socket per plugin instance.
    // This constructor path never allows private hosts (see the ctor below), so the callback
    // is wired to a fixed "never allow" policy rather than a live-read accessor.
    private static readonly HttpClient _defaultHttp = CreateDefaultClient();
    private static HttpClient CreateDefaultClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = CreateSsrfSafeConnectCallback(static () => false),
            AllowAutoRedirect = false,   // HttpPlugin follows redirects itself — see SendFollowingRedirectsAsync
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("fuseraft/1.0");
        return client;
    }

    /// <summary>
    /// Builds a <see cref="SocketsHttpHandler.ConnectCallback"/> that resolves the target host
    /// and validates it is not a private/loopback address in the same step as connecting to it.
    ///
    /// <para>
    /// A separate pre-check (resolve, validate, then let <see cref="HttpClient"/> connect on its
    /// own) leaves a DNS-rebinding TOCTOU window open: the attacker's DNS server can answer the
    /// validation lookup with a public IP and the connection's own independent lookup — issued
    /// moments later — with a private one, since nothing pins the two together. Overriding the
    /// connect step to do its own single resolution and validate exactly the address it is about
    /// to use closes that window; there is no second, independently-timed lookup for an attacker
    /// to race.
    /// </para>
    ///
    /// <para>
    /// <paramref name="allowPrivateHosts"/> is invoked on every call, not captured once, so a
    /// long-lived shared <see cref="HttpClient"/> can serve callers whose policy is only decided
    /// after the client is built (e.g. <c>PluginRegistry.Configure</c> runs after its shared
    /// client already exists).
    /// </para>
    /// </summary>
    internal static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> CreateSsrfSafeConnectCallback(
        Func<bool> allowPrivateHosts) =>
        async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;

            IPAddress address;
            if (IPAddress.TryParse(host, out var literal))
            {
                address = literal;
            }
            else
            {
                IPAddress[] resolved;
                try
                {
                    resolved = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException ex)
                {
                    throw new HttpRequestException($"Could not resolve host '{host}': {ex.Message}", ex);
                }

                // Fails closed on no addresses, same as ResolvesToPrivateAddressAsync's
                // unresolvable-host case — the request would fail for the same reason anyway.
                if (resolved.Length == 0)
                    throw new HttpRequestException($"Host '{host}' did not resolve to any address.");

                // First address only — deterministic and matches ordinary DNS-client behavior.
                // Picking a *different* address than the one just validated would reopen exactly
                // the gap this callback exists to close.
                address = resolved[0];
            }

            if (!allowPrivateHosts() && IsPrivateIp(address))
                throw new HttpRequestException(
                    $"Host '{host}' resolved to a private or loopback address ({address}) and was blocked.");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly ILogger<HttpPlugin>? _logger;

    // Null means unrestricted; non-null means enforce the allowlist.
    private readonly HashSet<string>? _allowedHosts;
    private readonly bool _allowPrivateHosts;

    // Named API profiles — null when no profiles are configured.
    private readonly IReadOnlyDictionary<string, ApiProfileConfig>? _profiles;

    private readonly Func<string, string, Task<bool>>? _approveAction;

    /// <summary>
    /// Creates a plugin with a shared external <see cref="HttpClient"/>, an optional
    /// host allowlist, optional named API profiles, and an optional flag that bypasses the
    /// private/loopback IP check (for local development environments only).
    /// </summary>
    public HttpPlugin(
        HttpClient httpClient,
        IReadOnlyList<string>? allowedHosts = null,
        IReadOnlyDictionary<string, ApiProfileConfig>? apiProfiles = null,
        bool allowPrivateHosts = false,
        ILogger<HttpPlugin>? logger = null,
        Func<string, string, Task<bool>>? approveAction = null)
    {
        _http               = httpClient;
        _ownsClient         = false;
        _logger             = logger;
        _allowedHosts       = BuildAllowedHosts(allowedHosts);
        _profiles           = apiProfiles;
        _allowPrivateHosts  = allowPrivateHosts;
        _approveAction      = approveAction;
    }

    /// <summary>
    /// Creates a plugin backed by the shared default <see cref="HttpClient"/> (no allowlist, no profiles).
    /// </summary>
    public HttpPlugin(Func<string, string, Task<bool>>? approveAction = null)
    {
        _http         = _defaultHttp;
        _ownsClient   = false;
        _logger       = null;
        _allowedHosts = null;
        _profiles     = null;
        _approveAction = approveAction;
    }

    // Request methods

    // GetAsync/HeadAsync deliberately have no _approveAction gate, unlike Post/Put/Patch/Delete
    // below: HITL approval is for actions with side effects, and read-only requests are never
    // gated — see docs/repl.md's "Read-only tools ... are never gated" and cli-reference.md's
    // matching HITL policy. Not an oversight; keep this asymmetry.
    [Description("HTTP GET request.")]
    public async Task<string> GetAsync(
        [Description("URL or profile-relative path.")] string url,
        [Description("Extra headers as JSON object.")] string? headers = null,
        [Description("Named API profile.")] string? profile = null,
        [Description("Timeout in seconds.")] int timeoutSeconds = 0)
    {
        var (resolvedUrl, mergedHeaders, effectiveTimeout, profileError) = ResolveProfile(url, headers, profile, timeoutSeconds);
        if (profileError is not null) return profileError;
        var denial = await CheckUrlAsync(resolvedUrl);
        if (denial is not null) return denial;

        using var request = BuildRequest(HttpMethod.Get, resolvedUrl, mergedHeaders, out var headerError);
        if (headerError is not null) return PluginResult.Error(headerError);
        return await SendAsync(request, effectiveTimeout);
    }

    [Description("HTTP POST request.")]
    public async Task<string> PostAsync(
        [Description("URL or profile-relative path.")] string url,
        [Description("Request body.")] string body,
        [Description("Content-Type header.")] string contentType = "application/json",
        [Description("Extra headers as JSON object.")] string? headers = null,
        [Description("Named API profile.")] string? profile = null,
        [Description("Timeout in seconds.")] int timeoutSeconds = 0)
    {
        var (resolvedUrl, mergedHeaders, effectiveTimeout, profileError) = ResolveProfile(url, headers, profile, timeoutSeconds);
        if (profileError is not null) return profileError;
        var denial = await CheckUrlAsync(resolvedUrl);
        if (denial is not null) return denial;

        if (_approveAction is not null && !await _approveAction("http_post", resolvedUrl))
            return PluginResult.Denied("HTTP request blocked by user.");

        using var request = BuildRequest(HttpMethod.Post, resolvedUrl, mergedHeaders, out var headerError);
        if (headerError is not null) return PluginResult.Error(headerError);
        request.Content = new StringContent(body, Encoding.UTF8, contentType);
        return await SendAsync(request, effectiveTimeout);
    }

    [Description("HTTP PUT request.")]
    public async Task<string> PutAsync(
        [Description("URL or profile-relative path.")] string url,
        [Description("Request body.")] string body,
        [Description("Content-Type header.")] string contentType = "application/json",
        [Description("Extra headers as JSON object.")] string? headers = null,
        [Description("Named API profile.")] string? profile = null,
        [Description("Timeout in seconds.")] int timeoutSeconds = 0)
    {
        var (resolvedUrl, mergedHeaders, effectiveTimeout, profileError) = ResolveProfile(url, headers, profile, timeoutSeconds);
        if (profileError is not null) return profileError;
        var denial = await CheckUrlAsync(resolvedUrl);
        if (denial is not null) return denial;

        if (_approveAction is not null && !await _approveAction("http_put", resolvedUrl))
            return PluginResult.Denied("HTTP request blocked by user.");

        using var request = BuildRequest(HttpMethod.Put, resolvedUrl, mergedHeaders, out var headerError);
        if (headerError is not null) return PluginResult.Error(headerError);
        request.Content = new StringContent(body, Encoding.UTF8, contentType);
        return await SendAsync(request, effectiveTimeout);
    }

    [Description("HTTP PATCH request.")]
    public async Task<string> PatchAsync(
        [Description("URL or profile-relative path.")] string url,
        [Description("Request body.")] string body,
        [Description("Content-Type header.")] string contentType = "application/json",
        [Description("Extra headers as JSON object.")] string? headers = null,
        [Description("Named API profile.")] string? profile = null,
        [Description("Timeout in seconds.")] int timeoutSeconds = 0)
    {
        var (resolvedUrl, mergedHeaders, effectiveTimeout, profileError) = ResolveProfile(url, headers, profile, timeoutSeconds);
        if (profileError is not null) return profileError;
        var denial = await CheckUrlAsync(resolvedUrl);
        if (denial is not null) return denial;

        if (_approveAction is not null && !await _approveAction("http_patch", resolvedUrl))
            return PluginResult.Denied("HTTP request blocked by user.");

        using var request = BuildRequest(HttpMethod.Patch, resolvedUrl, mergedHeaders, out var headerError);
        if (headerError is not null) return PluginResult.Error(headerError);
        request.Content = new StringContent(body, Encoding.UTF8, contentType);
        return await SendAsync(request, effectiveTimeout);
    }

    [Description("HTTP DELETE request.")]
    public async Task<string> DeleteAsync(
        [Description("URL or profile-relative path.")] string url,
        [Description("Extra headers as JSON object.")] string? headers = null,
        [Description("Named API profile.")] string? profile = null,
        [Description("Timeout in seconds.")] int timeoutSeconds = 0)
    {
        var (resolvedUrl, mergedHeaders, effectiveTimeout, profileError) = ResolveProfile(url, headers, profile, timeoutSeconds);
        if (profileError is not null) return profileError;
        var denial = await CheckUrlAsync(resolvedUrl);
        if (denial is not null) return denial;

        if (_approveAction is not null && !await _approveAction("http_delete", resolvedUrl))
            return PluginResult.Denied("HTTP request blocked by user.");

        using var request = BuildRequest(HttpMethod.Delete, resolvedUrl, mergedHeaders, out var headerError);
        if (headerError is not null) return PluginResult.Error(headerError);
        return await SendAsync(request, effectiveTimeout);
    }

    [Description("HTTP HEAD request. Returns response headers only.")]
    public async Task<string> HeadAsync(
        [Description("URL or profile-relative path.")] string url,
        [Description("Extra headers as JSON object.")] string? headers = null,
        [Description("Named API profile.")] string? profile = null,
        [Description("Timeout in seconds.")] int timeoutSeconds = 0)
    {
        var (resolvedUrl, mergedHeaders, effectiveTimeout, profileError) = ResolveProfile(url, headers, profile, timeoutSeconds);
        if (profileError is not null) return profileError;
        var denial = await CheckUrlAsync(resolvedUrl);
        if (denial is not null) return denial;

        using var request = BuildRequest(HttpMethod.Head, resolvedUrl, mergedHeaders, out var headerError);
        if (headerError is not null) return PluginResult.Error(headerError);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(effectiveTimeout));
        try
        {
            var (final, redirectDenial) = await SendFollowingRedirectsAsync(request, cts.Token);
            if (redirectDenial is not null) return redirectDenial;

            using var response = final!;
            return FormatHeaders(response);
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogDebug("HTTP HEAD request failed: {Message}", ex.Message);
            return $"[REQUEST ERROR] {ex.Message}";
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("HTTP HEAD timed out: {Url}", request.RequestUri);
            return PluginResult.Timeout($"HTTP request exceeded the {effectiveTimeout}s timeout.");
        }
    }

    // Helpers

    /// <summary>
    /// Resolves a named API profile against the supplied URL, headers, and timeout.
    /// Returns <c>(resolvedUrl, mergedHeadersJson, effectiveTimeout, error)</c>.
    /// <c>error</c> is non-null when the profile name is not found — callers must
    /// return the error string immediately without proceeding to the URL check.
    /// When no profile is named the inputs are returned unchanged and <c>error</c> is null.
    /// </summary>
    internal (string resolvedUrl, string? mergedHeaders, int effectiveTimeout, string? error) ResolveProfile(
        string url, string? headers, string? profileName, int callerTimeout)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            return (url, headers, callerTimeout > 0 ? callerTimeout : 30, null);

        if (_profiles is null || !_profiles.TryGetValue(profileName, out var profile))
            return (string.Empty, null, callerTimeout,
                PluginResult.Error($"API profile '{profileName}' is not defined in the configuration."));

        // Resolve URL: prepend the profile's BaseUrl only when the caller supplied a
        // relative path. Check for an explicit http/https scheme rather than using
        // Uri.IsAbsoluteUri — on Linux the runtime treats paths starting with '/' as
        // absolute file:// URIs, which would wrongly skip the base-URL prepend.
        string resolvedUrl;
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("http://",  StringComparison.OrdinalIgnoreCase))
        {
            resolvedUrl = url;
        }
        else
        {
            var baseUri = new Uri(profile.BaseUrl.TrimEnd('/') + "/");
            resolvedUrl = new Uri(baseUri, url.TrimStart('/')).ToString();
        }

        // Merge headers: profile defaults first, then per-call overrides on top.
        var merged = new Dictionary<string, string>(profile.DefaultHeaders, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(headers))
        {
            try
            {
                var perCall = JsonSerializer.Deserialize<Dictionary<string, string>>(headers);
                if (perCall is not null)
                    foreach (var (k, v) in perCall)
                        merged[k] = v;
            }
            catch (JsonException ex)
            {
                throw new ArgumentException(
                    $"'headers' must be a valid JSON object. Parse error: {ex.Message}", ex);
            }
        }

        var mergedJson = merged.Count > 0
            ? JsonSerializer.Serialize(merged)
            : null;

        // Timeout: caller wins when they pass an explicit positive value; otherwise defer to
        // the profile.  Using 0 as the sentinel avoids the ambiguity of the old != 30 check,
        // which couldn't distinguish "caller explicitly wanted 30s" from "caller used the default".
        var effectiveTimeout = callerTimeout > 0 ? callerTimeout : profile.TimeoutSeconds;

        return (resolvedUrl, mergedJson, effectiveTimeout, null);
    }

    /// <summary>
    /// Returns a [DENIED] error when the URL host is not on the allowlist or resolves to a
    /// private/loopback address. Returns null when the request is permitted.
    ///
    /// This is a fast, friendly pre-check only — it gives agents a clear [DENIED] message
    /// for the ordinary case instead of a raw connection error. It is not itself sufficient
    /// against DNS rebinding (a second, later resolution could answer differently), so the
    /// authoritative enforcement is <see cref="CreateSsrfSafeConnectCallback"/>, wired into
    /// every <see cref="HttpClient"/> this plugin sends through, which re-validates atomically
    /// at actual connect time.
    /// </summary>
    private async Task<string?> CheckUrlAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return PluginResult.Error($"Invalid URL: {url}");

        // Always block requests to private/loopback ranges regardless of allowlist —
        // unless AllowPrivateHosts is explicitly enabled (local dev / sandbox only).
        if (!_allowPrivateHosts && await ResolvesToPrivateAddressAsync(uri.Host))
            return PluginResult.Denied($"Host '{uri.Host}' resolves to a private or loopback address.");

        // When an allowlist is configured, enforce it strictly.
        if (_allowedHosts is not null && !_allowedHosts.Contains(uri.Host))
            return PluginResult.Denied($"Host '{uri.Host}' is not in the configured HTTP allowlist.");

        return null;
    }

    /// <summary>
    /// Returns true when <paramref name="host"/> is or resolves to a loopback, link-local,
    /// or RFC-1918 private address. Fails closed: an unresolvable hostname is treated as
    /// private since the HTTP request would fail anyway.
    /// </summary>
    private static async Task<bool> ResolvesToPrivateAddressAsync(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        // Fast path: literal IP address — no DNS needed.
        if (IPAddress.TryParse(host, out var literal))
            return IsPrivateIp(literal);

        // Resolve the hostname and check every returned address.
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host);
        }
        catch (SocketException)
        {
            // Unresolvable — fail closed. The HTTP request would fail for the same reason.
            return true;
        }

        return addresses.Any(IsPrivateIp);
    }

    private static bool IsPrivateIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return
                b[0] == 127 ||                                  // 127.0.0.0/8  loopback
                b[0] == 10 ||                                   // 10.0.0.0/8   RFC-1918
                (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||    // 172.16–31.0.0/12 RFC-1918
                (b[0] == 192 && b[1] == 168) ||                 // 192.168.0.0/16 RFC-1918
                (b[0] == 169 && b[1] == 254);                   // 169.254.0.0/16 link-local
        }

        // IPv6 loopback (::1) and link-local (fe80::/10)
        return ip.Equals(IPAddress.IPv6Loopback) || ip.IsIPv6LinkLocal;
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string url, string? headersJson, out string? error)
    {
        error = null;
        var request = new HttpRequestMessage(method, url);

        if (!string.IsNullOrWhiteSpace(headersJson))
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
                if (dict is not null)
                    foreach (var (key, value) in dict)
                        request.Headers.TryAddWithoutValidation(key, value);
            }
            catch (JsonException ex)
            {
                error = $"'headers' must be a valid JSON object. Parse error: {ex.Message}";
            }
        }

        return request;
    }

    // Request headers that are safe to send to a host the caller never named: content negotiation only.
    // Everything else — profile credentials (X-Api-Key, Authorization), per-call headers the model
    // supplied, cookies, custom tokens — stays with the origin it was meant for.
    private static readonly HashSet<string> CrossOriginSafeHeaders =
        new(StringComparer.OrdinalIgnoreCase) { "Accept", "Accept-Language", "Accept-Encoding", "Cache-Control", "Pragma" };

    /// <summary>
    /// Sends <paramref name="request"/> and follows redirects itself instead of letting the
    /// <see cref="HttpClient"/> do it, for two reasons the client can't handle:
    /// <list type="bullet">
    ///   <item><b>The allowlist and SSRF policy apply to every hop.</b> <see cref="CheckUrlAsync"/> used to run
    ///   once, on the URL the agent asked for, so an allowlisted host could redirect the request anywhere
    ///   and the agent got that host's response.</item>
    ///   <item><b>Credentials stay on their origin.</b> .NET strips only <c>Authorization</c> when a redirect
    ///   changes host, so an API profile's <c>X-Api-Key</c> (or any header the model supplied) was forwarded
    ///   verbatim to whatever the server pointed at. Once a hop leaves the origin originally requested, every
    ///   header outside <see cref="CrossOriginSafeHeaders"/> is dropped, and stays dropped.</item>
    /// </list>
    /// Method and body handling follow browsers and .NET's default (see
    /// <see cref="fuseraft.Infrastructure.Mcp.OriginBoundRedirectHandler.BuildRedirect"/>); at most
    /// <see cref="fuseraft.Infrastructure.Mcp.OriginBoundRedirectHandler.MaxRedirects"/> hops are followed and an
    /// https → http downgrade is not. Returns the final response, or a [DENIED] message when a hop is refused.
    /// </summary>
    private async Task<(HttpResponseMessage? Response, string? Denial)> SendFollowingRedirectsAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var origin = fuseraft.Infrastructure.Mcp.OriginBoundRedirectHandler.OriginOf(request.RequestUri!);

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
            var response = await _http.SendAsync(current, cancellationToken);

            if (!fuseraft.Infrastructure.Mcp.OriginBoundRedirectHandler.IsRedirect(response.StatusCode)
                || response.Headers.Location is not { } location
                || hop >= fuseraft.Infrastructure.Mcp.OriginBoundRedirectHandler.MaxRedirects)
                return (response, null);

            var target = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
            if (fuseraft.Infrastructure.Mcp.OriginBoundRedirectHandler.IsSchemeDowngrade(current.RequestUri!, target))
                return (response, null);

            var denial = await CheckUrlAsync(target.ToString());
            if (denial is not null)
            {
                response.Dispose();
                var reason = denial.StartsWith("[DENIED] ", StringComparison.Ordinal) ? denial["[DENIED] ".Length..] : denial;
                _logger?.LogDebug("HTTP redirect to {Target} refused: {Reason}", target, reason);
                return (null, PluginResult.Denied($"The server redirected to '{target}', which is not permitted: {reason}"));
            }

            var status = response.StatusCode;
            response.Dispose();
            current = fuseraft.Infrastructure.Mcp.OriginBoundRedirectHandler.BuildRedirect(current, target, status, body, contentHeaders);

            if (fuseraft.Infrastructure.Mcp.OriginBoundRedirectHandler.OriginOf(target) != origin)
                foreach (var name in current.Headers.Select(h => h.Key).Where(k => !CrossOriginSafeHeaders.Contains(k)).ToList())
                    current.Headers.Remove(name);
        }
    }

    private async Task<string> SendAsync(HttpRequestMessage request, int timeoutSeconds)
    {
        _logger?.LogDebug("HTTP {Method} {Url}", request.Method, request.RequestUri);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var (final, redirectDenial) = await SendFollowingRedirectsAsync(request, cts.Token);
            if (redirectDenial is not null) return redirectDenial;

            using var response = final!;
            // A local debug endpoint (/actuator/env, /debug/vars) or an echo service can hand back a live
            // credential; it is masked the same way shell output is.
            var body       = EnvSecretMasker.Mask(await response.Content.ReadAsStringAsync(cts.Token));
            var statusLine = $"[HTTP {(int)response.StatusCode} {response.ReasonPhrase}]";

            _logger?.LogDebug("{StatusLine} {Url} ({ContentType})",
                statusLine, request.RequestUri, response.Content.Headers.ContentType);

            return response.IsSuccessStatusCode
                ? (string.IsNullOrWhiteSpace(body) ? statusLine : body)
                : $"{statusLine}\n{body}";
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogDebug("HTTP request failed: {Message}", ex.Message);
            return $"[REQUEST ERROR] {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            _logger?.LogDebug("HTTP request timed out: {Url}", request.RequestUri);
            return PluginResult.Timeout($"HTTP request exceeded the {timeoutSeconds}s timeout.");
        }
    }

    private static string FormatHeaders(HttpResponseMessage response)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"HTTP/{response.Version} {(int)response.StatusCode} {response.ReasonPhrase}");
        foreach (var header in response.Headers)
            sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
        return sb.ToString().TrimEnd();
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }

    private static HashSet<string>? BuildAllowedHosts(IReadOnlyList<string>? list) =>
        list is { Count: > 0 }
            ? new HashSet<string>(list, StringComparer.OrdinalIgnoreCase)
            : null;
}
