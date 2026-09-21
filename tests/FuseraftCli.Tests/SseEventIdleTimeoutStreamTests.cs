using fuseraft.Infrastructure;

namespace FuseraftCli.Tests;

public sealed class SseEventIdleTimeoutStreamTests
{
    private sealed class NeverRespondsStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    [Fact]
    public async Task SilentSocket_TripsTheConfiguredByteIdleTimeout()
    {
        var stream = new SseEventIdleTimeoutStream(
            new NeverRespondsStream(), contentIdleTimeout: TimeSpan.FromMinutes(5), byteIdle: TimeSpan.FromMilliseconds(80));

        var ex = await Assert.ThrowsAsync<TimeoutException>(async () => await stream.ReadAsync(new byte[16]));

        Assert.Contains("no bytes received", ex.Message);
    }

    [Fact]
    public void PingsAlone_DoNotKeepAStalledModelAlive_PastTheConfiguredContentWindow()
    {
        var pings  = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("event: ping\ndata: {}\n\n", 4)));
        var stream = new SseEventIdleTimeoutStream(new MemoryStream(pings), contentIdleTimeout: TimeSpan.FromMilliseconds(150));
        var buffer = new byte[24];

        Assert.True(stream.Read(buffer, 0, buffer.Length) > 0);
        Thread.Sleep(450);

        var ex = Assert.Throws<TimeoutException>(() => stream.Read(buffer, 0, buffer.Length));
        Assert.Contains("content idle timeout", ex.Message);
    }

    [Fact]
    public void ContentEvents_KeepTheStreamAlive()
    {
        var events = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("data: {\"x\":1}\n\n", 4)));
        var stream = new SseEventIdleTimeoutStream(new MemoryStream(events), contentIdleTimeout: TimeSpan.FromMilliseconds(150));
        var buffer = new byte[16];

        Assert.True(stream.Read(buffer, 0, buffer.Length) > 0);
        Thread.Sleep(450);

        // A real data event lands in this read, resetting the window before it is checked.
        Assert.True(stream.Read(buffer, 0, buffer.Length) > 0);
    }
}
