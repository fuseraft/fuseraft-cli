using fuseraft.Cli.Commands.Repl;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Chat;

namespace FuseraftCli.Tests;

/// <summary><see cref="ChatClientFactory"/> sends a keychain key only to the endpoint origin it was saved for.</summary>
public sealed class ChatClientFactoryKeyOriginTests
{
    private const string XaiOrigin = "https://api.x.ai:443";

    private static ModelConfig Model(string endpoint, string origin) => new()
    {
        ModelId      = "grok-4.7",
        Provider     = "openai",
        Endpoint     = endpoint,
        ApiKey       = "xai-key",
        ApiKeyOrigin = origin,
    };

    [Fact]
    public void KeyFromItsOwnOrigin_IsUsed()
    {
        using var factory = new ChatClientFactory();

        Assert.NotNull(factory.Create(Model("https://api.x.ai/v1", XaiOrigin)));
    }

    [Fact]
    public void KeyFromAnotherOrigin_IsDropped()
    {
        using var factory = new ChatClientFactory();

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Create(Model("https://gateway.example.com/v1", XaiOrigin)));
        Assert.Contains("No API key available", ex.Message);
    }

    [Fact]
    public void UnscopedKey_GoesWhereverItIsConfigured()
    {
        using var factory = new ChatClientFactory();

        Assert.NotNull(factory.Create(Model("https://gateway.example.com/v1", origin: "")));
    }

    [Fact]
    public void SessionEndpointOverride_DoesNotCarryTheConfigsKey()
    {
        var cfg = new UserConfig
        {
            ModelId = "grok-4.7", Provider = "openai", Endpoint = "https://api.x.ai/v1",
            ApiKey = "xai-key", ApiKeyOrigin = XaiOrigin,
        };
        var model = ReplFactory.BuildModelConfig("some-model", cfg,
            connection: ConnectionOverride.From("openai", "https://gateway.example.com/v1"));
        using var factory = new ChatClientFactory();

        Assert.Equal(XaiOrigin, model.ApiKeyOrigin);
        var ex = Assert.Throws<InvalidOperationException>(() => factory.Create(model));
        Assert.Contains("No API key available", ex.Message);
    }

    [Fact]
    public void LocalOpenAiCompatibleServer_WorksWithoutAKey()
    {
        using var factory = new ChatClientFactory();

        Assert.NotNull(factory.Create(Model("http://localhost:11434/v1", XaiOrigin)));
    }

    [Theory]
    [InlineData("grok-4.7", "openai", "https://api.x.ai/v1", "https://api.x.ai/v1")]
    [InlineData("grok-4.7", "", "", "https://api.x.ai/v1")]
    [InlineData("claude-opus-4-7", "", "", "https://api.anthropic.com")]
    [InlineData("my-model", "anthropic", "", "https://api.anthropic.com")]
    [InlineData("llama3.1:8b", "", "", "http://localhost:11434")]
    [InlineData("my-model", "ollama", "", "http://localhost:11434")]
    [InlineData("my-model", "openai", "", "")]
    public void EndpointFor_MatchesWhereTheModelIsCalled(string modelId, string provider, string endpoint, string expected) =>
        Assert.Equal(expected, ChatClientFactory.EndpointFor(modelId, provider, endpoint));

    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:8080/v1", true)]
    [InlineData("https://gateway.example.com/v1", false)]
    [InlineData("", false)]
    public void UserConfig_WithALocalEndpoint_IsConfiguredWithoutAKey(string endpoint, bool expected) =>
        Assert.Equal(expected, new UserConfig { ModelId = "llama3.2", Provider = "openai", Endpoint = endpoint }.IsConfigured);
}
