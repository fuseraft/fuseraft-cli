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
            Orchestration:
              Name: Team
              Agents:
                - Name: Dev
                  Instructions: build
                  Model:
                    ModelId: gpt-4o
              Selection:
                Type: sequential
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
