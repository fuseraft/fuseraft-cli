using Microsoft.Extensions.Logging.Abstractions;
using fuseraft.Cli;
using fuseraft.Core.Models.Agents;
using fuseraft.Core.Models.Orchestration;
using fuseraft.Orchestration;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="OrchestratorConfigLoader.ValidateIsolationConstraints"/> — the guard
/// that keeps <c>Isolation: Fresh</c> (the new default) from silently starving Magentic's
/// manager/ledger loop, which structurally depends on every participant sharing the transcript.
/// </summary>
public sealed class OrchestratorConfigLoaderIsolationTests
{
    private static AgentConfig Agent(string name, AgentIsolation isolation) =>
        new() { Name = name, Isolation = isolation };

    [Fact]
    public void Magentic_config_with_a_fresh_agent_is_rejected()
    {
        var config = new OrchestrationConfig
        {
            Selection = new SelectionStrategyConfig { Type = OrchestratorTypes.Magentic },
            Agents =
            [
                Agent("Manager", AgentIsolation.Shared),
                Agent("Worker", AgentIsolation.Fresh),
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            OrchestratorConfigLoader.ValidateIsolationConstraints(config, NullLoggerFactory.Instance));

        Assert.Contains("magentic", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Worker", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Magentic_config_with_all_agents_shared_or_fork_is_accepted()
    {
        var config = new OrchestrationConfig
        {
            Selection = new SelectionStrategyConfig { Type = OrchestratorTypes.Magentic },
            Agents =
            [
                Agent("Manager", AgentIsolation.Shared),
                Agent("Worker", AgentIsolation.Fork),
            ],
        };

        var exception = Record.Exception(() =>
            OrchestratorConfigLoader.ValidateIsolationConstraints(config, NullLoggerFactory.Instance));

        Assert.Null(exception);
    }

    [Fact]
    public void Non_magentic_config_with_a_fresh_agent_is_accepted()
    {
        var config = new OrchestrationConfig
        {
            Selection = new SelectionStrategyConfig { Type = OrchestratorTypes.StateMachine },
            Agents = [Agent("Developer", AgentIsolation.Fresh)],
        };

        var exception = Record.Exception(() =>
            OrchestratorConfigLoader.ValidateIsolationConstraints(config, NullLoggerFactory.Instance));

        Assert.Null(exception);
    }
}
