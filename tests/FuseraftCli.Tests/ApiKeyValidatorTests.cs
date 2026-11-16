using fuseraft.Cli;
using fuseraft.Core.Models.Config;

namespace FuseraftCli.Tests;

public sealed class ApiKeyValidatorTests
{
    [Fact]
    public void ModelWithoutEndpoint_ProbesItsInferredProvider()
    {
        using var probe = ApiKeyValidator.BuildProbe(new ModelConfig { ModelId = "grok-4.3" }, "k")!;

        Assert.Equal("https://api.x.ai/v1/models", probe.RequestUri!.ToString());
        Assert.Equal("Bearer", probe.Headers.Authorization!.Scheme);
    }

    [Fact]
    public void ClaudeModel_UsesAnthropicHeaders()
    {
        using var probe = ApiKeyValidator.BuildProbe(new ModelConfig { ModelId = "claude-haiku-4-5" }, "k")!;

        Assert.Equal("https://api.anthropic.com/v1/models", probe.RequestUri!.ToString());
        Assert.Equal(["k"], probe.Headers.GetValues("x-api-key"));
        Assert.True(probe.Headers.Contains("anthropic-version"));
        Assert.Null(probe.Headers.Authorization);
    }

    [Fact]
    public void ExplicitEndpoint_KeepsOnlyItsOrigin()
    {
        using var probe = ApiKeyValidator.BuildProbe(
            new ModelConfig { ModelId = "house-model", Endpoint = "http://llm.local:8080/v1/chat/completions" }, "k")!;

        Assert.Equal("http://llm.local:8080/v1/models", probe.RequestUri!.ToString());
    }

    [Fact]
    public void UnknownModelWithoutEndpoint_IsNotProbed() =>
        Assert.Null(ApiKeyValidator.BuildProbe(new ModelConfig { ModelId = "house-model" }, "k"));
}
