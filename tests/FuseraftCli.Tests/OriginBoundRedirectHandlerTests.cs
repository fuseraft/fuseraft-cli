using System.Net;
using System.Text;
using fuseraft.Infrastructure.Mcp;

namespace FuseraftCli.Tests;

/// <summary>
/// <see cref="OriginBoundRedirectHandler"/> — redirects are followed, but the server's configured
/// credentials never leave the origin they were configured for. Real loopback servers, one per
/// origin (a different port is a different origin).
/// </summary>
[Collection("LoopbackPorts")]
public sealed class OriginBoundRedirectHandlerTests
{
    private static HttpClient NewClient(params string[] configuredHeaders) =>
        new(new OriginBoundRedirectHandler(configuredHeaders));

    private static HttpRequestMessage Post(string url, string body = "{\"jsonrpc\":\"2.0\"}")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("X-Api-Key", "super-secret");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer token-abc");
        req.Headers.TryAddWithoutValidation("Cookie", "session=xyz");
        req.Headers.TryAddWithoutValidation("X-Trace", "trace-1");
        return req;
    }

    private static readonly Task<int> Zero = Task.FromResult(0);

    [Fact]
    public async Task CrossOrigin307_DropsConfiguredAndCredentialHeaders_ButKeepsOthersAndTheBody()
    {
        await using var other = new LoopbackHttpServer((_, res) => LoopbackHttpServer.Respond(res, 200, "landed"));
        await using var origin = new LoopbackHttpServer((_, res) => LoopbackHttpServer.RedirectTo(res, other.BaseUrl + "/next"));
        using var client = NewClient("X-Api-Key");

        var response = await client.SendAsync(Post(origin.BaseUrl + "/mcp"));

        Assert.Equal("landed", await response.Content.ReadAsStringAsync());

        var first = Assert.Single(origin.Snapshot());
        Assert.Equal("super-secret", first.Headers["X-Api-Key"]);          // the configured origin keeps its own
        Assert.Equal("Bearer token-abc", first.Headers["Authorization"]);

        var hop = Assert.Single(other.Snapshot());
        Assert.Equal("POST", hop.Method);                                   // 307 preserves the method…
        Assert.Equal("{\"jsonrpc\":\"2.0\"}", hop.Body);                    // …and the body
        Assert.Equal("trace-1", hop.Headers["X-Trace"]);                    // unrelated headers ride along
        Assert.False(hop.Headers.ContainsKey("X-Api-Key"));
        Assert.False(hop.Headers.ContainsKey("Authorization"));
        Assert.False(hop.Headers.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task SameOriginRedirect_KeepsEveryHeaderAndTheBody()
    {
        await using var server = new LoopbackHttpServer((req, res) =>
            req.Url!.AbsolutePath == "/mcp"
                ? LoopbackHttpServer.RedirectTo(res, "/mcp/", 307)          // relative, same origin
                : LoopbackHttpServer.Respond(res, 200, "ok"));
        using var client = NewClient("X-Api-Key");

        var response = await client.SendAsync(Post(server.BaseUrl + "/mcp"));

        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        var requests = server.Snapshot();
        Assert.Equal(2, requests.Count);
        Assert.Equal("/mcp/", requests[1].Path);
        Assert.Equal("super-secret", requests[1].Headers["X-Api-Key"]);
        Assert.Equal("Bearer token-abc", requests[1].Headers["Authorization"]);
        Assert.Equal("{\"jsonrpc\":\"2.0\"}", requests[1].Body);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    public async Task PostRedirectedWith301_302_303_BecomesAGetWithNoBody(int status)
    {
        await using var other = new LoopbackHttpServer((_, res) => LoopbackHttpServer.Respond(res, 200, "landed"));
        await using var origin = new LoopbackHttpServer((_, res) => LoopbackHttpServer.RedirectTo(res, other.BaseUrl + "/next", status));
        using var client = NewClient("X-Api-Key");

        await client.SendAsync(Post(origin.BaseUrl + "/mcp"));

        var hop = Assert.Single(other.Snapshot());
        Assert.Equal("GET", hop.Method);
        Assert.Equal(string.Empty, hop.Body);
        Assert.False(hop.Headers.ContainsKey("X-Api-Key"));
    }

    [Fact]
    public async Task GetStaysAGetAcrossA301()
    {
        await using var other = new LoopbackHttpServer((_, res) => LoopbackHttpServer.Respond(res, 200, "landed"));
        await using var origin = new LoopbackHttpServer((_, res) => LoopbackHttpServer.RedirectTo(res, other.BaseUrl + "/next", 301));
        using var client = NewClient();

        await client.GetAsync(origin.BaseUrl + "/mcp");

        Assert.Equal("GET", Assert.Single(other.Snapshot()).Method);
    }

    [Fact]
    public async Task HeaderNameMatching_IsCaseInsensitive()
    {
        await using var other = new LoopbackHttpServer((_, res) => LoopbackHttpServer.Respond(res, 200));
        await using var origin = new LoopbackHttpServer((_, res) => LoopbackHttpServer.RedirectTo(res, other.BaseUrl + "/next"));
        using var client = NewClient("x-api-key");                          // configured in a different case

        await client.SendAsync(Post(origin.BaseUrl + "/mcp"));

        Assert.False(Assert.Single(other.Snapshot()).Headers.ContainsKey("X-Api-Key"));
    }

    [Fact]
    public async Task OnceDropped_HeadersStayDropped_EvenWhenALaterHopReturnsToTheOriginalOrigin()
    {
        LoopbackHttpServer? other = null;
        await using var origin = new LoopbackHttpServer((req, res) =>
            req.Url!.AbsolutePath == "/start"
                ? LoopbackHttpServer.RedirectTo(res, other!.BaseUrl + "/bounce")
                : LoopbackHttpServer.Respond(res, 200, "home again"));
        await using var otherServer = new LoopbackHttpServer((_, res) => LoopbackHttpServer.RedirectTo(res, origin.BaseUrl + "/back"));
        other = otherServer;
        using var client = NewClient("X-Api-Key");

        var response = await client.SendAsync(Post(origin.BaseUrl + "/start"));

        Assert.Equal("home again", await response.Content.ReadAsStringAsync());
        var back = origin.Snapshot().Single(r => r.Path == "/back");
        Assert.False(back.Headers.ContainsKey("X-Api-Key"));
        Assert.False(back.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task RedirectLoop_StopsAtMaxRedirects_AndReturnsTheRedirectResponse()
    {
        LoopbackHttpServer? self = null;
        await using var server = new LoopbackHttpServer((_, res) => LoopbackHttpServer.RedirectTo(res, self!.BaseUrl + "/again"));
        self = server;
        using var client = NewClient();

        var response = await client.GetAsync(server.BaseUrl + "/start");

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(1 + OriginBoundRedirectHandler.MaxRedirects, server.Snapshot().Count);
    }

    [Fact]
    public async Task RedirectWithoutLocation_IsReturnedAsIs()
    {
        await using var server = new LoopbackHttpServer((_, res) => LoopbackHttpServer.Respond(res, 302));
        using var client = NewClient();

        var response = await client.GetAsync(server.BaseUrl + "/x");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(server.Snapshot());
    }

    [Fact]
    public async Task NonRedirectResponse_PassesThroughUntouched()
    {
        await using var server = new LoopbackHttpServer((_, res) => LoopbackHttpServer.Respond(res, 404, "nope"));
        using var client = NewClient("X-Api-Key");

        var response = await client.SendAsync(Post(server.BaseUrl + "/mcp"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("nope", await response.Content.ReadAsStringAsync());
        Assert.Single(server.Snapshot());
    }

    [Fact]
    public async Task RequestWithoutBody_IsForwardedAcrossRedirects()
    {
        await using var other = new LoopbackHttpServer((_, res) => LoopbackHttpServer.Respond(res, 200, "landed"));
        await using var origin = new LoopbackHttpServer((_, res) => LoopbackHttpServer.RedirectTo(res, other.BaseUrl + "/next"));
        using var client = NewClient();

        var response = await client.GetAsync(origin.BaseUrl + "/sse");

        Assert.Equal("landed", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("https://h/x", "http://h/x", true)]
    [InlineData("http://h/x", "https://h/x", false)]
    [InlineData("http://h/x", "http://other/x", false)]
    [InlineData("https://h/x", "https://other/x", false)]
    public void IsSchemeDowngrade_OnlyFlagsHttpsToHttp(string from, string to, bool expected) =>
        Assert.Equal(expected, OriginBoundRedirectHandler.IsSchemeDowngrade(new Uri(from), new Uri(to)));

    [Fact]
    public void OriginOf_TreatsDefaultPortsAndHostCaseAsTheSameOrigin()
    {
        var a = OriginBoundRedirectHandler.OriginOf(new Uri("http://Example.COM/a"));
        var b = OriginBoundRedirectHandler.OriginOf(new Uri("http://example.com:80/b"));

        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("http://h:8080/x", "http://h:9090/x")]      // port
    [InlineData("http://h/x", "https://h/x")]                // scheme
    [InlineData("http://h/x", "http://other/x")]             // host
    public void OriginOf_DistinguishesSchemeHostAndPort(string a, string b) =>
        Assert.NotEqual(OriginBoundRedirectHandler.OriginOf(new Uri(a)), OriginBoundRedirectHandler.OriginOf(new Uri(b)));
}
