using System.Text.Json;
using fuseraft.Cli.Commands;
using fuseraft.Cli.Protocol;

namespace FuseraftCli.Tests;

[Collection("FuseraftHomeEnv")] // swaps the process-wide WireProtocol.Writer
public sealed class ShowConfigCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fuseraft-configs-{Guid.NewGuid():N}");
    private readonly TextWriter _originalWriter = WireProtocol.Writer;
    private readonly StringWriter _out = new();

    public ShowConfigCommandTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "agents"));
        WireProtocol.Writer = _out;
    }

    public void Dispose()
    {
        WireProtocol.Writer = _originalWriter;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private int Run(ShowConfigSettings settings) => new ShowConfigCommand().Execute(null!, settings, CancellationToken.None);

    [Fact]
    public void ListJson_SummarisesConfigs_SkipsAgentFiles_ReportsLoadErrors()
    {
        File.WriteAllText(Path.Combine(_dir, "team.yaml"), """
            Team: Team
            Agents:
              Dev:
                Instructions: build
                Model: { ModelId: gpt-4o }
            Flow:
              Start: Dev
              Steps:
                Dev:
                  - To: end
            """);
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ nope");
        File.WriteAllText(Path.Combine(_dir, "agents", "dev.yaml"), "Name: Dev");

        Assert.Equal(0, Run(new ShowConfigSettings { List = true, Json = true, Dir = _dir }));

        using var doc = JsonDocument.Parse(_out.ToString());
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        var broken = items.Single(i => i.GetProperty("path").GetString()!.EndsWith("broken.json"));
        Assert.False(string.IsNullOrEmpty(broken.GetProperty("error").GetString()));

        var team = items.Single(i => i.GetProperty("path").GetString()!.EndsWith("team.yaml"));
        Assert.Equal("Team", team.GetProperty("name").GetString());
        Assert.Equal("Dev", Assert.Single(team.GetProperty("agents").EnumerateArray()).GetString());
        Assert.True(Path.IsPathRooted(team.GetProperty("path").GetString()));
    }

    [Fact]
    public void ListJson_ReportsMarkdownAgentFiles_EvenWhenOneIsMissing()
    {
        const string flow = """
            flow:
              start: Dev
              steps:
                Dev:
                  - to: end
            """;
        File.WriteAllText(Path.Combine(_dir, "agents", "dev.md"), "---\nmodel: gpt-4o\n---\nbuild\n");
        File.WriteAllText(Path.Combine(_dir, "ok.yaml"), "team: Ok\nagents:\n  Dev: agents/dev.md\n" + flow);
        File.WriteAllText(Path.Combine(_dir, "gap.yaml"),
            "team: Gap\nagents:\n  Dev: agents/dev.md\n  Inline:\n    instructions: x\n    model: gpt-4o\n  Lost: agents/lost.md\n" + flow);

        Assert.Equal(0, Run(new ShowConfigSettings { List = true, Json = true, Dir = _dir }));

        using var doc = JsonDocument.Parse(_out.ToString());
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        string[] Files(string name) => [.. items.Single(i => i.GetProperty("path").GetString()!.EndsWith(name))
            .GetProperty("agentFiles").EnumerateArray().Select(f => f.GetString()!)];

        var dev = Path.Combine(_dir, "agents", "dev.md");
        Assert.Equal([dev], Files("ok.yaml"));
        Assert.Equal([dev, Path.Combine(_dir, "agents", "lost.md")], Files("gap.yaml"));
        Assert.Contains("lost.md", items.Single(i => i.GetProperty("path").GetString()!.EndsWith("gap.yaml")).GetProperty("error").GetString());
    }

    [Fact]
    public void ListJson_MissingDir_IsErrorEnvelope()
    {
        Assert.Equal(1, Run(new ShowConfigSettings { List = true, Json = true, Dir = Path.Combine(_dir, "absent") }));
        Assert.Contains("not found", _out.ToString());
    }

    [Fact]
    public void Json_WithoutList_IsRejected()
    {
        Assert.Equal(1, Run(new ShowConfigSettings { Json = true }));
        Assert.Contains("only to --list", _out.ToString());
    }
}
