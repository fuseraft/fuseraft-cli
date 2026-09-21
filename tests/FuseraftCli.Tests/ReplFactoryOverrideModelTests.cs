using fuseraft.Cli.Commands.Repl;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Chat;

namespace FuseraftCli.Tests;

/// <summary>
/// <c>memory.model</c> / <c>subagent.model</c> resolution. The REPL's factory has no Models registry,
/// so before this a model ID the prefix table didn't recognize threw, and a recognized one always went
/// to that vendor's own endpoint with that vendor's key — unusable behind a gateway.
/// </summary>
public sealed class ReplFactoryOverrideModelTests
{
    private static readonly ModelConfig Main = new()
    {
        ModelId         = "main-model",
        Provider        = "openai",
        Endpoint        = "https://gateway.example/v1",
        ApiKey          = "sk-main",
        ReasoningEffort = "high",
    };

    private static ModelConfig Resolve(string modelId, string? provider = null, string? endpoint = null, string? keyVar = null)
    {
        using var factory = new ChatClientFactory();
        return ReplFactory.ResolveOverrideModel(factory, Main, modelId, provider, endpoint, keyVar);
    }

    [Fact]
    public void RecognizedPrefix_GoesToThatVendor_WithoutBorrowingTheMainKey()
    {
        var r = Resolve("gpt-4o-mini");

        Assert.Equal("gpt-4o-mini", r.ModelId);
        Assert.Equal("https://api.openai.com/v1", r.Endpoint);
        Assert.Equal("OPENAI_API_KEY", r.ApiKeyEnvVar);
        Assert.Equal(string.Empty, r.ApiKey);
    }

    [Fact]
    public void UnrecognizedPrefix_RidesTheMainProvidersConnection_InsteadOfThrowing()
    {
        var r = Resolve("openrouter/some-model");

        Assert.Equal("openrouter/some-model", r.ModelId);
        Assert.Equal("https://gateway.example/v1", r.Endpoint);
        Assert.Equal("sk-main", r.ApiKey);
        Assert.Equal("openai", r.Provider);
    }

    [Fact]
    public void Inheriting_DoesNotCarryTheMainModelsReasoningEffort()
    {
        Assert.Null(Resolve("openrouter/some-model").ReasoningEffort);
    }

    [Fact]
    public void ExplicitEndpoint_OverridesThePrefixTable_AndReusesTheMainKey()
    {
        // A LiteLLM-style gateway that serves gpt-4o-mini itself.
        var r = Resolve("gpt-4o-mini", endpoint: "https://litellm.example/v1");

        Assert.Equal("https://litellm.example/v1", r.Endpoint);
        Assert.Equal("sk-main", r.ApiKey);
        Assert.Equal("openai", r.Provider);
    }

    [Fact]
    public void ExplicitEndpointWithItsOwnKeyVar_DoesNotLeakTheMainKeyToIt()
    {
        var r = Resolve("gpt-4o-mini", endpoint: "https://other.example/v1", keyVar: "OTHER_KEY");

        Assert.Equal("https://other.example/v1", r.Endpoint);
        Assert.Equal("OTHER_KEY", r.ApiKeyEnvVar);
        Assert.Equal(string.Empty, r.ApiKey);
    }

    [Fact]
    public void ExplicitKeyVarAlone_KeepsThePrefixDetectedEndpoint()
    {
        var r = Resolve("gpt-4o-mini", keyVar: "MY_OPENAI_KEY");

        Assert.Equal("https://api.openai.com/v1", r.Endpoint);
        Assert.Equal("MY_OPENAI_KEY", r.ApiKeyEnvVar);
    }

    [Fact]
    public void ExplicitKeyVar_WithAnUnrecognizedPrefix_UsesTheMainEndpointButNotTheMainKey()
    {
        var r = Resolve("openrouter/some-model", keyVar: "GATEWAY_KEY_2");

        Assert.Equal("https://gateway.example/v1", r.Endpoint);
        Assert.Equal("GATEWAY_KEY_2", r.ApiKeyEnvVar);
        Assert.Equal(string.Empty, r.ApiKey);
    }

    [Fact]
    public void ExplicitProvider_OverridesTheDetectedOne()
    {
        var r = Resolve("claude-haiku-4-5", provider: "openai");

        Assert.Equal("openai", r.Provider);
    }

    [Fact]
    public void WhitespaceOnlyOverrides_AreTreatedAsUnset()
    {
        var r = Resolve("gpt-4o-mini", provider: " ", endpoint: "", keyVar: "  ");

        Assert.Equal("https://api.openai.com/v1", r.Endpoint);
        Assert.Equal("OPENAI_API_KEY", r.ApiKeyEnvVar);
    }
}
