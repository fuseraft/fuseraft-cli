using System.Buffers;
using System.Net.WebSockets;

namespace fuseraft.Cli.Serve;

/// <summary>
/// A <see cref="Stream"/> over a WebSocket in which each message is one line, so the attach
/// protocol's line-based reader and writer run unchanged over either transport.
/// </summary>
public sealed class WebSocketLineStream(
    WebSocket socket, bool ownsSocket = false, int maxMessageBytes = WebSocketLineStream.DefaultMaxMessageBytes) : Stream
{
    public const int DefaultMaxMessageBytes = 1 << 20;

    // A StreamWriter's own writes and an approval answer from another flow can overlap, and a
    // WebSocket allows only one send at a time.
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ArrayBufferWriter<byte> _pendingLine = new();
    private byte[] _incoming = [];
    private int _incomingOffset;
    private bool _receiveClosed;

    public override bool CanRead  => true;
    public override bool CanWrite => true;
    public override bool CanSeek  => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_incomingOffset == _incoming.Length && !await ReceiveLineAsync(cancellationToken))
            return 0;

        var count = Math.Min(buffer.Length, _incoming.Length - _incomingOffset);
        _incoming.AsMemory(_incomingOffset, count).CopyTo(buffer);
        _incomingOffset += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    private async Task<bool> ReceiveLineAsync(CancellationToken cancellationToken)
    {
        if (_receiveClosed) return false;

        var message = new ArrayBufferWriter<byte>();
        while (true)
        {
            ValueWebSocketReceiveResult result;
            try { result = await socket.ReceiveAsync(message.GetMemory(4096), cancellationToken); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
                _receiveClosed = true;
                return false;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                _receiveClosed = true;
                await TryCloseOutputAsync(WebSocketCloseStatus.NormalClosure, null);
                return false;
            }

            message.Advance(result.Count);
            if (message.WrittenCount > maxMessageBytes)
            {
                _receiveClosed = true;
                await TryCloseOutputAsync(WebSocketCloseStatus.MessageTooBig, $"Messages are limited to {maxMessageBytes} bytes.");
                return false;
            }
            if (result.EndOfMessage) break;
        }

        var line = message.WrittenSpan;
        if (line.EndsWith("\n"u8)) line = line[..^1];
        _incoming = [.. line, (byte)'\n'];
        _incomingOffset = 0;
        return true;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            while (!buffer.IsEmpty)
            {
                var newline = buffer.Span.IndexOf((byte)'\n');
                if (newline < 0)
                {
                    _pendingLine.Write(buffer.Span);
                    return;
                }

                _pendingLine.Write(buffer.Span[..newline]);
                buffer = buffer[(newline + 1)..];
                var line = _pendingLine.WrittenMemory;
                if (line.Span.EndsWith("\r"u8)) line = line[..^1];
                try { await socket.SendAsync(line, WebSocketMessageType.Text, endOfMessage: true, cancellationToken); }
                catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
                {
                    throw new IOException("The WebSocket connection is closed.", ex);
                }
                finally { _pendingLine.Clear(); }
            }
        }
        finally { _sendLock.Release(); }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    // Only complete lines are sent; a partial one waits for its newline.
    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private async Task TryCloseOutputAsync(WebSocketCloseStatus status, string? description)
    {
        await _sendLock.WaitAsync();
        try
        {
            if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
            await socket.CloseOutputAsync(status, description, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { }
        finally { _sendLock.Release(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sendLock.Dispose();
            if (ownsSocket)
            {
                // Disposing alone leaves a pending receive waiting; aborting ends it.
                socket.Abort();
                socket.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
