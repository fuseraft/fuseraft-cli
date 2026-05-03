using System.ComponentModel;
using System.Net;
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
    private static readonly HttpClient _defaultHttp = CreateDefaultClient();
    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("fuseraft/1.0");
        return client;
    }

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly ILogger<HttpPlugin>? _logger;

    // Null means unrestricted; non-null means enforce the allowlist.
    private readonly HashSet<string>? _allowedHosts;
    private readonly bool _allowPrivateHosts;

    // Named API profiles — null when no profiles are configured.
    private readonly IReadOnlyDictionary<string, ApiProfileConfig>? _profiles;

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
        ILogger<HttpPlugin>? logger = null)
    {
        _http               = httpClient;
        _ownsClient         = false;
        _logger             = logger;
        _allowedHosts       = BuildAllowedHosts(allowedHosts);
        _profiles           = apiProfiles;
        _allowPrivateHosts  = allowPrivateHosts;
    }

    /// <summary>
    /// Creates a plugin backed by the shared default <see cref="HttpClient"/> (no allowlist, no profiles).
    /// </summary>
    public HttpPlugin()
    {
        _http         = _defaultHttp;
        _ownsClient   = false;
        _logger       = null;
        _allowedHosts = null;
        _profiles     = null;
    }

    // Request methods

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
            using var response = await _http.SendAsync(request, cts.Token);
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

    private async Task<string> SendAsync(HttpRequestMessage request, int timeoutSeconds)
    {
        _logger?.LogDebug("HTTP {Method} {Url}", request.Method, request.RequestUri);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var response = await _http.SendAsync(request, cts.Token);
            var body       = await response.Content.ReadAsStringAsync(cts.Token);
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
