using fuseraft.Cli.Commands.Repl;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Chat;

namespace FuseraftCli.Tests;

/// <summary><c>fuseraft repl --provider/--endpoint</c>: one session's connection, over the config's.</summary>
public sealed class ReplFactoryConnectionTests
{
    private static readonly UserConfig Config = new()
    {
        ModelId  = "grok-4.7",
        Provider = "openai",
        Endpoint = "https://api.x.ai/v1",
        ApiKey   = "sk-config",
    };

    [Fact]
    public void NoFlags_UsesTheConfigsConnection()
    {
        Assert.Null(ConnectionOverride.From(" ", null));

        var c = ReplFactory.BuildModelConfig("grok-4.7", Config, connection: null);

        Assert.Equal("https://api.x.ai/v1", c.Endpoint);
        Assert.Equal("openai", c.Provider);
    }

    [Fact]
    public void BothFlags_ReplaceTheConfigsConnection_ButKeepItsKey()
    {
        var c = ReplFactory.BuildModelConfig("llama3.1:8b", Config,
            connection: ConnectionOverride.From("ollama", " http://gpu-box:11434 "));

        Assert.Equal("http://gpu-box:11434", c.Endpoint);
        Assert.Equal("ollama", c.Provider);
        Assert.Equal("sk-config", c.ApiKey);
    }

    [Fact]
    public void ProviderAlone_DropsTheConfigsEndpoint_SoItsDefaultApplies()
    {
        var c = ReplFactory.BuildModelConfig("claude-sonnet-4-6", Config, connection: ConnectionOverride.From("anthropic", null));

        Assert.Equal(string.Empty, c.Endpoint);
        using var factory = new ChatClientFactory();
        Assert.Equal("https://api.anthropic.com", factory.Resolve(c with { ApiKey = "" }).Endpoint);
    }

    [Fact]
    public void EndpointAlone_DropsTheConfigsProvider_AndIsOpenAiCompatible()
    {
        var c = ReplFactory.BuildModelConfig("acme-model", Config, connection: ConnectionOverride.From(null, "https://gateway.example/v1"));

        Assert.Equal(string.Empty, c.Provider);
        using var factory = new ChatClientFactory();
        var resolved = factory.Resolve(c);
        Assert.Equal("openai", resolved.Provider);
        Assert.Equal("https://gateway.example/v1", resolved.Endpoint);
    }
}
