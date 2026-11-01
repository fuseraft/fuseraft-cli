using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using fuseraft.Cli.Protocol;
using fuseraft.Cli.Serve;

namespace FuseraftCli.Tests;

/// <summary>
/// <see cref="ServeHttpHost"/> — when a token is required, what the listener refuses, and the
/// attach protocol over a real WebSocket.
/// </summary>
public sealed class ServeHttpHostTests
{
    private const string Token = "s3cret-token";

    [Fact]
    public void Validate_BeyondLoopbackWithoutAToken_IsRefused()
    {
        var problem = ServeHttpHost.Validate(new ServeHttpOptions("0.0.0.0", 5000, null));

        Assert.NotNull(problem);
        Assert.Contains(ServeHttpHost.TokenVariable, problem);
    }

    [Theory]
    [InlineData("0.0.0.0", Token)]
    [InlineData("localhost", null)]
    [InlineData("127.0.0.1", null)]
    public void Validate_AcceptsATokenOrLoopback(string bind, string? token) =>
        Assert.Null(ServeHttpHost.Validate(new ServeHttpOptions(bind, 5000, token)));

    [Fact]
    public void Validate_RejectsAnOutOfRangePort() =>
        Assert.Contains("--http-port", ServeHttpHost.Validate(new ServeHttpOptions("localhost", 70000, null)));

    [Theory]
    [InlineData("localhost", "ws://localhost:5000/attach")]
    [InlineData("0.0.0.0", "ws://localhost:5000/attach")]
    [InlineData("192.168.1.20", "ws://192.168.1.20:5000/attach")]
    public void LocalUrl_NamesAnAddressThisMachineCanReach(string bind, string expected) =>
        Assert.Equal(expected, ServeHttpHost.LocalUrl(new ServeHttpOptions(bind, 5000, null), 5000, webSocket: true, ServeHttpHost.AttachPath));

    [Fact]
    public async Task Attach_OverWebSocket_SpeaksTheAttachProtocol()
    {
        var queue = new ServeTaskQueue();
        await using var app = await StartAsync(token: null, queue);
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(AttachUri(app), default);

        using (var hello = JsonDocument.Parse(await ReceiveAsync(ws)))
        {
            Assert.Equal("hello", hello.RootElement.GetProperty("type").GetString());
            Assert.Equal(WireProtocol.Version, hello.RootElement.GetProperty("protocolVersion").GetInt32());
        }

        await ws.SendAsync(Encoding.UTF8.GetBytes("write a haiku"), WebSocketMessageType.Text, endOfMessage: true, default);

        using var queued = JsonDocument.Parse(await ReceiveAsync(ws));
        Assert.Equal("queued", queued.RootElement.GetProperty("type").GetString());
        var sessionId = queued.RootElement.GetProperty("sessionId").GetString()!;
        Assert.Equal("write a haiku", queue.TryGet(sessionId)!.Task);
    }

    [Fact]
    public async Task Attach_WithAToken_RequiresIt()
    {
        await using var app = await StartAsync(Token);

        using var anonymous = new ClientWebSocket();
        var refused = await Assert.ThrowsAsync<WebSocketException>(() => anonymous.ConnectAsync(AttachUri(app), default));
        Assert.Contains("401", refused.Message);

        using var authorized = new ClientWebSocket();
        authorized.Options.SetRequestHeader("Authorization", "Bearer " + Token);
        await authorized.ConnectAsync(AttachUri(app), default);
        Assert.Contains("hello", await ReceiveAsync(authorized));
    }

    [Fact]
    public async Task Mcp_WithAToken_RequiresIt()
    {
        await using var app = await StartAsync(Token);
        using var http = new HttpClient();

        var response = await http.PostAsync(BaseUrl(app) + "/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Attach_FromABrowserOrigin_IsRefused()
    {
        await using var app = await StartAsync(token: null);
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "https://evil.example");

        var refused = await Assert.ThrowsAsync<WebSocketException>(() => ws.ConnectAsync(AttachUri(app), default));
        Assert.Contains("403", refused.Message);
    }

    [Fact]
    public async Task Loopback_RefusesAForeignHostHeader()
    {
        await using var app = await StartAsync(token: null);
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl(app) + ServeHttpHost.AttachPath);
        request.Headers.Host = "rebound.evil.example";

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
    }

    [Fact]
    public async Task Attach_WithoutAnUpgrade_IsABadRequest()
    {
        await using var app = await StartAsync(token: null);
        using var http = new HttpClient();

        var response = await http.GetAsync(BaseUrl(app) + ServeHttpHost.AttachPath);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Stopping_EndsAnAttachedConnection()
    {
        var app = await StartAsync(token: null);
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(AttachUri(app), default);
        await ReceiveAsync(ws);

        var stop = app.StopAsync();

        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        await app.DisposeAsync();
    }

    private static async Task<WebApplication> StartAsync(string? token, ServeTaskQueue? queue = null)
    {
        queue ??= new ServeTaskQueue();
        var listener = new ServeSocketListener(
            "/unused.sock", queue, new ServeHumanApprovalService(allowWhenUnattended: false),
            NullLogger<ServeSocketListener>.Instance);
        var app = ServeHttpHost.Build(new ServeHttpOptions("127.0.0.1", 0, token), queue, listener.HandleStreamAsync);
        await app.StartAsync();
        return app;
    }

    private static string BaseUrl(WebApplication app) => app.Urls.First().TrimEnd('/');

    private static Uri AttachUri(WebApplication app) =>
        new(BaseUrl(app).Replace("http://", "ws://") + ServeHttpHost.AttachPath);

    private static async Task<string> ReceiveAsync(ClientWebSocket ws)
    {
        var buffer = new byte[16384];
        var result = await ws.ReceiveAsync(buffer, default).WaitAsync(TimeSpan.FromSeconds(10));
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }
}
