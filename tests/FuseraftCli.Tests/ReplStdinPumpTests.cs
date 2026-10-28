using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

/// <summary>
/// While a turn runs, a plain message from the VS Code panel joins it (as the full-screen REPL's prompt does);
/// commands, image messages, and approval and question answers keep their place in line.
/// </summary>
public sealed class ReplStdinPumpTests
{
    [Theory]
    [InlineData("""{"type":"user_input","text":"also add tests"}""", "also add tests")]
    [InlineData("""{"type":"user_input","text":"  trimmed  "}""", "trimmed")]
    [InlineData("""{"type":"user_input","text":"/help"}""", null)]
    [InlineData("""{"type":"user_input","text":"!git status"}""", null)]
    [InlineData("""{"type":"user_input","text":"$commit"}""", null)]
    [InlineData("""{"type":"user_input","text":"   "}""", null)]
    [InlineData("""{"type":"user_input","text":"look","images":[{"name":"a.png","data":"AAAA"}]}""", null)]
    [InlineData("""{"type":"approval_response","approved":true}""", null)]
    [InlineData("""{"type":"question_response","answer":"yes"}""", null)]
    [InlineData("not json", null)]
    public void InterjectionText_OnlyPlainMessages(string line, string? expected) =>
        Assert.Equal(expected, ReplStdinPump.InterjectionText(line));

    [Fact]
    public async Task WhileATurnRuns_PlainMessagesJoinIt_AndEverythingElseQueues()
    {
        var joined = new List<string>();
        var pump = new ReplStdinPump(new StringReader(string.Join('\n',
            """{"type":"user_input","text":"use the v2 API"}""",
            """{"type":"approval_response","approved":true}""",
            """{"type":"user_input","text":"/compact"}""")), () => null)
        {
            Interject = text => { joined.Add(text); return true; },
        };
        pump.Start();

        Assert.True(await pump.ReadApprovalResponseAsync());
        Assert.Equal("/compact", (await pump.ReadMessageAsync())!.Text);
        Assert.Null(await pump.ReadMessageAsync());
        Assert.Equal(["use the v2 API"], joined);
    }

    [Fact]
    public async Task BetweenTurns_MessagesQueue()
    {
        var pump = new ReplStdinPump(new StringReader("""{"type":"user_input","text":"next question"}"""), () => null);
        pump.Start();

        Assert.Equal("next question", (await pump.ReadMessageAsync())!.Text);
    }

    [Theory]
    [InlineData("""{"type":"user_input","text":" /title Login bug "}""", "/title Login bug")]
    [InlineData("""{"type":"user_input","text":"/title a\nb"}""", null)]
    [InlineData("""{"type":"user_input","text":"plain"}""", null)]
    [InlineData("""{"type":"user_input","text":"/title x","images":[{"name":"a.png","data":"AAAA"}]}""", null)]
    public void CommandText_OnlyOneLineCommands(string line, string? expected) =>
        Assert.Equal(expected, ReplStdinPump.CommandText(line));

    [Fact]
    public async Task WhileATurnRuns_ACommandThatNeedNotWait_RunsNow_AndOthersQueue()
    {
        var ran = new List<string>();
        using var turn = new CancellationTokenSource();
        var pump = new ReplStdinPump(new StringReader(string.Join('\n',
            """{"type":"user_input","text":"/title Login bug"}""",
            """{"type":"user_input","text":"/compact"}""")), () => turn)
        {
            RunNow = line => { ran.Add(line); return line.StartsWith("/title") ? "renamed" : null; },
        };
        pump.Start();

        Assert.Equal("/compact", (await pump.ReadMessageAsync())!.Text);
        Assert.Null(await pump.ReadMessageAsync());
        Assert.Equal(["/title Login bug", "/compact"], ran);
    }

    [Fact]
    public async Task BetweenTurns_EvenAnInstantCommandQueues()
    {
        var pump = new ReplStdinPump(new StringReader("""{"type":"user_input","text":"/title Later"}"""), () => null)
        {
            RunNow = _ => "renamed",
        };
        pump.Start();

        Assert.Equal("/title Later", (await pump.ReadMessageAsync())!.Text);
    }
}
