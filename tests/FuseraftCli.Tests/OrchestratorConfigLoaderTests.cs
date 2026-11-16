using fuseraft.Cli;
using fuseraft.Core.Models.Agents;
using fuseraft.Core.Models.Config;
using fuseraft.Core.Models.Orchestration;

namespace FuseraftCli.Tests;

public sealed class OrchestratorConfigLoaderTests
{
    private static readonly UserConfig XaiDefault = new()
    {
        ModelId = "grok-4.7", Endpoint = "https://api.x.ai/v1", Provider = "openai", ApiKeyEnvVar = "XAI_API_KEY",
    };

    private static ModelConfig Filled(ModelConfig model) =>
        OrchestratorConfigLoader.ApplyGlobalDefaults(
            new OrchestrationConfig { Agents = [new AgentConfig { Name = "A", Model = model }] }, XaiDefault)
        .Agents[0].Model;

    [Fact]
    public void ModelOnAnotherProvider_KeepsItsOwnEndpointAndKey()
    {
        var m = Filled(new ModelConfig { ModelId = "claude-haiku-4-5", ApiKeyEnvVar = "ANTHROPIC_API_KEY" });

        Assert.Equal(string.Empty, m.Endpoint);
        Assert.Equal("ANTHROPIC_API_KEY", m.ApiKeyEnvVar);
    }

    [Fact]
    public void ModelOnAnotherProvider_DoesNotInheritTheGlobalKeyVar() =>
        Assert.Equal(string.Empty, Filled(new ModelConfig { ModelId = "claude-haiku-4-5" }).ApiKeyEnvVar ?? string.Empty);

    [Fact]
    public void ModelOnTheGlobalProvider_InheritsEndpointAndKey()
    {
        var m = Filled(new ModelConfig { ModelId = "grok-4.3" });

        Assert.Equal("https://api.x.ai/v1", m.Endpoint);
        Assert.Equal("XAI_API_KEY", m.ApiKeyEnvVar);
    }

    [Fact]
    public void UnrecognizedModel_InheritsTheGlobalConnection() =>
        Assert.Equal("https://api.x.ai/v1", Filled(new ModelConfig { ModelId = "house-model-7b" }).Endpoint);

    [Fact]
    public void NoModelId_TakesTheGlobalModelAndConnection()
    {
        var m = Filled(new ModelConfig());

        Assert.Equal("grok-4.7", m.ModelId);
        Assert.Equal("https://api.x.ai/v1", m.Endpoint);
    }
}
