using System.Text.Json;
using fuseraft.Cli.Commands.Memory;
using fuseraft.Cli.Protocol;
using fuseraft.Core;
using fuseraft.Core.Models.Knowledge;
using fuseraft.Infrastructure.Memory;

namespace FuseraftCli.Tests;

[Collection("FuseraftHomeEnv")]
public sealed class MemoryAgentsCommandTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");
    private readonly TextWriter _originalWriter = WireProtocol.Writer;
    private readonly StringWriter _out = new();

    public MemoryAgentsCommandTests()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _home);
        WireProtocol.Writer = _out;
    }

    public void Dispose()
    {
        WireProtocol.Writer = _originalWriter;
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private Task<int> RunAsync() =>
        new MemoryAgentsCommand().ExecuteAsync(null!, new MemoryAgentsSettings { Json = true }, CancellationToken.None);

    [Fact]
    public async Task Json_ListsAgentsWithMemories()
    {
        var store = MemoryStore.ForAgent("reviewer");
        await store.SaveAsync(new MemoryEntry { Name = "style", Type = "feedback", Description = "House style", Body = "Prefer small PRs." });
        await store.SaveAsync(new MemoryEntry { Name = "tests", Type = "feedback", Description = "Tests", Body = "Always add tests." });
        Directory.CreateDirectory(FuseraftPaths.GlobalMemoryAgent("empty"));
        Directory.CreateDirectory(FuseraftPaths.GlobalMemoryAgent("!!!")); // sanitizes to empty: ForAgent would throw

        Assert.Equal(0, await RunAsync());

        using var doc = JsonDocument.Parse(_out.ToString());
        var agent = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("reviewer", agent.GetProperty("agent").GetString());
        Assert.Equal(2, agent.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Json_NoMemories_IsEmptyList()
    {
        Assert.Equal(0, await RunAsync());

        using var doc = JsonDocument.Parse(_out.ToString());
        Assert.Empty(doc.RootElement.GetProperty("items").EnumerateArray());
    }
}
