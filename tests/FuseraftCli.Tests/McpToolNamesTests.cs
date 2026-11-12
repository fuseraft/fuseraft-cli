using Microsoft.Extensions.AI;
using fuseraft.Infrastructure.Mcp;

namespace FuseraftCli.Tests;

public sealed class McpToolNamesTests
{
    private static AIFunction Fake(string name, string result = "ok") =>
        AIFunctionFactory.Create(() => result, name, $"Fake {name}.");

    private static List<string> Names(IEnumerable<AIFunction> tools) => [.. tools.Select(t => t.Name)];

    [Fact]
    public void NoClash_ReturnsTheSameTools()
    {
        var read = Fake("read_file");
        var query = Fake("query_database");

        var result = McpToolNames.Disambiguate([(null, read), ("Sql", query)]);

        Assert.Same(read, result[0]);
        Assert.Same(query, result[1]);
    }

    [Fact]
    public void TwoServersWithTheSameTool_BothGetTheirServerAsAPrefix()
    {
        var result = McpToolNames.Disambiguate(
            [("Sql", Fake("query_database")), ("Local", Fake("query_database")), ("Local", Fake("list_tables"))]);

        Assert.Equal(["Sql_query_database", "Local_query_database", "list_tables"], Names(result));
    }

    [Fact]
    public void ServerToolNamedLikeABuiltIn_IsRenamed_AndTheBuiltInKeepsItsName()
    {
        var result = McpToolNames.Disambiguate([(null, Fake("read_file")), ("Docs", Fake("read_file"))]);

        Assert.Equal(["read_file", "Docs_read_file"], Names(result));
    }

    [Fact]
    public async Task RenamedTool_StillCallsItsOwnTool()
    {
        var result = McpToolNames.Disambiguate([("Sql", Fake("query", "from sql")), ("Local", Fake("query", "from local"))]);

        Assert.Equal("from local", (await result[1].InvokeAsync(new AIFunctionArguments()))?.ToString());
    }

    // The REPL finds an MCP tool's read-only hint and approval gate through GetService, so a rename must not hide them.
    private sealed class Marker(AIFunction inner) : DelegatingAIFunction(inner);

    [Fact]
    public void RenamedTool_StillAnswersGetServiceForTheWrappersInside()
    {
        var result = McpToolNames.Disambiguate([("Sql", new Marker(Fake("query"))), ("Local", Fake("query"))]);

        Assert.NotNull(result[0].GetService<Marker>());
    }

    [Fact]
    public void ServerNameWithCharactersToolNamesCantHold_IsCleanedUp()
    {
        var result = McpToolNames.Disambiguate([("my server.v2", Fake("query")), ("other", Fake("query"))]);

        Assert.Equal("my_server_v2_query", result[0].Name);
    }

    [Fact]
    public void PrefixedNameTakenByAnotherTool_GetsANumber()
    {
        var result = McpToolNames.Disambiguate(
            [(null, Fake("Sql_query")), ("Sql", Fake("query")), ("Local", Fake("query"))]);

        Assert.Equal(["Sql_query", "Sql_query_2", "Local_query"], Names(result));
    }

    [Fact]
    public void LongNames_StayWithinTheProviderLimit_AndUnique()
    {
        var tool = new string('t', 60);
        var server = new string('s', 30);

        var result = McpToolNames.Disambiguate([(server, Fake(tool)), (server, Fake(tool))]);

        Assert.All(result, t => Assert.True(t.Name.Length <= McpToolNames.MaxLength, t.Name));
        Assert.NotEqual(result[0].Name, result[1].Name);
    }
}
