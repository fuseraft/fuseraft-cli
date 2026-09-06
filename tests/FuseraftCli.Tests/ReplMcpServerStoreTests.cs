using fuseraft.Core;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Storage;

namespace FuseraftCli.Tests;

/// <summary>
/// Save/load round-trip for the REPL's saved-MCP-servers file. Isolates FUSERAFT_HOME so this
/// never touches the real <c>~/.fuseraft/repl-mcp-servers.json</c> — see
/// <see cref="FuseraftHomeEnvCollection"/> for why the whole test class must run sequentially
/// relative to other tests that also override this environment variable.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplMcpServerStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);

    public ReplMcpServerStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuseraft_mcpstore_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Load_NoFile_ReturnsEmptyList()
    {
        Assert.Empty(ReplMcpServerStore.Load());
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var servers = new List<McpServerConfig>
        {
            new()
            {
                Name = "filesystem",
                Transport = "stdio",
                Command = "npx",
                Args = ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"],
                Env = new Dictionary<string, string?> { ["FOO"] = "bar" },
                WorkingDirectory = "/tmp",
            },
            new()
            {
                Name = "remote",
                Transport = "http",
                Url = "https://example.com/mcp",
            },
        };

        ReplMcpServerStore.Save(servers);
        var loaded = ReplMcpServerStore.Load();

        Assert.Equal(2, loaded.Count);
        Assert.Equal("filesystem", loaded[0].Name);
        Assert.Equal("stdio", loaded[0].Transport);
        Assert.Equal("npx", loaded[0].Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "/tmp"], loaded[0].Args);
        Assert.Equal("bar", loaded[0].Env["FOO"]);
        Assert.Equal("/tmp", loaded[0].WorkingDirectory);
        Assert.Equal("remote", loaded[1].Name);
        Assert.Equal("http", loaded[1].Transport);
        Assert.Equal("https://example.com/mcp", loaded[1].Url);
    }

    [Fact]
    public void Save_OverwritesPreviousContent()
    {
        ReplMcpServerStore.Save([new McpServerConfig { Name = "first" }]);
        ReplMcpServerStore.Save([new McpServerConfig { Name = "second" }]);

        var loaded = ReplMcpServerStore.Load();
        Assert.Single(loaded);
        Assert.Equal("second", loaded[0].Name);
    }

    [Fact]
    public void Load_CorruptedFile_ReturnsEmptyListRatherThanThrowing()
    {
        Directory.CreateDirectory(FuseraftPaths.GlobalRoot);
        File.WriteAllText(ReplMcpServerStore.StorePath, "{ not valid json ][");

        Assert.Empty(ReplMcpServerStore.Load());
    }
}
