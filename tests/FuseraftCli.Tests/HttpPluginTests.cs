using System.Net;
using System.Text.Json;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="HttpPlugin"/> API-profile support: URL resolution,
/// header merging, timeout selection, unknown-profile errors, and allowlist enforcement.
///
/// A <see cref="FakeHandler"/> intercepts outbound HTTP calls so no network traffic
/// is generated. DNS resolution for the allowlist check still runs, so test hosts must
/// be real public hostnames (we use <c>example.com</c>, which is always resolvable).
/// </summary>
public sealed class HttpPluginTests : IDisposable
{
    // 203.0.113.1 is in the RFC 5737 TEST-NET-3 range: reserved for documentation/tests,
    // always public (passes the private-IP check), and parsed as a literal IP — so no DNS
    // lookup is needed. The FakeHandler intercepts before any real TCP connection is made.
    private const string TestHost    = "203.0.113.1";
    private const string TestBaseUrl = $"https://{TestHost}/api/v1";

    private readonly FakeHandler _handler;
    private readonly HttpClient  _client;

    public HttpPluginTests()
    {
        _handler = new FakeHandler();
        _client  = new HttpClient(_handler) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("fuseraft/1.0");
    }

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }

    // -----------------------------------------------------------------------
    // ResolveProfile — URL resolution
    // -----------------------------------------------------------------------

    [Fact]
    public void ResolveProfile_NoProfile_ReturnsInputsUnchanged()
    {
        var plugin = MakePlugin();
        var (url, hdrs, timeout, error) = plugin.ResolveProfile(
            "https://example.com/foo", "{\"X-Foo\":\"bar\"}", null, 45);

        Assert.Null(error);
        Assert.Equal("https://example.com/foo", url);
        Assert.Equal("{\"X-Foo\":\"bar\"}", hdrs);
        Assert.Equal(45, timeout);
    }

    [Fact]
    public void ResolveProfile_RelativePath_LeadingSlash_PrependsBaseUrl()
    {
        var plugin = MakePlugin(profileName: "snow");
        var (url, _, _, error) = plugin.ResolveProfile("/table/incident", null, "snow", 30);

        Assert.Null(error);
        Assert.Equal($"{TestBaseUrl}/table/incident", url);
    }

    [Fact]
    public void ResolveProfile_RelativePath_NoLeadingSlash_PrependsBaseUrl()
    {
        var plugin = MakePlugin(profileName: "snow");
        var (url, _, _, error) = plugin.ResolveProfile("table/incident", null, "snow", 30);

        Assert.Null(error);
        Assert.Equal($"{TestBaseUrl}/table/incident", url);
    }

    [Fact]
    public void ResolveProfile_AbsoluteUrl_UsedAsIs()
    {
        var plugin = MakePlugin(profileName: "snow");
        var absoluteUrl = "https://other.example.com/different/path";
        var (url, _, _, error) = plugin.ResolveProfile(absoluteUrl, null, "snow", 30);

        Assert.Null(error);
        Assert.Equal(absoluteUrl, url);
    }

    [Fact]
    public void ResolveProfile_UnknownProfile_ReturnsError()
    {
        var plugin = MakePlugin(profileName: "snow"); // "missing" is not registered
        var (_, _, _, error) = plugin.ResolveProfile("/foo", null, "missing", 30);

        Assert.NotNull(error);
        Assert.StartsWith("[ERROR]", error);
        Assert.Contains("missing", error);
    }

    [Fact]
    public void ResolveProfile_NullProfilesDictionary_ReturnsError()
    {
        var plugin = MakePlugin(); // no profiles at all
        var (_, _, _, error) = plugin.ResolveProfile("/foo", null, "snow", 30);

        Assert.NotNull(error);
        Assert.StartsWith("[ERROR]", error);
    }

    // -----------------------------------------------------------------------
    // ResolveProfile — header merging
    // -----------------------------------------------------------------------

    [Fact]
    public void ResolveProfile_ProfileDefaultHeaders_InjectedWhenNoPerCallHeaders()
    {
        var plugin = MakePlugin(
            profileName: "snow",
            headers: new() { ["Authorization"] = "Bearer token123", ["Accept"] = "application/json" });

        var (_, mergedJson, _, _) = plugin.ResolveProfile("/table/incident", null, "snow", 30);

        Assert.NotNull(mergedJson);
        var merged = JsonSerializer.Deserialize<Dictionary<string, string>>(mergedJson!)!;
        Assert.Equal("Bearer token123", merged["Authorization"]);
        Assert.Equal("application/json", merged["Accept"]);
    }

    [Fact]
    public void ResolveProfile_PerCallHeaders_OverrideProfileDefaults()
    {
        var plugin = MakePlugin(
            profileName: "snow",
            headers: new() { ["Authorization"] = "Bearer default", ["Accept"] = "application/json" });

        var perCall = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer override",
        });

        var (_, mergedJson, _, _) = plugin.ResolveProfile("/table/incident", perCall, "snow", 30);

        Assert.NotNull(mergedJson);
        var merged = JsonSerializer.Deserialize<Dictionary<string, string>>(mergedJson!)!;
        Assert.Equal("Bearer override",   merged["Authorization"]);  // overridden
        Assert.Equal("application/json", merged["Accept"]);           // kept from profile
    }

    [Fact]
    public void ResolveProfile_PerCallHeaders_MergedWithProfileDefaults_NonConflicting()
    {
        var plugin = MakePlugin(
            profileName: "snow",
            headers: new() { ["Authorization"] = "Bearer token123" });

        var perCall = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["X-Custom-Header"] = "my-value",
        });

        var (_, mergedJson, _, _) = plugin.ResolveProfile("/table/incident", perCall, "snow", 30);

        Assert.NotNull(mergedJson);
        var merged = JsonSerializer.Deserialize<Dictionary<string, string>>(mergedJson!)!;
        Assert.Equal("Bearer token123", merged["Authorization"]);
        Assert.Equal("my-value",        merged["X-Custom-Header"]);
    }

    [Fact]
    public void ResolveProfile_EmptyProfileHeaders_NoPerCallHeaders_ReturnsNullMerged()
    {
        var plugin = MakePlugin(profileName: "snow"); // headers defaults to []
        var (_, mergedJson, _, _) = plugin.ResolveProfile("/table/incident", null, "snow", 30);

        Assert.Null(mergedJson);
    }

    // -----------------------------------------------------------------------
    // ResolveProfile — timeout selection
    // -----------------------------------------------------------------------

    [Fact]
    public void ResolveProfile_ProfileTimeout_UsedWhenCallerUsesDefault()
    {
        var plugin = MakePlugin(profileName: "snow", profileTimeout: 60);
        var (_, _, effectiveTimeout, _) = plugin.ResolveProfile("/foo", null, "snow", callerTimeout: 0);

        // Caller used the default (0 = unset), so the profile's 60s should win.
        Assert.Equal(60, effectiveTimeout);
    }

    [Fact]
    public void ResolveProfile_CallerTimeout_OverridesProfileTimeout()
    {
        var plugin = MakePlugin(profileName: "snow", profileTimeout: 60);
        var (_, _, effectiveTimeout, _) = plugin.ResolveProfile("/foo", null, "snow", callerTimeout: 10);

        // Caller explicitly specified 10, so 10 wins over the profile's 60s.
        Assert.Equal(10, effectiveTimeout);
    }

    [Fact]
    public void ResolveProfile_CallerTimeout_ExplicitThirtyOverridesProfile()
    {
        var plugin = MakePlugin(profileName: "snow", profileTimeout: 60);
        var (_, _, effectiveTimeout, _) = plugin.ResolveProfile("/foo", null, "snow", callerTimeout: 30);

        // Caller explicitly passed 30 — should win, not be confused with the old sentinel default.
        Assert.Equal(30, effectiveTimeout);
    }

    [Fact]
    public void ResolveProfile_NoProfile_CallerTimeoutReturnedUnchanged()
    {
        var plugin = MakePlugin();
        var (_, _, effectiveTimeout, _) = plugin.ResolveProfile("/foo", null, null, callerTimeout: 45);

        Assert.Equal(45, effectiveTimeout);
    }

    // -----------------------------------------------------------------------
    // GetAsync — end-to-end with profile (real DNS lookup, fake HTTP)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_WithProfile_SendsRequestToResolvedUrl()
    {
        var plugin = MakePlugin(
            allowedHosts: [TestHost],
            profileName: "snow",
            headers: new() { ["Authorization"] = "Bearer tok" });

        var result = await plugin.GetAsync("/table/incident", profile: "snow");

        Assert.Equal($"{TestBaseUrl}/table/incident", _handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer tok", _handler.LastRequest.Headers.GetValues("Authorization").First());
        Assert.Equal("fake body", result);
    }

    [Fact]
    public async Task GetAsync_UnknownProfile_ReturnsErrorWithoutHttpCall()
    {
        var plugin = MakePlugin(allowedHosts: [TestHost]);

        var result = await plugin.GetAsync("/table/incident", profile: "missing");

        Assert.Null(_handler.LastRequest); // no HTTP call made
        Assert.StartsWith("[ERROR]", result);
    }

    // -----------------------------------------------------------------------
    // PostAsync — end-to-end with profile
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PostAsync_WithProfile_SendsToResolvedUrl()
    {
        var plugin = MakePlugin(allowedHosts: [TestHost], profileName: "snow");

        await plugin.PostAsync("/table/incident", body: "{}", profile: "snow");

        Assert.Equal($"{TestBaseUrl}/table/incident", _handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, _handler.LastRequest.Method);
    }

    // -----------------------------------------------------------------------
    // PatchAsync — new method
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PatchAsync_WithProfile_SendsPatchToResolvedUrl()
    {
        var plugin = MakePlugin(
            allowedHosts: [TestHost],
            profileName: "snow",
            headers: new() { ["Content-Type"] = "application/json" });

        await plugin.PatchAsync("/table/incident/INC001", body: "{\"state\":\"6\"}", profile: "snow");

        Assert.Equal($"{TestBaseUrl}/table/incident/INC001", _handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Patch, _handler.LastRequest.Method);
    }

    [Fact]
    public async Task PatchAsync_WithoutProfile_SendsDirectUrl()
    {
        var plugin = MakePlugin(allowedHosts: [TestHost]);

        await plugin.PatchAsync($"https://{TestHost}/some/path", body: "{}");

        Assert.Equal($"https://{TestHost}/some/path", _handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Patch, _handler.LastRequest.Method);
    }

    // -----------------------------------------------------------------------
    // Allowlist enforcement is not bypassed by a profile
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_AllowlistConfigured_BlocksUnlistedHostEvenWithProfile()
    {
        // Profile resolves to example.com, but allowlist only allows other.example.com.
        var plugin = MakePlugin(
            allowedHosts: ["other.example.com"],
            profileName: "snow");

        var result = await plugin.GetAsync("/table/incident", profile: "snow");

        Assert.Null(_handler.LastRequest); // blocked before sending
        Assert.StartsWith("[DENIED]", result);
    }

    // -----------------------------------------------------------------------
    // ApiProfileConfig — model defaults
    // -----------------------------------------------------------------------

    [Fact]
    public void ApiProfileConfig_DefaultValues_AreCorrect()
    {
        var config = new ApiProfileConfig();

        Assert.Equal(string.Empty, config.BaseUrl);
        Assert.Empty(config.DefaultHeaders);
        Assert.Equal(30, config.TimeoutSeconds);
    }

    [Fact]
    public void ApiProfileConfig_CanBeInitialized_WithAllFields()
    {
        var config = new ApiProfileConfig
        {
            BaseUrl        = "https://mycompany.service-now.com/api/now",
            TimeoutSeconds = 45,
            DefaultHeaders = new() { ["Authorization"] = "Basic abc123" },
        };

        Assert.Equal("https://mycompany.service-now.com/api/now", config.BaseUrl);
        Assert.Equal(45, config.TimeoutSeconds);
        Assert.Equal("Basic abc123", config.DefaultHeaders["Authorization"]);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds an <see cref="HttpPlugin"/> backed by the fake handler, with an optional
    /// named profile (registered under <paramref name="profileName"/>) and allowlist.
    /// </summary>
    private HttpPlugin MakePlugin(
        IReadOnlyList<string>? allowedHosts = null,
        string? profileName = null,
        string? baseUrl = null,
        Dictionary<string, string>? headers = null,
        int profileTimeout = 30)
    {
        IReadOnlyDictionary<string, ApiProfileConfig>? profiles = null;
        if (profileName is not null)
        {
            profiles = new Dictionary<string, ApiProfileConfig>(StringComparer.OrdinalIgnoreCase)
            {
                [profileName] = new ApiProfileConfig
                {
                    BaseUrl        = baseUrl ?? TestBaseUrl,
                    DefaultHeaders = headers ?? [],
                    TimeoutSeconds = profileTimeout,
                }
            };
        }
        return new HttpPlugin(_client, allowedHosts, profiles);
    }

    /// <summary>
    /// Intercepts every outgoing <see cref="HttpRequestMessage"/> and returns a canned
    /// 200 OK response. No actual network traffic is generated.
    /// </summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string ResponseBody   { get; set; } = "fake body";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(ResponseBody),
            });
        }
    }
}
