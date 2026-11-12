using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Agents;
using fuseraft.Infrastructure.Plugins;
using Microsoft.Extensions.AI;
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
    public void SubagentPlugin_ExposesExploreAndLocate_ButNotDelegate()
    {
        var names = ResolveToolNames("Subagent");

        Assert.Contains("subagent_explore", names);
        Assert.Contains("subagent_locate", names);
        Assert.DoesNotContain(SubagentPlugin.DelegateToolName, names);
    }

    [Fact]
    public void DelegateToolName_MatchesTheNameReflectionGivesDelegateAsync()
    {
        var names = PluginRegistry.GetFunctionsFromObject(new SubagentPlugin(chatClient: null, explorerTools: []))
            .Select(f => f.Name);

        Assert.Contains(SubagentPlugin.DelegateToolName, names);
    }

    [Fact]
    public void McpServersWithClashingTools_GetUniqueNames_AndBuiltInsKeepTheirs()
    {
        // Two MCP servers (registered as AI functions) sharing query_database, one also shadowing a built-in.
        _registry.RegisterAIFunctions("Sql",   [Fake("query_database"), Fake("read_file")]);
        _registry.RegisterAIFunctions("Local", [Fake("query_database"), Fake("list_tables")]);

        var names = Resolve(Agent("FileSystem", "Sql", "Local")).Select(t => t.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("read_file", names);
        Assert.Contains("Sql_read_file", names);
        Assert.Contains("Sql_query_database", names);
        Assert.Contains("Local_query_database", names);
        Assert.Contains("list_tables", names);
        Assert.DoesNotContain("query_database", names);
    }

    [Fact]
    public void SubagentPlugins_WithClashingMcpTools_GetUniqueNames()
    {
        _registry.RegisterAIFunctions("Sql",   [Fake("query_database")]);
        _registry.RegisterAIFunctions("Local", [Fake("query_database")]);

        var tools = AgentToolResolver.BuildSubagentTools(Agent(["Sql", "Local"], "Subagent"), _registry, null);

        Assert.Equal(["Sql_query_database", "Local_query_database"], tools.Select(t => t.Name));
    }

    private static AIFunction Fake(string name) => AIFunctionFactory.Create(() => name, name, $"Fake {name}.");

    private static AgentConfig Agent(params string[] plugins) => Agent(null, plugins);

    private static AgentConfig Agent(List<string>? subagentPlugins, params string[] plugins) => new()
    {
        Name            = "TestAgent",
        Instructions    = "You are a test agent.",
        Plugins         = [.. plugins],
        SubagentPlugins = subagentPlugins,
        Model        = new ModelConfig
        {
            ModelId      = "grok-4-1-fast-reasoning",
            Endpoint     = "https://api.x.ai/v1",
            ApiKeyEnvVar = FakeApiKeyVar,
        },
    };

    private List<AIFunction> Resolve(AgentConfig config)
    {
        var factory  = new ChatClientFactory();
        var resolver = new AgentToolResolver(factory, _registry, null, null, null, null);
        return resolver.ConvertPluginTools(config, factory.Resolve(config.Model), null, [], new object());
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
