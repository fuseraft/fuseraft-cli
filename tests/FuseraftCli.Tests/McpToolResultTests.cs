using fuseraft.Cli.Commands.Repl;
using fuseraft.Infrastructure.Mcp;
using Microsoft.Extensions.AI;

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
    public void Display_ShowsAResultsContent_AndAnythingElseAsItIs()
    {
        Assert.Equal("refused", McpToolResult.Display("""{"content":[{"type":"text","text":"refused"}],"isError":true}"""));
        Assert.Equal("plain text", McpToolResult.Display("plain text"));
        Assert.Equal("", McpToolResult.Display(null));
    }

    [Fact]
    public void Display_IndentsJsonText_OnlyWhenAsked_AndLeavesOtherTextAlone()
    {
        const string result = """{"content":[{"type":"text","text":"{\"n\":1}"},{"type":"text","text":"{not json"}]}""";

        Assert.Equal("{\"n\":1}\n{not json", McpToolResult.Display(result));
        Assert.Equal("{\n  \"n\": 1\n}\n{not json", McpToolResult.Display(result, indentJson: true).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Display_ReadsTheContentTheSdkReturns()
    {
        // A successful call comes back from the SDK as its content, not as the CallToolResult's JSON.
        Assert.Equal("{\n  \"n\": 1\n}", McpToolResult.Display(new TextContent("{\"n\":1}"), indentJson: true).ReplaceLineEndings("\n"));
        Assert.Equal("{\"n\":1}", McpToolResult.Display(new TextContent("{\"n\":1}")));
        Assert.Equal("a\n[image/png]",
            McpToolResult.Display(new List<AIContent> { new TextContent("a"), new DataContent(new byte[] { 1 }, "image/png") }));
    }

    [Fact]
    public void AnMcpError_CountsAsAToolFailure()
    {
        Assert.True(ReplTurn.IsToolFailureText("""{"content":[{"type":"text","text":"no"}],"isError":true}"""));
        Assert.False(ReplTurn.IsToolFailureText("""{"content":[{"type":"text","text":"[]"}],"isError":false}"""));
    }
}
