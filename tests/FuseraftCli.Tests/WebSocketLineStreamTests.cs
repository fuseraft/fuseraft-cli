using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using fuseraft.Cli.Serve;

namespace FuseraftCli.Tests;

public sealed class WebSocketLineStreamTests : IAsyncLifetime
{
    private TcpClient _client = null!;
    private TcpClient _server = null!;
    private WebSocket _local = null!;
    private WebSocket _remote = null!;

    public async Task InitializeAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await _client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        _server = await accept;
        listener.Stop();
        _local  = WebSocket.CreateFromStream(_server.GetStream(), isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
        _remote = WebSocket.CreateFromStream(_client.GetStream(), isServer: false, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
    }

    public Task DisposeAsync()
    {
        _local.Dispose();
        _remote.Dispose();
        _client.Dispose();
        _server.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Write_SendsOneMessagePerLine()
    {
        await using var stream = new WebSocketLineStream(_local);
        await using var writer = new StreamWriter(stream) { AutoFlush = true };

        await writer.WriteAsync("first\r\nsec");
        await writer.WriteAsync("ond\n");

        Assert.Equal("first", await ReceiveAsync());
        Assert.Equal("second", await ReceiveAsync());
    }

    [Fact]
    public async Task Write_HoldsAPartialLineUntilItsNewline()
    {
        await using var stream = new WebSocketLineStream(_local);
        await stream.WriteAsync("partial"u8.ToArray());

        var receive = ReceiveAsync();
        await Task.Delay(100);
        Assert.False(receive.IsCompleted);

        await stream.WriteAsync("\n"u8.ToArray());
        Assert.Equal("partial", await receive);
    }

    [Fact]
    public async Task Read_EachMessageIsOneLine()
    {
        await using var stream = new WebSocketLineStream(_local);
        using var reader = new StreamReader(stream);

        await SendAsync("one");
        await SendAsync("two\n");
        await _remote.SendAsync("thr"u8.ToArray(), WebSocketMessageType.Text, endOfMessage: false, default);
        await _remote.SendAsync("ee"u8.ToArray(), WebSocketMessageType.Text, endOfMessage: true, default);

        Assert.Equal("one", await ReadLineAsync(reader));
        Assert.Equal("two", await ReadLineAsync(reader));
        Assert.Equal("three", await ReadLineAsync(reader));
    }

    [Fact]
    public async Task Read_EndsWhenThePeerCloses()
    {
        await using var stream = new WebSocketLineStream(_local);
        using var reader = new StreamReader(stream);

        await _remote.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, default);

        Assert.Null(await ReadLineAsync(reader));
    }

    [Fact]
    public async Task Read_AnOversizedMessageClosesTheConnection()
    {
        await using var stream = new WebSocketLineStream(_local, maxMessageBytes: 16);
        using var reader = new StreamReader(stream);

        await SendAsync(new string('x', 64));

        Assert.Null(await ReadLineAsync(reader));
        var result = await _remote.ReceiveAsync(new byte[256], default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, _remote.CloseStatus);
    }

    [Fact]
    public async Task Write_AfterTheSocketIsGone_ThrowsIOException()
    {
        var stream = new WebSocketLineStream(_local, ownsSocket: true);
        await stream.DisposeAsync();

        await Assert.ThrowsAsync<IOException>(() => new WebSocketLineStream(_local).WriteAsync("x\n"u8.ToArray()).AsTask());
    }

    private Task SendAsync(string text) =>
        _remote.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, default);

    private async Task<string> ReceiveAsync()
    {
        var buffer = new byte[4096];
        var result = await _remote.ReceiveAsync(buffer, default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.EndOfMessage);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static Task<string?> ReadLineAsync(StreamReader reader) =>
        reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
}
