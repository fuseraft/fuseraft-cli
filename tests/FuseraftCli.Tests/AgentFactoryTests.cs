using System.Text.Json;
using fuseraft.Core.Exceptions;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Plugins;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests that <see cref="AgentFactory"/> rejects invalid configurations before making
/// any network calls.
/// </summary>
[Collection("FuseraftTestApiKeyEnv")]
public sealed class AgentFactoryTests : IDisposable
{
    // A real (but unused) API key so ChatClientFactory doesn't throw on the env var
    // check. Clients are constructed without making network calls, so any non-empty
    // value is sufficient here.
    private const string FakeApiKeyVar = "FUSERAFT_TEST_API_KEY";
    private const string FakeApiKey    = "sk-test-key-not-used-in-unit-tests";

    private readonly PluginRegistry _registry;
    private readonly AgentFactory   _factory;

    public AgentFactoryTests()
    {
        Environment.SetEnvironmentVariable(FakeApiKeyVar, FakeApiKey);

        _registry = new PluginRegistry(NullLoggerFactory.Instance).RegisterDefaults();
        _factory  = new AgentFactory(
            new ChatClientFactory(),
            _registry);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FakeApiKeyVar, null);
        _registry.Dispose();
    }

    // Name validation

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ThrowsArgumentException_WhenAgentNameIsEmpty(string name)
    {
        var config = ValidConfig() with { Name = name };

        Assert.Throws<ArgumentException>(() => _factory.Create(config));
    }

    // Unknown plugin

    [Fact]
    public void Create_ThrowsInvalidOperationException_WhenPluginIsUnknown()
    {
        var config = ValidConfig() with
        {
            Plugins = ["Shell", "NonExistentPlugin"]
        };

        Assert.Throws<InvalidOperationException>(() => _factory.Create(config));
    }

    // Valid config succeeds

    [Fact]
    public void Create_Succeeds_WhenConfigIsValid()
    {
        var config = ValidConfig();

        var agent = _factory.Create(config);

        Assert.Equal("TestAgent", agent.Name);
    }

    [Fact]
    public void Create_Succeeds_WithKnownPlugin()
    {
        var config = ValidConfig() with { Plugins = ["Shell"] };

        var agent = _factory.Create(config);

        Assert.NotNull(agent);
    }

    // "Self" is a declaration-of-intent skipped by AgentToolResolver.ConvertPluginTools
    // (like "Skills") and instead built by AgentFactory.Create itself, from the complete
    // resolved tool set — this exercises that end-to-end wiring doesn't throw regardless
    // of where "Self" appears in the declared plugin order.
    [Fact]
    public void Create_Succeeds_WithSelfPluginDeclaredLast()
    {
        var config = ValidConfig() with { Plugins = ["Shell", "Self"] };

        var agent = _factory.Create(config);

        Assert.NotNull(agent);
    }

    [Fact]
    public void Create_Succeeds_WithSelfPluginDeclaredFirst()
    {
        var config = ValidConfig() with { Plugins = ["Self", "Shell"] };

        var agent = _factory.Create(config);

        Assert.NotNull(agent);
    }

    // Remote (A2A) agents

    // Orchestrators look up Instructions, Isolation and routes by agent name; under the card's
    // name every lookup missed and the remote agent ran with no instructions at all.
    [Fact]
    public async Task Create_RemoteAgent_IsNamedAfterTheConfigNotTheCard_AndSendsHeadersAndSystemText()
    {
        LoopbackHttpServer? self = null;
        await using var remote = new LoopbackHttpServer(async (req, res) =>
        {
            res.ContentType = "application/json";
            if (req.HttpMethod == "GET")
            {
                await LoopbackHttpServer.Respond(res, 200, CardJson(req.Url!.GetLeftPart(UriPartial.Authority)));
                return;
            }
            var id = JsonDocument.Parse(self!.Snapshot()[^1].Body).RootElement.GetProperty("id").GetRawText();
            await LoopbackHttpServer.Respond(res, 200,
                """{"jsonrpc":"2.0","id":""" + id + ""","result":{"message":{"role":"ROLE_AGENT","parts":[{"text":"remote reply"}],"messageId":"m2","contextId":"c1"}}}""");
        });
        self = remote;
        var config = ValidConfig() with
        {
            Name        = "Reviewer",
            RemoteAgent = new RemoteAgentConfig
            {
                Url     = remote.BaseUrl,
                Headers = new() { ["Authorization"] = "Bearer t0ken" },
            },
        };

        var agent = _factory.Create(config);
        var response = await agent.RunAsync(
            [new ChatMessage(ChatRole.System, "You review code."), new ChatMessage(ChatRole.User, "Review this.")]);

        Assert.Equal("Reviewer", agent.Name);
        Assert.Equal("remote reply", response.Text);
        var requests = remote.Snapshot();
        Assert.All(requests, r => Assert.Equal("Bearer t0ken", r.Headers["Authorization"]));
        var send = Assert.Single(requests, r => r.Method == "POST");
        Assert.Contains("You review code.", send.Body);
        Assert.Contains("Review this.", send.Body);
    }

    // The SDK resolves the card path relative to the URL, so without a trailing slash a URL with a
    // path (one agent of several on a host, as `fuseraft serve --a2a` serves them) lost its last segment.
    [Fact]
    public async Task Create_RemoteAgent_UrlWithAPath_FetchesThatAgentsCard()
    {
        await using var remote = new LoopbackHttpServer((req, res) =>
            req.Url!.AbsolutePath == "/agents/Dev/.well-known/agent-card.json"
                ? LoopbackHttpServer.Respond(res, 200, CardJson(req.Url.GetLeftPart(UriPartial.Authority) + "/agents/Dev"))
                : LoopbackHttpServer.Respond(res, 404, "{}"));
        var config = ValidConfig() with { Name = "Dev", RemoteAgent = new RemoteAgentConfig { Url = remote.BaseUrl + "/agents/Dev" } };

        var agent = _factory.Create(config);

        Assert.Equal("Dev", agent.Name);
    }

    [Fact]
    public async Task Create_RemoteAgent_CardRejected_ReportsTheStatus()
    {
        await using var remote = new LoopbackHttpServer((_, res) => LoopbackHttpServer.Respond(res, 401, "{}"));
        var config = ValidConfig() with { Name = "Reviewer", RemoteAgent = new RemoteAgentConfig { Url = remote.BaseUrl } };

        var ex = Assert.Throws<AgentSetupException>(() => _factory.Create(config));

        Assert.Contains("Agent 'Reviewer'", ex.Message);
        Assert.Contains("401 (Unauthorized)", ex.Message);
    }

    [Fact]
    public void Create_RemoteAgent_Unreachable_ReportsTheConnectionError()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var config = ValidConfig() with { Name = "Reviewer", RemoteAgent = new RemoteAgentConfig { Url = $"http://127.0.0.1:{port}" } };

        var ex = Assert.Throws<AgentSetupException>(() => _factory.Create(config));

        Assert.Contains("Agent 'Reviewer'", ex.Message);
        Assert.DoesNotContain("HTTP request failed", ex.Message);
    }

    private static string CardJson(string url) => $$"""
        {"name":"CardName","description":"d","version":"1.0.0",
         "supportedInterfaces":[{"url":"{{url}}","protocolBinding":"JSONRPC","protocolVersion":"1.0"}],
         "capabilities":{"streaming":false,"pushNotifications":false},
         "skills":[],"defaultInputModes":["text/plain"],"defaultOutputModes":["text/plain"]}
        """;

    // Helpers

    private static AgentConfig ValidConfig() => new()
    {
        Name         = "TestAgent",
        Instructions = "You are a test agent.",
        Model        = new ModelConfig
        {
            ModelId     = "grok-4-1-fast-reasoning",
            Endpoint    = "https://api.x.ai/v1",
            ApiKeyEnvVar = FakeApiKeyVar
        }
    };
}
