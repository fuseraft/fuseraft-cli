namespace fuseraft.Infrastructure;

/// <summary>
/// Wraps a network <see cref="Stream"/> and throws <see cref="TimeoutException"/> if the
/// SSE stream stops delivering real content events for longer than the configured idle window.
///
/// <para>
/// <c>HttpClient.Timeout</c> only covers time-to-first-byte. Once an SSE connection is open
/// the body can block indefinitely. A naive byte-level idle timer is defeated by keep-alive
/// ping events that providers (e.g. Anthropic) send every ~20–30 s; those pings deliver bytes
/// without any model output, silently resetting a byte-level timer forever.
/// </para>
///
/// <para>
/// This wrapper parses the SSE framing (field lines separated by blank lines) and maintains
/// two independent timers:
/// <list type="bullet">
///   <item><b>Byte-level</b> — <see cref="ByteIdleTimeout"/> (2 min): fires when the TCP
///     connection delivers no bytes at all, indicating a dead socket.</item>
///   <item><b>Content-event-level</b> — <paramref name="contentIdleTimeout"/> (default 5 min):
///     fires when no non-ping SSE event with a <c>data:</c> field has been received. Ping
///     events (<c>event: ping</c>) and bare comment lines (<c>: …</c>) do NOT reset this
///     timer, so a stalled model is detected even while keep-alives continue.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class SseEventIdleTimeoutStream(Stream inner, TimeSpan contentIdleTimeout) : Stream
{
    // Byte-level deadline: if the TCP socket delivers nothing at all for this long, the
    // connection is dead regardless of SSE state.
    private static readonly TimeSpan ByteIdleTimeout = TimeSpan.FromSeconds(120);

    // Track when we last saw a non-ping SSE data event.
    private DateTime _lastContentEventAt = DateTime.UtcNow;

    // SSE line-parse state.
    private readonly byte[] _lineBuf      = new byte[512];
    private int              _lineLen      = 0;
    private bool             _prevWasNl    = false;  // true when previous byte was '\n'
    private bool             _inPingEvent  = false;  // current SSE event has "event: ping"
    private bool             _hasDataLine  = false;  // current SSE event has at least one "data:" line

    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        using var byteCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        byteCts.CancelAfter(ByteIdleTimeout);
        int n;
        try
        {
            n = await inner.ReadAsync(buffer, offset, count, byteCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Streaming idle timeout: no bytes received for {ByteIdleTimeout.TotalSeconds:0}s. " +
                "The API connection appears to be dead.");
        }
        if (n > 0) CheckContentIdle(buffer.AsSpan(offset, n));
        return n;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var byteCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        byteCts.CancelAfter(ByteIdleTimeout);
        int n;
        try
        {
            n = await inner.ReadAsync(buffer, byteCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Streaming idle timeout: no bytes received for {ByteIdleTimeout.TotalSeconds:0}s. " +
                "The API connection appears to be dead.");
        }
        if (n > 0) CheckContentIdle(buffer.Span[..n]);
        return n;
    }

    // Parse bytes into SSE lines, detect event boundaries and ping events, then check whether
    // the content-idle window has been exceeded.
    private void CheckContentIdle(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            if (b == (byte)'\n')
            {
                if (_prevWasNl || _lineLen == 0)
                {
                    // Blank line → SSE event boundary.
                    // Count as a content event only when it has a data: field and is not a ping.
                    if (_hasDataLine && !_inPingEvent)
                        _lastContentEventAt = DateTime.UtcNow;
                    _inPingEvent = false;
                    _hasDataLine = false;
                    _lineLen     = 0;
                }
                else
                {
                    // End of a field line — strip trailing \r and classify.
                    int len = _lineLen;
                    if (len > 0 && _lineBuf[len - 1] == (byte)'\r') len--;
                    ClassifyLine(_lineBuf.AsSpan(0, len));
                    _lineLen = 0;
                }
                _prevWasNl = true;
            }
            else
            {
                _prevWasNl = false;
                if (_lineLen < _lineBuf.Length)
                    _lineBuf[_lineLen++] = b;
            }
        }

        if (DateTime.UtcNow - _lastContentEventAt > contentIdleTimeout)
            throw new TimeoutException(
                $"Streaming content idle timeout: no non-ping SSE event received for " +
                $"{contentIdleTimeout.TotalMinutes:0} minute(s). " +
                "Keep-alive pings are flowing but the model appears to have stalled.");
    }

    // Sets _inPingEvent or _hasDataLine based on the SSE field line.
    private void ClassifyLine(ReadOnlySpan<byte> line)
    {
        if (line.IsEmpty) return;

        // SSE comment (":" prefix) — treat as keep-alive, do nothing.
        if (line[0] == (byte)':') return;

        // Cheaply decode — field names are ASCII.
        int colon = line.IndexOf((byte)':');
        if (colon < 0) return;

        var field = System.Text.Encoding.ASCII.GetString(line[..colon]).Trim();
        var value = System.Text.Encoding.ASCII.GetString(line[(colon + 1)..]).Trim();

        if (field.Equals("event", StringComparison.OrdinalIgnoreCase) &&
            value.Equals("ping", StringComparison.OrdinalIgnoreCase))
            _inPingEvent = true;

        if (field.Equals("data", StringComparison.OrdinalIgnoreCase))
            _hasDataLine = true;
    }

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin)        => throw new NotSupportedException();
    public override void SetLength(long value)                       => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
