using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

/// <summary>Behind the VS Code panel, a pick (e.g. which code block <c>/copy code</c> takes) is asked in its question bar.</summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplInputTests : IDisposable
{
    private readonly TextWriter _originalWriter = ReplJsonBridge.Writer;
    private readonly StringWriter _events = new();

    public ReplInputTests() => ReplJsonBridge.Writer = _events;

    public void Dispose()
    {
        ReplJsonBridge.Writer = _originalWriter;
        ReplInput.Bridge = null;
    }

    // Started once the question is asked: an answer that arrives before its question is a stale one, and dropped.
    private static ReplStdinPump Answering(string answer) =>
        new(new StringReader($$"""{"type":"question_response","answer":"{{answer}}"}"""), () => null);

    [Fact]
    public async Task SelectAsync_AsksThePanel_AndReturnsItsPick()
    {
        var pump = Answering("2. python: print(1)");
        ReplInput.Bridge = pump;

        var asked = ReplInput.SelectAsync("[bold]Which code block?[/]", ["1. sh: ls", "2. python: print(1)"]);
        pump.Start();
        var picked = await asked;

        Assert.Equal("2. python: print(1)", picked);
        Assert.Contains("\"type\":\"question\"", _events.ToString());
        Assert.Contains("\"question\":\"Which code block?\"", _events.ToString());
        Assert.Contains("\"allowOther\":false", _events.ToString());
    }

    [Fact]
    public async Task SelectAsync_AnswerNotAChoice_IsNoPick()
    {
        var pump = Answering("something else");
        ReplInput.Bridge = pump;

        var asked = ReplInput.SelectAsync("Which?", ["a", "b"]);
        pump.Start();
        Assert.Null(await asked);
    }
}
