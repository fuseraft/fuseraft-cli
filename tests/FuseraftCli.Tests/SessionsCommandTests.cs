using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using fuseraft.Cli.Commands;
using fuseraft.Cli.Protocol;
using fuseraft.Core.Models.Agents;
using fuseraft.Core.Models.Session;
using fuseraft.Infrastructure.Memory;

namespace FuseraftCli.Tests;

[Collection("FuseraftHomeEnv")] // swaps the process-wide WireProtocol.Writer
public sealed class SessionsCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fuseraft-sessions-{Guid.NewGuid():N}");
    private readonly JsonSessionStore _store;
    private readonly TextWriter _originalWriter = WireProtocol.Writer;
    private readonly StringWriter _out = new();

    public SessionsCommandTests()
    {
        _store = new JsonSessionStore(NullLogger<JsonSessionStore>.Instance, _dir);
        WireProtocol.Writer = _out;
    }

    public void Dispose()
    {
        WireProtocol.Writer = _originalWriter;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ExecuteAsync never reads its CommandContext.
    private Task<int> RunAsync(SessionsSettings settings) =>
        new SessionsCommand(_store).ExecuteAsync(null!, settings, CancellationToken.None);

    [Fact]
    public async Task Show_Json_ReturnsSessionWithMessages()
    {
        await _store.SaveAsync(new SessionCheckpoint
        {
            SessionId = "abcd1234", Task = "Build it", ConfigPath = "/c.yaml",
            Messages = [new AgentMessage { AgentName = "Dev", Content = "done", ToolCalls = [new ToolCallRecord("write_file", "path=a", true)] }],
        });

        Assert.Equal(0, await RunAsync(new SessionsSettings { Show = "abcd1234", Json = true }));

        using var doc = JsonDocument.Parse(_out.ToString());
        var item = doc.RootElement.GetProperty("item");
        Assert.Equal("abcd1234", item.GetProperty("sessionId").GetString());
        var msg = Assert.Single(item.GetProperty("messages").EnumerateArray());
        Assert.Equal("Dev", msg.GetProperty("agentName").GetString());
        Assert.Equal("write_file", msg.GetProperty("toolCalls")[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("deadbeef")] // well-formed, absent
    [InlineData("nope")]     // malformed — the store throws on these
    public async Task Show_Json_UnknownId_IsErrorEnvelope(string id)
    {
        Assert.Equal(1, await RunAsync(new SessionsSettings { Show = id, Json = true }));

        using var doc = JsonDocument.Parse(_out.ToString());
        Assert.Contains("not found", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Delete_MalformedId_IsNotFoundNotACrash()
    {
        Assert.Equal(1, await RunAsync(new SessionsSettings { Delete = "nope" }));
    }

    [Fact]
    public async Task Json_WithDelete_IsRejected()
    {
        Assert.Equal(1, await RunAsync(new SessionsSettings { Delete = "abcd1234", Json = true }));
        Assert.Contains("applies only to listing", _out.ToString());
    }
}
