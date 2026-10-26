using System.Net;
using fuseraft.Core.Models.Agents;
using fuseraft.Infrastructure.Agents;

namespace FuseraftCli.Tests;

/// <summary>
/// <see cref="RemoteAgentHttpClient"/> — a remote agent's configured headers reach its own origin
/// and nowhere else: not an origin the agent card names, not the far side of a redirect.
/// </summary>
[Collection("LoopbackPorts")]
public sealed class RemoteAgentHttpClientTests
{
    private const string TokenVar = "FUSERAFT_TEST_REMOTE_AGENT_TOKEN";

    [Fact]
    public async Task Create_SendsConfiguredHeadersToTheConfiguredOrigin_WithEnvTokensExpanded()
    {
        await using var server = new LoopbackHttpServer((_, res) => LoopbackHttpServer.Respond(res, 200, "ok"));
        Environment.SetEnvironmentVariable(TokenVar, "s3cret");
        try
        {
            using var client = RemoteAgentHttpClient.Create(Config(server.BaseUrl, ("Authorization", $"Bearer ${{{TokenVar}}}")));

            await client.GetAsync(server.BaseUrl + "/.well-known/agent-card.json");

            Assert.True(server.SawHeader("Authorization", "Bearer s3cret"));
        }
        finally { Environment.SetEnvironmentVariable(TokenVar, null); }
    }

    [Fact]
    public async Task Create_DoesNotSendConfiguredHeadersToAnotherOrigin()
    {
        await using var configured = new LoopbackHttpServer((_, res) => LoopbackHttpServer.Respond(res, 200, "ok"));
        await using var elsewhere  = new LoopbackHttpServer((_, res) => LoopbackHttpServer.Respond(res, 200, "ok"));
        using var client = RemoteAgentHttpClient.Create(Config(configured.BaseUrl, ("X-Api-Key", "k1")));

        await client.GetAsync(elsewhere.BaseUrl + "/");

        Assert.Single(elsewhere.Snapshot());
        Assert.False(elsewhere.SawHeader("X-Api-Key"));
    }

    [Fact]
    public async Task Create_DropsConfiguredHeadersOnACrossOriginRedirect()
    {
        await using var other  = new LoopbackHttpServer((_, res) => LoopbackHttpServer.Respond(res, 200, "landed"));
        await using var origin = new LoopbackHttpServer((_, res) => LoopbackHttpServer.RedirectTo(res, other.BaseUrl + "/next"));
        using var client = RemoteAgentHttpClient.Create(Config(origin.BaseUrl, ("X-Api-Key", "k1")));

        var response = await client.GetAsync(origin.BaseUrl + "/");

        Assert.Equal("landed", await response.Content.ReadAsStringAsync());
        Assert.True(origin.SawHeader("X-Api-Key", "k1"));
        Assert.False(other.SawHeader("X-Api-Key"));
    }

    [Fact]
    public void UnsetEnvTokens_ReportsOnlyVariablesThatAreNotSet()
    {
        Environment.SetEnvironmentVariable(TokenVar, "set");
        try
        {
            var unset = RemoteAgentHttpClient.UnsetEnvTokens(Config("http://127.0.0.1:1",
                ("Authorization", $"Bearer ${{{TokenVar}}}"),
                ("X-Api-Key", "${FUSERAFT_TEST_REMOTE_AGENT_MISSING}")));

            var (header, variable) = Assert.Single(unset);
            Assert.Equal("X-Api-Key", header);
            Assert.Equal("FUSERAFT_TEST_REMOTE_AGENT_MISSING", variable);
        }
        finally { Environment.SetEnvironmentVariable(TokenVar, null); }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401 (Unauthorized). Check RemoteAgent.Headers")]
    [InlineData(HttpStatusCode.Forbidden, "403 (Forbidden). Check RemoteAgent.Headers")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "503 (ServiceUnavailable).")]
    public void DescribeCardFailure_NamesTheStatusTheSdkWrapped(HttpStatusCode status, string expected)
    {
        var ex = new Exception("HTTP request failed", new HttpRequestException("x", null, status));

        var message = RemoteAgentHttpClient.DescribeCardFailure("Reviewer", "http://h:1/", ex);

        Assert.Contains("Agent 'Reviewer'", message);
        Assert.Contains("http://h:1/.well-known/agent-card.json", message);
        Assert.Contains(expected, message);
        if (status == HttpStatusCode.ServiceUnavailable) Assert.DoesNotContain("Headers", message);
    }

    [Fact]
    public void DescribeCardFailure_UsesTheConnectionErrorWhenThereIsNoStatus()
    {
        var ex = new Exception("HTTP request failed", new HttpRequestException("Connection refused (h:1)"));

        Assert.Contains("Connection refused (h:1)", RemoteAgentHttpClient.DescribeCardFailure("R", "http://h:1", ex));
    }

    private static RemoteAgentConfig Config(string url, params (string Name, string Value)[] headers) => new()
    {
        Url     = url,
        Headers = headers.ToDictionary(h => h.Name, h => h.Value),
    };
}
