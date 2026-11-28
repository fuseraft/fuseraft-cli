using fuseraft.Infrastructure.Chat;

namespace FuseraftCli.Tests;

public sealed class ChatClientFactoryDefaultEndpointTests
{
    [Theory]
    [InlineData("anthropic", "https://api.anthropic.com")]
    [InlineData(" XAI ", "https://api.x.ai/v1")]
    [InlineData("ollama", "http://localhost:11434")]
    [InlineData("google", "https://generativelanguage.googleapis.com/v1beta/openai")]
    public void KnownProvider_HasItsPublicEndpoint(string provider, string endpoint) =>
        Assert.Equal(endpoint, ChatClientFactory.DefaultEndpoint(provider));

    [Theory]
    [InlineData("azure")]
    [InlineData("other")]
    public void ProviderWithoutAPublicEndpoint_IsNull(string provider) =>
        Assert.Null(ChatClientFactory.DefaultEndpoint(provider));
}
