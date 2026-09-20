using fuseraft.Core.Models;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Mcp;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// The Http plugin checked its host allowlist once, on the URL the agent asked for, and let the
/// <see cref="HttpClient"/> follow redirects on its own — so an allowlisted host could redirect the
/// request to any host and the agent got that host's response, and an API profile's
/// <c>X-Api-Key</c> (.NET strips only <c>Authorization</c> across hosts) went with it. Reproduced
/// against the production shared client first. Two loopback servers give two <i>hosts</i>
/// (127.0.0.1 and 127.0.0.2), which is what an allowlist and a credential boundary care about.
/// </summary>
[Collection("LoopbackPorts")]
public sealed class HttpPluginRedirectTests
{
    private const string Profile = "svc";

    private static HttpPlugin Http(
        LoopbackHttpServer origin, IReadOnlyList<string>? allowedHosts, bool withProfile = true)
    {
        var profiles = withProfile
            ? new Dictionary<string, ApiProfileConfig>
            {
                [Profile] = new()
                {
                    BaseUrl = origin.BaseUrl,
                    DefaultHeaders = new()
                    {
                        ["X-Api-Key"] = "profile-secret-key-777",
                        ["Authorization"] = "Bearer profile-bearer-888",
                    },
                },
            }
            : null;

        // The registry builds the real shared client — the one production sends through.
        var registry = new PluginRegistry().RegisterDefaults().Configure(
            new SecurityConfig { HttpAllowedHosts = [.. allowedHosts ?? []], AllowPrivateHosts = true }, profiles);
        Assert.True(registry.TryGet("Http", out var plugin));
        return Assert.IsType<HttpPlugin>(plugin);
    }

    private static LoopbackHttpServer Landing(string body = "landed") =>
        new((_, res) => LoopbackHttpServer.Respond(res, 200, body), host: "127.0.0.2");

    private static LoopbackHttpServer RedirectingTo(string location, int status = 302) =>
        new((_, res) => LoopbackHttpServer.RedirectTo(res, location, status));

    // -- the allowlist applies to every hop --------------------------------------------------------

    [Fact]
    public async Task ARedirectToAHostNotOnTheAllowlist_IsDenied_AndThatHostIsNeverContacted()
    {
        await using var other  = Landing("must-not-be-seen");
        await using var origin = RedirectingTo(other.BaseUrl + "/collect");
        var http = Http(origin, allowedHosts: ["127.0.0.1"]);

        var result = await http.GetAsync("/start", profile: Profile);

        Assert.StartsWith("[DENIED]", result);
        Assert.Contains("127.0.0.2", result);
        Assert.Contains("not in the configured HTTP allowlist", result);
        Assert.DoesNotContain("must-not-be-seen", result);
        Assert.Empty(other.Snapshot());                                   // the request never left
    }

    [Fact]
    public async Task ARedirectToAnAllowlistedHost_IsFollowed()
    {
        await using var other  = Landing("landed-on-allowed-host");
        await using var origin = RedirectingTo(other.BaseUrl + "/next");
        var http = Http(origin, allowedHosts: ["127.0.0.1", "127.0.0.2"]);

        var result = await http.GetAsync("/start", profile: Profile);

        Assert.Equal("landed-on-allowed-host", result);
    }

    [Fact]
    public async Task ARedirectChain_IsCheckedAtEveryHop_NotJustTheFirst()
    {
        await using var end    = Landing("end-of-chain");
        await using var middle = new LoopbackHttpServer((_, res) => LoopbackHttpServer.RedirectTo(res, end.BaseUrl + "/z"), host: "127.0.0.3");
        await using var origin = RedirectingTo(middle.BaseUrl + "/y");
        var http = Http(origin, allowedHosts: ["127.0.0.1", "127.0.0.3"]);   // 127.0.0.2 (the end) is not allowed

        var result = await http.GetAsync("/start", profile: Profile);

        Assert.StartsWith("[DENIED]", result);
        Assert.Contains("127.0.0.2", result);
        Assert.Single(middle.Snapshot());                                     // hop 1 was allowed…
        Assert.Empty(end.Snapshot());                                         // …hop 2 was refused
    }

    [Fact]
    public async Task WithNoAllowlistAtAll_ACrossHostRedirectIsStillFollowed_ButWithoutCredentials()
    {
        await using var other  = Landing("landed-unrestricted");
        await using var origin = RedirectingTo(other.BaseUrl + "/next");
        var http = Http(origin, allowedHosts: null);

        var result = await http.GetAsync("/start", profile: Profile);

        Assert.Equal("landed-unrestricted", result);
        Assert.False(other.SawHeader("X-Api-Key"));
    }

    // -- credentials stay on their origin ----------------------------------------------------------

    [Fact]
    public async Task ProfileCredentials_ReachTheOriginItself_ButNotAnotherHostItRedirectsTo()
    {
        await using var other  = Landing();
        await using var origin = RedirectingTo(other.BaseUrl + "/collect");
        var http = Http(origin, allowedHosts: ["127.0.0.1", "127.0.0.2"]);

        await http.GetAsync("/start", profile: Profile);

        Assert.True(origin.SawHeader("X-Api-Key", "profile-secret-key-777"));           // the configured host gets its key
        Assert.True(origin.SawHeader("Authorization", "Bearer profile-bearer-888"));
        Assert.NotEmpty(other.Snapshot());
        Assert.False(other.SawHeader("X-Api-Key"), "a profile credential leaked to another host via redirect");
        Assert.False(other.SawHeader("Authorization"));
    }

    [Fact]
    public async Task HeadersTheModelSuppliedPerCall_AlsoStayOnTheirOrigin()
    {
        await using var other  = Landing();
        await using var origin = RedirectingTo(other.BaseUrl + "/collect");
        var http = Http(origin, allowedHosts: ["127.0.0.1", "127.0.0.2"], withProfile: false);

        await http.GetAsync(origin.BaseUrl + "/start", headers: "{\"X-Model-Token\":\"model-supplied-999\",\"Accept\":\"text/plain\"}");

        Assert.True(origin.SawHeader("X-Model-Token"));
        Assert.False(other.SawHeader("X-Model-Token"));
        Assert.True(other.SawHeader("Accept", "text/plain"));             // content negotiation is harmless and kept
    }

    [Fact]
    public async Task OnceDropped_CredentialsStayDropped_EvenIfALaterHopReturnsToTheOriginalHost()
    {
        LoopbackHttpServer? origin = null;
        await using var other = new LoopbackHttpServer((_, res) => LoopbackHttpServer.RedirectTo(res, origin!.BaseUrl + "/back"), host: "127.0.0.2");
        await using var first = new LoopbackHttpServer((req, res) =>
            req.Url!.AbsolutePath == "/start"
                ? LoopbackHttpServer.RedirectTo(res, other.BaseUrl + "/bounce")
                : LoopbackHttpServer.Respond(res, 200, "home again"));
        origin = first;
        var http = Http(first, allowedHosts: ["127.0.0.1", "127.0.0.2"]);

        var result = await http.GetAsync("/start", profile: Profile);

        Assert.Equal("home again", result);
        var back = first.Snapshot().Single(r => r.Path == "/back");
        Assert.False(back.Headers.ContainsKey("X-Api-Key"));
    }

    [Fact]
    public async Task ASameOriginRedirect_KeepsEveryHeader()
    {
        await using var origin = new LoopbackHttpServer((req, res) =>
            req.Url!.AbsolutePath == "/start"
                ? LoopbackHttpServer.RedirectTo(res, "/next")               // relative, same origin
                : LoopbackHttpServer.Respond(res, 200, "arrived"));
        var http = Http(origin, allowedHosts: ["127.0.0.1"]);

        var result = await http.GetAsync("/start", profile: Profile);

        Assert.Equal("arrived", result);
        var next = origin.Snapshot().Single(r => r.Path == "/next");
        Assert.Equal("profile-secret-key-777", next.Headers["X-Api-Key"]);
        Assert.Equal("Bearer profile-bearer-888", next.Headers["Authorization"]);
    }

    // -- methods and bodies ------------------------------------------------------------------------

    [Fact]
    public async Task Post307_ToAnAllowlistedHost_ReplaysTheBody_WithoutCredentials()
    {
        await using var other  = Landing("posted");
        await using var origin = RedirectingTo(other.BaseUrl + "/sink", 307);
        var http = Http(origin, allowedHosts: ["127.0.0.1", "127.0.0.2"]);

        var result = await http.PostAsync("/start", "{\"n\":42}", profile: Profile);

        Assert.Equal("posted", result);
        var hop = Assert.Single(other.Snapshot());
        Assert.Equal("POST", hop.Method);
        Assert.Equal("{\"n\":42}", hop.Body);
        Assert.False(hop.Headers.ContainsKey("X-Api-Key"));
    }

    [Fact]
    public async Task Post302_BecomesAGetWithNoBody()
    {
        await using var other  = Landing();
        await using var origin = RedirectingTo(other.BaseUrl + "/next", 302);
        var http = Http(origin, allowedHosts: ["127.0.0.1", "127.0.0.2"]);

        await http.PostAsync("/start", "secret-body-data", profile: Profile);

        var hop = Assert.Single(other.Snapshot());
        Assert.Equal("GET", hop.Method);
        Assert.Equal(string.Empty, hop.Body);
    }

    [Fact]
    public async Task Head_FollowsRedirectsUnderTheSameRules()
    {
        await using var other  = Landing();
        await using var origin = RedirectingTo(other.BaseUrl + "/next");
        var http = Http(origin, allowedHosts: ["127.0.0.1"]);           // 127.0.0.2 not allowed

        var result = await http.HeadAsync("/start", profile: Profile);

        Assert.StartsWith("[DENIED]", result);
        Assert.Empty(other.Snapshot());
    }

    [Fact]
    public async Task Head_AcrossAnAllowedRedirect_StripsCredentialsToo()
    {
        await using var other  = Landing();
        await using var origin = RedirectingTo(other.BaseUrl + "/next");
        var http = Http(origin, allowedHosts: ["127.0.0.1", "127.0.0.2"]);

        await http.HeadAsync("/start", profile: Profile);

        Assert.False(other.SawHeader("X-Api-Key"));
    }

    // -- limits and edge cases ---------------------------------------------------------------------

    [Fact]
    public async Task ARedirectLoop_StopsAtTheLimit_AndReportsTheRedirectResponse()
    {
        LoopbackHttpServer? self = null;
        await using var origin = new LoopbackHttpServer((_, res) => LoopbackHttpServer.RedirectTo(res, self!.BaseUrl + "/again"));
        self = origin;
        var http = Http(origin, allowedHosts: ["127.0.0.1"]);

        var result = await http.GetAsync("/start", profile: Profile);

        Assert.StartsWith("[HTTP 307", result);                          // the helper redirects with 307 by default
        Assert.Equal(1 + OriginBoundRedirectHandler.MaxRedirects, origin.Snapshot().Count);
    }

    [Fact]
    public async Task ANonRedirectResponse_PassesThroughUnchanged()
    {
        await using var origin = new LoopbackHttpServer((_, res) => LoopbackHttpServer.Respond(res, 404, "nothing here"));
        var http = Http(origin, allowedHosts: ["127.0.0.1"]);

        var result = await http.GetAsync("/missing", profile: Profile);

        Assert.StartsWith("[HTTP 404", result);
        Assert.Contains("nothing here", result);
    }

    [Fact]
    public async Task HttpPluginWithAnExternalClient_StillWorks_ForOrdinaryRequests()
    {
        await using var origin = new LoopbackHttpServer((_, res) => LoopbackHttpServer.Respond(res, 200, "plain"));
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        var http = new HttpPlugin(client, allowedHosts: ["127.0.0.1"], allowPrivateHosts: true);

        Assert.Equal("plain", await http.GetAsync(origin.BaseUrl + "/x"));
    }

    [Fact]
    public async Task ThePostApprovalPrompt_IsShownOnce_NotOncePerHop()
    {
        await using var other  = Landing();
        await using var origin = RedirectingTo(other.BaseUrl + "/sink", 307);
        var asked = 0;
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        var http = new HttpPlugin(client, allowedHosts: ["127.0.0.1", "127.0.0.2"], allowPrivateHosts: true,
            approveAction: (_, _) => { asked++; return Task.FromResult(true); });

        await http.PostAsync(origin.BaseUrl + "/start", "x");

        Assert.Equal(1, asked);
    }
}
