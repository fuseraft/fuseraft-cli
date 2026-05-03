using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests that <see cref="AgentFactory"/> rejects invalid configurations before making
/// any network calls.
/// </summary>
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
