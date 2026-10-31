using System.Text.Json;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Cli.Protocol;

namespace FuseraftCli.Tests;

public sealed class ReplCommandTests
{
    [Fact]
    public void ReadyEvent_KeepsEveryFieldVsCodeReads_AndAddsProtocolVersion()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
            ReplCommand.ReadyEvent("abc123", "model-x", "My chat", null), WireProtocol.JsonOpts));
        var root = doc.RootElement;

        // replPanelProvider.ts and media/repl.js read type, sessionId, model, title, autoTitle, midTurnCommands.
        Assert.Equal("ready", root.GetProperty("type").GetString());
        Assert.Equal("abc123", root.GetProperty("sessionId").GetString());
        Assert.Equal("model-x", root.GetProperty("model").GetString());
        Assert.Equal("My chat", root.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("autoTitle").ValueKind);
        Assert.Contains("/title", root.GetProperty("midTurnCommands").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(WireProtocol.Version, root.GetProperty("protocolVersion").GetInt32());
    }
}
