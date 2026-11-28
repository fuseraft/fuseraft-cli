using fuseraft.Core.Models;
using fuseraft.Infrastructure.Chat;

namespace FuseraftCli.Tests;

/// <summary>A provider preset's key variable goes only to that provider's own server.</summary>
public sealed class ChatClientFactoryPresetKeyTests
{
    private static ModelConfig Resolve(ModelConfig config)
    {
        using var factory = new ChatClientFactory();
        return factory.Resolve(config);
    }

    [Fact]
    public void PresetServer_GetsThePresetsVariable() =>
        Assert.Equal("ANTHROPIC_API_KEY", Resolve(new ModelConfig { ModelId = "claude-sonnet-4-6" }).ApiKeyEnvVar);

    [Fact]
    public void PresetServer_UnderAnotherPath_IsStillItsServer() =>
        Assert.Equal("OPENAI_API_KEY",
            Resolve(new ModelConfig { ModelId = "gpt-4o", Endpoint = "https://api.openai.com/v1/" }).ApiKeyEnvVar);

    [Fact]
    public void AnotherServer_DoesNotGetThePresetsVariable()
    {
        var resolved = Resolve(new ModelConfig { ModelId = "claude-sonnet-4-6", Endpoint = "https://gateway.example/v1" });

        Assert.Equal("https://gateway.example/v1", resolved.Endpoint);
        Assert.Equal(string.Empty, resolved.ApiKeyEnvVar);
    }

    [Fact]
    public void NamedVariable_IsKept() =>
        Assert.Equal("GATEWAY_KEY",
            Resolve(new ModelConfig { ModelId = "claude-sonnet-4-6", Endpoint = "https://gateway.example/v1", ApiKeyEnvVar = "GATEWAY_KEY" }).ApiKeyEnvVar);
}
