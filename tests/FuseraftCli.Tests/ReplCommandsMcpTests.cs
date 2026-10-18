using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

public sealed class ReplCommandsMcpTests
{
    [Fact]
    public void ParseEnvPairs_ReadsKeyValuePairs()
    {
        var env = ReplCommands.ParseEnvPairs("""DB_PASSWORD=${PROD_DB_PASSWORD} GREETING="hello world" EMPTY=""");

        Assert.Equal(new Dictionary<string, string?>
        {
            ["DB_PASSWORD"] = "${PROD_DB_PASSWORD}",
            ["GREETING"]    = "hello world",
            ["EMPTY"]       = "",
        }, env);
    }

    [Fact]
    public void ParseEnvPairs_Blank_IsNone() => Assert.Empty(ReplCommands.ParseEnvPairs("  ")!);

    [Theory]
    [InlineData("NOEQUALS")]
    [InlineData("=value")]
    [InlineData("A=1 B")]
    public void ParseEnvPairs_APairWithoutAKey_IsRejected(string line) => Assert.Null(ReplCommands.ParseEnvPairs(line));

    [Fact]
    public void ConnectFailure_GivesEveryDistinctMessage_AndPointsAStdioServerAtItsLog()
    {
        var ex = new InvalidOperationException("Failed to connect transport.",
            new System.ComponentModel.Win32Exception("No such file or directory"));

        Assert.Equal(
            "Failed to connect transport.: No such file or directory [dim]/mcp log db shows what it wrote to stderr.[/]",
            ReplCommands.ConnectFailure(ex, new() { Name = "db", Transport = "stdio" }));
        Assert.Equal("refused", ReplCommands.ConnectFailure(new Exception("refused", new Exception("refused")),
            new() { Name = "api", Transport = "http" }));
    }
}
