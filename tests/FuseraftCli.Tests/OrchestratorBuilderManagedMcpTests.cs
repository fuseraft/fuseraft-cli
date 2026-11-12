using fuseraft.Cli;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Mcp;
using fuseraft.Infrastructure.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace FuseraftCli.Tests;

public sealed class OrchestratorBuilderManagedMcpTests
{
    [Fact]
    public async Task UnreachableServer_IsRegisteredWithNoTools_SoTheAgentStillBuilds()
    {
        var config = new OrchestrationConfig
        {
            Agents = [new AgentConfig { Name = "Analyst", Plugins = ["Sql"] }],
        };
        // Port 1 on loopback refuses at once.
        var managed  = new[] { new McpServerConfig { Name = "Sql", Transport = "http", Url = "http://127.0.0.1:1/mcp", TransportMode = "streamable-http" } };
        var registry = new PluginRegistry();
        await using var manager = new McpSessionManager();

        await OrchestratorBuilder.ConnectManagedMcpServersAsync(config, managed, manager, registry,
            NullLogger.Instance, CancellationToken.None);

        Assert.True(registry.TryGetAIFunctions("Sql", out var tools));
        Assert.Empty(tools);
    }

    [Fact]
    public async Task ServerNoAgentNames_IsNotConnected()
    {
        var config   = new OrchestrationConfig { Agents = [new AgentConfig { Name = "Dev", Plugins = ["FileSystem"] }] };
        var managed  = new[] { new McpServerConfig { Name = "Sql", Transport = "http", Url = "http://127.0.0.1:1/mcp" } };
        var registry = new PluginRegistry();
        await using var manager = new McpSessionManager();

        await OrchestratorBuilder.ConnectManagedMcpServersAsync(config, managed, manager, registry,
            NullLogger.Instance, CancellationToken.None);

        Assert.False(registry.TryGetAIFunctions("Sql", out _));
    }
}
