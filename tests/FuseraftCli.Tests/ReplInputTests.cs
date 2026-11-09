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

    [Fact]
    public async Task AskOverBridge_AsksAFreeTextQuestion_AndReturnsTheAnswer()
    {
        var pump = Answering("sql");

        var asked = ReplInput.AskOverBridgeAsync(pump, "[dim]Server name[/]");
        pump.Start();

        Assert.Equal("sql", await asked);
        Assert.Contains("\"question\":\"Server name\"", _events.ToString());
        Assert.Contains("\"options\":[]", _events.ToString());
        Assert.Contains("\"allowOther\":true", _events.ToString());
    }

    [Fact]
    public async Task AskOverBridge_OffersTheDefaultAsAChoice()
    {
        var pump = Answering("Authorization");

        var asked = ReplInput.AskOverBridgeAsync(pump, "Header name", defaultValue: "Authorization");
        pump.Start();

        Assert.Equal("Authorization", await asked);
        Assert.Contains("\"options\":[\"Authorization\"]", _events.ToString());
    }

    [Fact]
    public async Task AskOverBridge_BlankChoice_IsAnEmptyAnswer()
    {
        var pump = Answering(ReplInput.BlankChoice);

        var asked = ReplInput.AskOverBridgeAsync(pump, "Arguments", allowEmpty: true);
        pump.Start();

        Assert.Equal("", await asked);
    }

    [Fact]
    public async Task AskOverBridge_Skipped_IsCancelled()
    {
        var pump = new ReplStdinPump(new StringReader("""{"type":"question_response","answer":null}"""), () => null);

        var asked = ReplInput.AskOverBridgeAsync(pump, "Header value");
        pump.Start();

        Assert.Null(await asked);
    }

    [Fact]
    public async Task AskOverBridge_Secret_IsNeverAsked()
    {
        var pump = Answering("sk-live");

        Assert.Null(await ReplInput.AskOverBridgeAsync(pump, "API key", secret: true));
        Assert.DoesNotContain("question", _events.ToString());
    }
}
