using System.Text.Json;
using fuseraft.Cli.Commands;
using fuseraft.Cli.Protocol;

namespace FuseraftCli.Tests;

// Generation itself writes into the working directory (.gitignore, .fuseraft/knowledge), so only the
// paths that return before writing are unit-tested here.
[Collection("FuseraftHomeEnv")] // swaps the process-wide WireProtocol.Writer
public sealed class InitCommandTests : IDisposable
{
    private readonly TextWriter _originalWriter = WireProtocol.Writer;
    private readonly StringWriter _out = new();

    public InitCommandTests() => WireProtocol.Writer = _out;

    public void Dispose() => WireProtocol.Writer = _originalWriter;

    private Task<int> RunAsync(InitSettings settings) =>
        new InitCommand().ExecuteAsync(null!, settings, CancellationToken.None);

    [Fact]
    public async Task ListTemplatesJson_ListsEveryBuildableTemplate()
    {
        Assert.Equal(0, await RunAsync(new InitSettings { ListTemplates = true, Json = true }));

        using var doc = JsonDocument.Parse(_out.ToString());
        var keys = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(t => t.GetProperty("key").GetString()).ToList();
        Assert.Contains("solo", keys);
        Assert.Contains("swe", keys);
        Assert.All(doc.RootElement.GetProperty("items").EnumerateArray(),
            t => Assert.False(string.IsNullOrEmpty(t.GetProperty("label").GetString())));
    }

    [Fact]
    public async Task Json_UnknownTemplate_IsErrorEnvelope()
    {
        Assert.Equal(1, await RunAsync(new InitSettings { Template = "nope", Json = true, Model = "gpt-4o" }));

        using var doc = JsonDocument.Parse(_out.ToString());
        Assert.Contains("Unknown template", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Json_ExistingFileWithoutForce_IsErrorEnvelope()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fuseraft-init-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, "keep me");
        try
        {
            Assert.Equal(1, await RunAsync(new InitSettings { Template = "solo", Json = true, Model = "gpt-4o", OutputPath = path }));

            using var doc = JsonDocument.Parse(_out.ToString());
            Assert.Contains("already exist", doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("keep me", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
