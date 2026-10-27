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

    private static ReplStdinPump Answering(string answer)
    {
        var pump = new ReplStdinPump(new StringReader($$"""{"type":"question_response","answer":"{{answer}}"}"""), () => null);
        pump.Start();
        return pump;
    }

    [Fact]
    public async Task SelectAsync_AsksThePanel_AndReturnsItsPick()
    {
        ReplInput.Bridge = Answering("2. python: print(1)");

        var picked = await ReplInput.SelectAsync("[bold]Which code block?[/]", ["1. sh: ls", "2. python: print(1)"]);

        Assert.Equal("2. python: print(1)", picked);
        Assert.Contains("\"type\":\"question\"", _events.ToString());
        Assert.Contains("\"question\":\"Which code block?\"", _events.ToString());
        Assert.Contains("\"allowOther\":false", _events.ToString());
    }

    [Fact]
    public async Task SelectAsync_AnswerNotAChoice_IsNoPick()
    {
        ReplInput.Bridge = Answering("something else");

        Assert.Null(await ReplInput.SelectAsync("Which?", ["a", "b"]));
    }
}
