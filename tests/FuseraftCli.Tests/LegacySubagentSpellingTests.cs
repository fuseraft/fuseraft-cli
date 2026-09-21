using fuseraft.Cli;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Plugins;
using Microsoft.Extensions.Configuration;

namespace FuseraftCli.Tests;

/// <summary>
/// The "SubAgent" spelling was renamed to "Subagent". Configs written before that still say
/// <c>SubAgentModel</c> / <c>Plugins: [SubAgent]</c>; both bind and resolve case-insensitively,
/// and this pins that so a future binder or registry change can't silently break them.
/// </summary>
public sealed class LegacySubagentSpellingTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fuseraft-legacy-{Guid.NewGuid():N}.yaml");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void OldSpelling_InAnOrchestrationYaml_StillBinds()
    {
        File.WriteAllText(_path, """
            Orchestration:
              Agents:
                - Name: Archaeologist
                  Instructions: explore
                  Plugins:
                    - SubAgent
                  SubAgentModel: claude-haiku-4-5-20251001
                  SubAgentMaxToolCalls: 30
                  SubAgentPlugins:
                    - FileSystem
            """);

        var agent = YamlConfigLoader.LoadAsConfiguration(_path)
            .GetSection("Orchestration").Get<OrchestrationConfig>()!.Agents.Single();

        Assert.Contains("SubAgent", agent.Plugins);
        Assert.Equal("claude-haiku-4-5-20251001", agent.SubagentModel);
        Assert.Equal(30, agent.SubagentMaxToolCalls);
        Assert.Equal(["FileSystem"], agent.SubagentPlugins);
    }

    [Theory]
    [InlineData("SubAgent")]
    [InlineData("Subagent")]
    [InlineData("subagent")]
    public void PluginRegistry_ResolvesEitherSpelling(string name)
    {
        using var registry = new PluginRegistry().RegisterDefaults();

        Assert.True(registry.TryGet(name, out _));
    }
}
