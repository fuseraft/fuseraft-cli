using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

/// <summary>
/// While a turn runs, a plain message from the VS Code panel joins it (as the full-screen REPL's prompt does);
/// commands and image messages wait, and approval and question answers go only to the prompt waiting for them.
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
        var approval = pump.ReadApprovalResponseAsync();
        pump.Start();

        Assert.True(await approval);
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
    public async Task WhileBusy_ACommandThatNeedNotWait_RunsNow_AndOthersQueue()
    {
        // No turn is streaming (no active request), as between the turns of a /goal run: the REPL is still busy.
        var ran = new List<string>();
        var pump = new ReplStdinPump(new StringReader(string.Join('\n',
            """{"type":"user_input","text":"/title Login bug"}""",
            """{"type":"user_input","text":"/compact"}""")), () => null)
        {
            RunNow = line => { ran.Add(line); return line.StartsWith("/title") ? "renamed" : null; },
        };
        pump.Start();
        await pump.Closed;

        Assert.Equal("/compact", (await pump.ReadMessageAsync())!.Text);
        Assert.Null(await pump.ReadMessageAsync());
        Assert.Equal(["/title Login bug", "/compact"], ran);
    }

    [Fact]
    public async Task WhileWaitingForInput_EvenAnInstantCommandQueues()
    {
        var pump = new ReplStdinPump(new StringReader("""{"type":"user_input","text":"/title Later"}"""), () => null)
        {
            RunNow = _ => "renamed",
        };
        var read = pump.ReadMessageAsync();
        pump.Start();

        Assert.Equal("/title Later", (await read)!.Text);
    }

    [Fact]
    public async Task WhileWaitingForInput_APlainMessageIsTheNextInput_NotAnInterjection()
    {
        var joined = new List<string>();
        var pump = new ReplStdinPump(new StringReader("""{"type":"user_input","text":"next question"}"""), () => null)
        {
            Interject = text => { joined.Add(text); return true; },
        };
        var read = pump.ReadMessageAsync();
        pump.Start();

        Assert.Equal("next question", (await read)!.Text);
        Assert.Empty(joined);
    }

    [Fact]
    public async Task AnApprovalPrompt_TakesOnlyItsAnswer_AndMessagesSentMeanwhileWaitAsInput()
    {
        var pump = new ReplStdinPump(new StringReader(string.Join('\n',
            """{"type":"user_input","text":"also, thanks!"}""",
            """{"type":"user_input","text":"/hitl auto"}""",
            """{"type":"approval_response","approved":true}""")), () => null);
        var approval = pump.ReadApprovalResponseAsync();
        pump.Start();

        Assert.True(await approval);
        Assert.Equal("also, thanks!", (await pump.ReadMessageAsync())!.Text);
        Assert.Equal("/hitl auto", (await pump.ReadMessageAsync())!.Text);
    }

    [Fact]
    public async Task AQuestion_IgnoresAnApprovalAnswer()
    {
        var pump = new ReplStdinPump(new StringReader(string.Join('\n',
            """{"type":"approval_response","approved":true}""",
            """{"type":"user_input","text":"hello"}""",
            """{"type":"question_response","answer":"SQLite"}""")), () => null);
        var question = pump.ReadQuestionResponseAsync(CancellationToken.None);
        pump.Start();

        Assert.Equal("SQLite", await question);
        Assert.Equal("hello", (await pump.ReadMessageAsync())!.Text);
    }

    [Fact]
    public async Task AnAnswerSentBeforeItsPromptOpened_AnswersNothing()
    {
        // e.g. Allow clicked just after Stop closed the prompt: it must not approve the next one.
        var pump = new ReplStdinPump(new StringReader("""{"type":"approval_response","approved":true}"""), () => null);
        pump.Start();
        await pump.Closed;

        Assert.Null(await pump.TryReadApprovalResponseAsync());
        Assert.Null(await pump.ReadMessageAsync());
    }

    [Fact]
    public async Task Stop_DeniesAnOpenApproval()
    {
        using var turn = new CancellationTokenSource();
        var pump = new ReplStdinPump(new StringReader(string.Join('\n',
            """{"type":"interrupt"}""",
            """{"type":"approval_response","approved":true}""")), () => turn);
        var approval = pump.TryReadApprovalResponseAsync();
        pump.Start();

        Assert.False(await approval);
        Assert.True(turn.IsCancellationRequested);
    }

}
