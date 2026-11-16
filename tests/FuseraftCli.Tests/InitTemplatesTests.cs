using System.Text.RegularExpressions;
using fuseraft.Cli.Commands;
using fuseraft.Cli.Team;
using fuseraft.Core.Models;

namespace FuseraftCli.Tests;

/// <summary>Every template `fuseraft init` writes, and every shipped example, must load and route correctly.</summary>
public sealed class InitTemplatesTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var f in _files)
            try { File.Delete(f); } catch { /* best effort */ }
    }

    public static TheoryData<string, string?> TemplatesWithEndpoints()
    {
        var data = new TheoryData<string, string?>();
        foreach (var t in InitTemplatesReadingTests.Templates())
        {
            data.Add(t, null);
            data.Add(t, "https://example.com/v1");
        }
        return data;
    }

    public static TheoryData<string> Examples()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(ExamplesDir(), "*.yaml").Order())
            data.Add(Path.GetFileName(f));
        return data;
    }

    private static string ExamplesDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "config", "examples")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new DirectoryNotFoundException("config/examples"), "config", "examples");
    }

    private OrchestrationConfig CompileTemplate(string template, string? endpoint)
    {
        var generated = InitTemplates.Build(template, "gpt-4o", endpoint);
        Assert.Empty(generated.AgentFiles);
        var path = Path.Combine(Path.GetTempPath(), $"init-{template}-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, generated.MainConfig);
        _files.Add(path);
        return TeamConfigLoader.LoadAndCompile(path);
    }

    [Theory]
    [MemberData(nameof(TemplatesWithEndpoints))]
    public void Template_Compiles(string template, string? endpoint)
    {
        var cfg = CompileTemplate(template, endpoint);

        Assert.NotEmpty(cfg.Agents);
        var models = cfg.Models.Values.Concat(cfg.Agents.Select(a => a.Model));
        Assert.Contains(models, m => m.ModelId == "gpt-4o");
        if (endpoint is not null)
            Assert.Contains(cfg.Models.Values, m => m.Endpoint == endpoint);
    }

    [Theory]
    [MemberData(nameof(TemplatesWithEndpoints))]
    public void Template_InstructionsNameOnlyRealRoutes(string template, string? endpoint) =>
        AssertRoutesExist(CompileTemplate(template, endpoint));

    [Theory]
    [MemberData(nameof(Examples))]
    public void Example_Compiles(string file) =>
        Assert.NotEmpty(TeamConfigLoader.LoadAndCompile(Path.Combine(ExamplesDir(), file)).Agents);

    [Theory]
    [MemberData(nameof(Examples))]
    public void Example_InstructionsNameOnlyRealRoutes(string file) =>
        AssertRoutesExist(TeamConfigLoader.LoadAndCompile(Path.Combine(ExamplesDir(), file)));

    // A route an agent is told to take but doesn't have fails at run time with a correction loop.
    private static void AssertRoutesExist(OrchestrationConfig cfg)
    {
        foreach (var agent in cfg.Agents)
        {
            var routes = agent.HandoffRoutes.Select(r => r.Name).ToHashSet();
            foreach (Match m in Regex.Matches(agent.Instructions, "route_keyword: \"([^\"]+)\""))
                Assert.True(routes.Contains(m.Groups[1].Value),
                    $"{agent.Name} is told to hand off on '{m.Groups[1].Value}', but its routes are [{string.Join(", ", routes)}].");
        }
    }
}
