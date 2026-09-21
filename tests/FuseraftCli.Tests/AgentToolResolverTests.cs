using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Agents;
using fuseraft.Infrastructure.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace FuseraftCli.Tests;

[Collection("FuseraftTestApiKeyEnv")]
public sealed class AgentToolResolverTests : IDisposable
{
    private const string FakeApiKeyVar = "FUSERAFT_TEST_API_KEY";

    private readonly PluginRegistry _registry;

    public AgentToolResolverTests()
    {
        Environment.SetEnvironmentVariable(FakeApiKeyVar, "sk-test-key-not-used-in-unit-tests");
        _registry = new PluginRegistry(NullLoggerFactory.Instance).RegisterDefaults();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FakeApiKeyVar, null);
        _registry.Dispose();
    }

    [Fact]
    public void SubAgentPlugin_ExposesExploreAndLocate_ButNotDelegate()
    {
        var names = ResolveToolNames("SubAgent");

        Assert.Contains("subagent_explore", names);
        Assert.Contains("subagent_locate", names);
        Assert.DoesNotContain(SubAgentPlugin.DelegateToolName, names);
    }

    [Fact]
    public void DelegateToolName_MatchesTheNameReflectionGivesDelegateAsync()
    {
        var names = PluginRegistry.GetFunctionsFromObject(new SubAgentPlugin(chatClient: null, explorerTools: []))
            .Select(f => f.Name);

        Assert.Contains(SubAgentPlugin.DelegateToolName, names);
    }

    private List<string> ResolveToolNames(params string[] plugins)
    {
        var config = new AgentConfig
        {
            Name         = "TestAgent",
            Instructions = "You are a test agent.",
            Plugins      = [.. plugins],
            Model        = new ModelConfig
            {
                ModelId      = "grok-4-1-fast-reasoning",
                Endpoint     = "https://api.x.ai/v1",
                ApiKeyEnvVar = FakeApiKeyVar,
            },
        };

        var factory  = new ChatClientFactory();
        var resolver = new AgentToolResolver(factory, _registry, null, null, null, null);
        var tools    = resolver.ConvertPluginTools(config, factory.Resolve(config.Model), null, [], new object());
        return tools.Select(t => t.Name).ToList();
    }
}
