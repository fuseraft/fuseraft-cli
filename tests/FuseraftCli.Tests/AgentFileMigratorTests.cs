using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;
using fuseraft.Cli.Team;
using fuseraft.Core.Models;

namespace fuseraft.Tests;

public class AgentFileMigratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"migrate-{Guid.NewGuid():N}");

    public AgentFileMigratorTests() => Directory.CreateDirectory(Path.Combine(_dir, "agents"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Team(string agents)
    {
        var path = Path.Combine(_dir, "team.yaml");
        File.WriteAllText(path, $"""
            team: T
            agents:
            {agents}
            flow:
              start: Writer
              steps:
                Writer:
                  - to: end
            """);
        return path;
    }

    private void Agent(string name, string yaml) => File.WriteAllText(Path.Combine(_dir, "agents", name), yaml);

    private static OrchestrationConfig Load(string teamPath, AgentFileMigrator.Result result) =>
        TeamCompiler.Compile(TeamConfigLoader.Load(
            TeamConfigLoader.ParseObject(result.TeamText, teamPath), teamPath,
            result.Files.ToDictionary(f => f.Path, f => f.Content)));

    [Fact]
    public void Applies_OnlyToTeamsWithAgentFile()
    {
        Assert.True(AgentFileMigrator.Applies(JsonNode.Parse("""{"team":"T","agents":{"A":{"agentFile":"a.yaml"}}}""")!.AsObject()));
        Assert.False(AgentFileMigrator.Applies(JsonNode.Parse("""{"team":"T","agents":{"A":{"instructions":"x"}}}""")!.AsObject()));
        Assert.False(AgentFileMigrator.Applies(JsonNode.Parse("""{"Orchestration":{}}""")!.AsObject()));
    }

    [Fact]
    public void AgentFile_BecomesMarkdown_WithInlineFieldsWinning()
    {
        Agent("writer.yaml", "Role: From file\nInstructions: File text.\nTools: [Shell]\n");
        var team = Team("  Writer:\n    agentFile: agents/writer.yaml\n    role: Inline role");

        var result = AgentFileMigrator.Convert(team);

        var (path, content) = Assert.Single(result.Files);
        Assert.Equal(Path.Combine(_dir, "agents", "writer.md"), path);
        Assert.StartsWith("---\nrole: Inline role\ntools: [Shell]\n---\nFile text.\n", content);
        Assert.Contains("Writer: agents/writer.md", result.TeamText);
        Assert.DoesNotContain("agentFile", result.TeamText);

        var agent = Load(team, result).Agents.Single();
        Assert.Equal("Inline role", agent.Description);
        Assert.StartsWith("File text.", agent.Instructions);
    }

    [Fact]
    public void SharedFile_WithDifferentOverrides_GetsOneFilePerAgent()
    {
        Agent("base.yaml", "instructions: Shared text.\n");
        var team = Team("  Writer:\n    agentFile: agents/base.yaml\n    role: A\n  Other:\n    agentFile: agents/base.yaml\n    role: B");

        var result = AgentFileMigrator.Convert(team);

        Assert.Equal(["base-other.md", "base-writer.md"], result.Files.Select(f => Path.GetFileName(f.Path)).Order());
        Assert.Contains("Other: agents/base-other.md", result.TeamText);
    }

    [Fact]
    public void SharedFile_WithoutOverrides_StaysOneFile()
    {
        Agent("base.yaml", "instructions: Shared text.\n");
        var result = AgentFileMigrator.Convert(Team("  Writer:\n    agentFile: agents/base.yaml\n  Other:\n    agentFile: agents/base.yaml"));

        Assert.Equal("base.md", Path.GetFileName(Assert.Single(result.Files).Path));
    }

    [Fact]
    public void ExistingDifferentMarkdown_IsRefused()
    {
        Agent("writer.yaml", "instructions: File text.\n");
        Agent("writer.md", "---\n---\nSomething else.\n");

        Assert.Contains("already exists", Assert.Throws<TeamConfigException>(
            () => AgentFileMigrator.Convert(Team("  Writer:\n    agentFile: agents/writer.yaml"))).Message);
    }

    [Fact]
    public void PascalCaseAgentFile_IsWrittenCamelCase_KeepingUserKeys()
    {
        Agent("writer.yaml", "Instructions: x\nModel:\n  ModelId: m\n  DefaultHeaders: { X-Api-Key: k }\n");
        var content = Assert.Single(AgentFileMigrator.Convert(Team("  Writer:\n    agentFile: agents/writer.yaml")).Files).Content;

        Assert.Contains("model:\n  modelId: m\n  defaultHeaders:\n    X-Api-Key: k\n", content);
    }

    [Fact]
    public void MissingAgentFile_IsRejected() =>
        Assert.Contains("not found", Assert.Throws<TeamConfigException>(
            () => AgentFileMigrator.Convert(Team("  Writer:\n    agentFile: agents/nope.yaml"))).Message);
}
