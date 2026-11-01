using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using fuseraft.Cli.Serve;

namespace FuseraftCli.Tests;

/// <summary>
/// <see cref="ServeAttachClient"/> over a real attach WebSocket: a client that reconnects under its id gets its
/// running task's approval and the results it missed; one that doesn't come back is left to the unattended policy.
/// </summary>
public sealed class ServeAttachClientTests : IAsyncLifetime
{
    private readonly ServeTaskQueue _queue = new();
    private readonly ServeHumanApprovalService _approvals = new(allowWhenUnattended: false);
    private WebApplication _app = null!;

    private TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(30);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }

    [Fact]
    public async Task Reconnecting_AnswersTheApprovalTheDroppedConnectionWasAsked()
    {
        await StartAsync();
        var first = await ConnectAsync("web-1");
        var sessionId = await DispatchAsync(first, "deploy it");
        var (request, _) = await NextTaskAsync();
        _approvals.SetCurrentTaskOrigin(request.OriginSink);
        var gate = _approvals.PromptShellCommandAsync("make deploy");
        Assert.Equal("approval_request", Type(await ReceiveAsync(first)));

        first.Abort();
        var second = await ConnectAsync("web-1");

        using (var hello = JsonDocument.Parse(await ReceiveAsync(second)))
        {
            Assert.Equal("web-1", hello.RootElement.GetProperty("clientId").GetString());
            var inFlight = hello.RootElement.GetProperty("inFlight");
            Assert.Equal(sessionId, inFlight.GetProperty("sessionId").GetString());
            Assert.Equal("deploy it", inFlight.GetProperty("task").GetString());
        }
        using (var asked = JsonDocument.Parse(await ReceiveAsync(second)))
            Assert.Equal("make deploy", asked.RootElement.GetProperty("command").GetString());
        await SendAsync(second, """{"type":"approval_response","approved":true}""");

        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task AResultThatFinishedWhileAway_IsSentOnReconnect()
    {
        await StartAsync();
        var first = await ConnectAsync("web-1");
        var sessionId = await DispatchAsync(first, "write the docs");
        var (_, record) = await NextTaskAsync();

        first.Abort();
        await Task.Delay(100);
        Finish(record);
        var client = (ServeAttachClient)record.OriginSink!;
        for (var i = 0; i < 100 && client.Undelivered.Count == 0; i++) await Task.Delay(20);
        var second = await ConnectAsync("web-1");

        using (var hello = JsonDocument.Parse(await ReceiveAsync(second)))
        {
            Assert.Equal(JsonValueKind.Null, hello.RootElement.GetProperty("inFlight").ValueKind);
            Assert.Equal(sessionId, hello.RootElement.GetProperty("pendingResults")[0].GetString());
        }
        using var result = JsonDocument.Parse(await ReceiveAsync(second));
        Assert.Equal("result", Type(result));
        Assert.Equal(sessionId, result.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("write the docs", result.RootElement.GetProperty("task").GetString());
    }

    [Fact]
    public async Task AClientThatDoesNotComeBack_IsLeftToTheUnattendedPolicy()
    {
        Grace = TimeSpan.FromMilliseconds(300);
        await StartAsync();
        var first = await ConnectAsync("web-1");
        await DispatchAsync(first, "deploy it");
        var (request, _) = await NextTaskAsync();
        _approvals.SetCurrentTaskOrigin(request.OriginSink);
        var gate = _approvals.PromptShellCommandAsync("make deploy");
        await ReceiveAsync(first);

        first.Abort();

        Assert.False(await gate.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task AnAnonymousClient_GetsNoGrace()
    {
        await StartAsync();
        var first = await ConnectAsync(clientId: null);
        await DispatchAsync(first, "deploy it");
        var (request, _) = await NextTaskAsync();
        _approvals.SetCurrentTaskOrigin(request.OriginSink);
        var gate = _approvals.PromptShellCommandAsync("make deploy");
        await ReceiveAsync(first);

        first.Abort();

        // Well inside the 30-second grace a named client would get.
        Assert.False(await gate.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ANewConnectionWithTheSameId_ReplacesTheOldOne()
    {
        await StartAsync();
        var first = await ConnectAsync("web-1");
        var second = await ConnectAsync("web-1");
        await ReceiveAsync(second);

        Assert.True(await ClosesAsync(first));
        Assert.Equal("queued", Type(JsonDocument.Parse(await DispatchRawAsync(second, "still works"))));
    }

    [Fact]
    public async Task AnInvalidClientId_IsRefused()
    {
        await StartAsync();
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader(ServeAttachClients.ClientIdHeader, "not ok!");

        var refused = await Assert.ThrowsAsync<WebSocketException>(() => ws.ConnectAsync(AttachUri(), default));

        Assert.Contains("400", refused.Message);
    }

    private async Task StartAsync()
    {
        var listener = new ServeSocketListener("/unused.sock", _queue, _approvals, NullLogger<ServeSocketListener>.Instance, Grace);
        _app = ServeHttpHost.Build(new ServeHttpOptions("127.0.0.1", 0, null), _queue, listener.HandleStreamAsync);
        await _app.StartAsync();
    }

    private Uri AttachUri() => new(_app.Urls.First().TrimEnd('/').Replace("http://", "ws://") + ServeHttpHost.AttachPath);

    /// <summary>Connects; the hello is left unread so a test can inspect it.</summary>
    private async Task<ClientWebSocket> ConnectAsync(string? clientId)
    {
        var ws = new ClientWebSocket();
        if (clientId is not null) ws.Options.SetRequestHeader(ServeAttachClients.ClientIdHeader, clientId);
        await ws.ConnectAsync(AttachUri(), default);
        return ws;
    }

    private static async Task<string> DispatchAsync(ClientWebSocket ws, string task)
    {
        await ReceiveAsync(ws); // hello
        using var queued = JsonDocument.Parse(await DispatchRawAsync(ws, task));
        return queued.RootElement.GetProperty("sessionId").GetString()!;
    }

    private static async Task<string> DispatchRawAsync(ClientWebSocket ws, string task)
    {
        await SendAsync(ws, JsonSerializer.Serialize(new { type = "user_input", text = task }));
        return await ReceiveAsync(ws);
    }

    private async Task<(ServeTaskRequest Request, ServeTaskRecord Record)> NextTaskAsync()
    {
        var request = await _queue.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var record = _queue.TryGet(request.SessionId)!;
        record.Status = ServeTaskStatus.Running;
        return (request, record);
    }

    private static void Finish(ServeTaskRecord record)
    {
        record.Status    = ServeTaskStatus.Completed;
        record.Succeeded = true;
        record.Done.SetResult(true);
    }

    private static Task SendAsync(ClientWebSocket ws, string line) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(line), WebSocketMessageType.Text, endOfMessage: true, default);

    private static async Task<string> ReceiveAsync(ClientWebSocket ws)
    {
        var buffer = new byte[65536];
        var result = await ws.ReceiveAsync(buffer, default).WaitAsync(TimeSpan.FromSeconds(10));
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    /// <summary>Reads past whatever was already sent until the server closes or drops the connection.</summary>
    private static async Task<bool> ClosesAsync(ClientWebSocket ws)
    {
        var buffer = new byte[65536];
        try
        {
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer, default).WaitAsync(TimeSpan.FromSeconds(10));
                if (result.MessageType == WebSocketMessageType.Close) return true;
            }
        }
        catch (WebSocketException) { return true; }
        catch (TimeoutException) { return false; }
    }

    private static string? Type(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Type(doc);
    }

    private static string? Type(JsonDocument doc) => doc.RootElement.GetProperty("type").GetString();
}
