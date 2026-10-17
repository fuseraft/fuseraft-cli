using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

public sealed class ReplCommandsMcpTests
{
    [Fact]
    public void ParseEnvPairs_ReadsKeyValuePairs()
    {
        var env = ReplCommands.ParseEnvPairs("""DB_PASSWORD=${PROD_DB_PASSWORD} GREETING="hello world" EMPTY=""");

        Assert.Equal(new Dictionary<string, string>
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
}
