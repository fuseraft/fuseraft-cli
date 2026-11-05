using fuseraft.Cli;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace FuseraftCli.Tests;

/// <summary>
/// <see cref="SystemPromptBuilder"/> — a remote (A2A) agent gets only its own instructions and the
/// spec; the base prompt and local environment blocks stay with local agents.
/// </summary>
public sealed class SystemPromptBuilderTests
{
    [Fact]
    public async Task BuildSystemPrompt_RemoteAgentGetsOnlyItsInstructionsAndTheSpec()
    {
        var config = new OrchestrationConfig
        {
            Agents =
            [
                new AgentConfig { Name = "Local",  Instructions = "Local own." },
                new AgentConfig { Name = "Remote", Instructions = "Remote own.", RemoteAgent = new RemoteAgentConfig { Url = "http://127.0.0.1:1" } },
            ],
        };

        var built = await SystemPromptBuilder.BuildSystemPrompt(
            config, "orchestration.yaml", "s1", "Spec body.", NullLoggerFactory.Instance, CancellationToken.None);

        var remote = built.Agents.Single(a => a.Name == "Remote").Instructions;
        Assert.StartsWith("Remote own.", remote);
        Assert.Contains("Spec body.", remote);
        Assert.DoesNotContain("Runtime Environment", remote);
        Assert.DoesNotContain(Environment.CurrentDirectory, remote);

        var local = built.Agents.Single(a => a.Name == "Local").Instructions;
        Assert.Contains("Local own.", local);
        Assert.Contains("Spec body.", local);
        Assert.Contains("Runtime Environment", local);
    }

    [Fact]
    public async Task BuildSystemPrompt_PluginToolGuidanceOnlyForAgentsWithThePlugin()
    {
        var config = new OrchestrationConfig
        {
            Agents =
            [
                new AgentConfig { Name = "Bare", Instructions = "Bare own." },
                new AgentConfig { Name = "Full", Instructions = "Full own.", Plugins = ["subagent", "SessionContext"] },
            ],
        };

        var built = await SystemPromptBuilder.BuildSystemPrompt(
            config, "orchestration.yaml", "s1", null, NullLoggerFactory.Instance, CancellationToken.None);

        var bare = built.Agents.Single(a => a.Name == "Bare").Instructions;
        Assert.DoesNotContain("session_context_read", bare);
        Assert.DoesNotContain("subagent_", bare);

        var full = built.Agents.Single(a => a.Name == "Full").Instructions;
        Assert.Contains("session_context_read", full);
        Assert.Contains("subagent_explore_many", full);
    }

    [Fact]
    public void BuildPluginGuidanceBlock_NullWithoutGuidedPlugins() =>
        Assert.Null(SystemPromptBuilder.BuildPluginGuidanceBlock(["FileSystem", "Shell"]));
}
