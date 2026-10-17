using fuseraft.Cli.Commands.Repl;
using fuseraft.Infrastructure.Mcp;

namespace FuseraftCli.Tests;

public sealed class McpToolResultTests
{
    [Fact]
    public void TryRead_JoinsTheTextContentAndReadsIsError()
    {
        var ok = McpToolResult.TryRead(
            """{"content":[{"type":"text","text":"a"},{"type":"text","text":"b"}],"isError":true}""", out var content, out var isError);

        Assert.True(ok);
        Assert.Equal("a\nb", content);
        Assert.True(isError);
    }

    [Fact]
    public void TryRead_KeepsANoticeAppendedAfterTheResult()
    {
        var ok = McpToolResult.TryRead(
            """{"content":[{"type":"text","text":"refused"}],"isError":true}""" + "\n\n[SYSTEM NOTICE: 2 tool calls in a row have failed]",
            out var content, out var isError);

        Assert.True(ok);
        Assert.True(isError);
        Assert.Equal("refused\n\n[SYSTEM NOTICE: 2 tool calls in a row have failed]", content);
    }

    [Fact]
    public void TryRead_KeepsOtherContentKindsAsJson()
    {
        McpToolResult.TryRead("""{"content":[{"type":"image","data":"x"}]}""", out var content, out var isError);

        Assert.Equal("""{"type":"image","data":"x"}""", content);
        Assert.False(isError);
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("{\"rows\":[]}")]
    [InlineData("{not json")]
    [InlineData(null)]
    public void TryRead_AnythingElse_IsNotAResult(string? text) =>
        Assert.False(McpToolResult.TryRead(text, out _, out _));

    [Fact]
    public void AnMcpError_CountsAsAToolFailure()
    {
        Assert.True(ReplTurn.IsToolFailureText("""{"content":[{"type":"text","text":"no"}],"isError":true}"""));
        Assert.False(ReplTurn.IsToolFailureText("""{"content":[{"type":"text","text":"[]"}],"isError":false}"""));
    }
}
