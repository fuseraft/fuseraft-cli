using System.Text.Json;
using fuseraft.Cli.Protocol;

namespace FuseraftCli.Tests;

/// <summary>
/// The JSON envelope every <c>--json</c> command writes. Clients (VS Code, fuseraft-web, CI) parse
/// these exact keys, so a failure here means a breaking wire change, not a test to update casually.
/// </summary>
[Collection("FuseraftHomeEnv")] // WriteList_WritesOneLineAndReturnsZero swaps the process-wide WireProtocol.Writer
public sealed class WireProtocolTests
{
    private sealed record Sample(string SessionId, int TurnCount);

    [Fact]
    public void SerializeList_WrapsItemsInVersionedEnvelope()
    {
        using var doc = JsonDocument.Parse(WireProtocol.SerializeList([new Sample("abc", 3)]));
        var root = doc.RootElement;

        Assert.Equal(["protocolVersion", "items"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(WireProtocol.Version, root.GetProperty("protocolVersion").GetInt32());
        var item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal("abc", item.GetProperty("sessionId").GetString());
        Assert.Equal(3, item.GetProperty("turnCount").GetInt32());
    }

    [Fact]
    public void SerializeList_EmptyIsAnEmptyArrayNotNull()
    {
        using var doc = JsonDocument.Parse(WireProtocol.SerializeList(Array.Empty<Sample>()));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("items").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void SerializeError_CarriesVersionAndMessage()
    {
        using var doc = JsonDocument.Parse(WireProtocol.SerializeError("not configured"));
        Assert.Equal(["protocolVersion", "error"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("not configured", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void WriteList_WritesOneLineAndReturnsZero()
    {
        var original = WireProtocol.Writer;
        var sw = new StringWriter();
        WireProtocol.Writer = sw;
        try
        {
            Assert.Equal(0, WireProtocol.WriteList([new Sample("a", 1)]));
            Assert.Equal(1, WireProtocol.WriteError("x"));
        }
        finally { WireProtocol.Writer = original; }

        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"items\"", lines[0]);
        Assert.Contains("\"error\"", lines[1]);
    }
}
