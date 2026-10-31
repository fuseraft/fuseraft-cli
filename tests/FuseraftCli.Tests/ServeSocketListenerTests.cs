using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using fuseraft.Cli.Protocol;
using fuseraft.Cli.Serve;

namespace FuseraftCli.Tests;

public sealed class ServeSocketListenerTests : IDisposable
{
    // AF_UNIX paths are capped at ~108 bytes, so keep the name short even under a long TMPDIR.
    private readonly string _socketPath = Path.Combine(
        Path.GetTempPath().Length < 60 ? Path.GetTempPath() : "/tmp", $"fr-{Guid.NewGuid():N}"[..11] + ".sock");
    private readonly CancellationTokenSource _cts = new();
    private readonly ServeSocketListener _listener;

    public ServeSocketListenerTests()
    {
        _listener = new ServeSocketListener(
            _socketPath, new ServeTaskQueue(), new ServeHumanApprovalService(allowWhenUnattended: false),
            NullLogger<ServeSocketListener>.Instance);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }

    [Fact]
    public async Task Connect_FirstLineIsVersionedHello()
    {
        if (OperatingSystem.IsWindows()) return;
        _listener.Start(_cts.Token);

        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath));
        await using var stream = new NetworkStream(client, ownsSocket: false);
        using var reader = new StreamReader(stream);

        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(line);
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("hello", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(WireProtocol.Version, doc.RootElement.GetProperty("protocolVersion").GetInt32());
    }
}
