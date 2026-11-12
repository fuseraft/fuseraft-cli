using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Storage;

namespace FuseraftCli.Tests;

public class ManagedMcpServersTests
{
    [Fact]
    public void Parse_ReadsHttpServerWithHeaders()
    {
        var servers = ManagedMcpServers.Parse("""
            [{"name":"Sql","transport":"http","url":"http://localhost:8080/mcp","transportMode":"streamable-http",
              "headers":{"Authorization":"Bearer ${FUSERAFT_WEB_MCP_1_TOKEN}"}}]
            """, out var error);

        Assert.Null(error);
        var s = Assert.Single(servers);
        Assert.Equal("Sql", s.Name);
        Assert.Equal("http", s.Transport);
        Assert.Equal("streamable-http", s.TransportMode);
        Assert.Equal("Bearer ${FUSERAFT_WEB_MCP_1_TOKEN}", s.Headers["Authorization"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Parse_UnsetIsEmptyWithoutError(string? json)
    {
        Assert.Empty(ManagedMcpServers.Parse(json, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Parse_InvalidJsonIsEmptyWithError()
    {
        Assert.Empty(ManagedMcpServers.Parse("{not json", out var error));
        Assert.Contains("isn't a JSON array", error);
    }

    [Fact]
    public void Parse_SkipsServersWithoutAName()
    {
        var servers = ManagedMcpServers.Parse("""[{"url":"http://a/mcp"},{"name":"B","transport":"http","url":"http://b/mcp"}]""", out var error);

        Assert.Equal("B", Assert.Single(servers).Name);
        Assert.Contains("without a Name", error);
    }

    // fuseraft-web passes each server's token as FUSERAFT_WEB_MCP_<n>_TOKEN and relies on its value being masked.
    [Fact]
    public void LauncherTokenVariables_AreMaskedAsSecrets() =>
        Assert.True(fuseraft.Infrastructure.Plugins.EnvSecretMasker.IsSensitiveName("FUSERAFT_WEB_MCP_1_TOKEN"));

    [Fact]
    public void Merge_KeepsOwnServerOfTheSameName()
    {
        var own     = new McpServerConfig { Name = "sql", Url = "http://mine/mcp" };
        var managed = new[] { new McpServerConfig { Name = "Sql", Url = "http://server/mcp" }, new McpServerConfig { Name = "Docs" } };

        var merged = ManagedMcpServers.Merge([own], managed);

        Assert.Equal(["sql", "Docs"], merged.Select(s => s.Name));
        Assert.Equal("http://mine/mcp", merged[0].Url);
    }

    [Fact]
    public void ReferencedBy_OnlyServersAnAgentNamesAndTheConfigDoesNotDeclare()
    {
        var managed = new[]
        {
            new McpServerConfig { Name = "Sql" },
            new McpServerConfig { Name = "Docs" },
            new McpServerConfig { Name = "Jira" },
        };
        var own = new[] { new McpServerConfig { Name = "docs" } };

        var wanted = ManagedMcpServers.ReferencedBy(managed, own, ["FileSystem", "sql", "Docs"]);

        Assert.Equal("Sql", Assert.Single(wanted).Name);
    }
}
