using System.Text.Json;
using System.Threading.Channels;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Owns stdin for the life of a JSON-bridge REPL session (<c>fuseraft repl --vscode</c>). A
/// single background loop is the only thing that ever reads from it once <see cref="Start"/> is
/// called; everything else consumes lines through <see cref="ReadInputAsync"/> /
/// <see cref="ReadApprovalResponseAsync"/> instead of touching the underlying reader directly.
///
/// This exists because of how the "Stop" button has to work on Windows. There's no way to
/// deliver a real SIGINT to a child process there, so the extension sends the interrupt as an
/// in-band <c>{"type":"interrupt"}</c> stdin line instead (see ReplPanelProvider.ts). The old
/// design (<c>ReplJsonBridge.ReadInput</c>) only read stdin from inside the main turn loop, once
/// per turn boundary — so an interrupt line written while a turn was mid-stream (the main loop
/// blocked awaiting <c>ExecuteAsync</c>, not calling ReadInput) just sat unread in the pipe until
/// the turn finished on its own. By then <c>ctx.ActiveCts</c> was already null and the interrupt
/// was silently a no-op: clicking Stop mid-response did nothing. Routing every stdin line through
/// this always-running pump lets an interrupt be acted on the instant it arrives, regardless of
/// what the main loop is awaiting.
/// </summary>
public sealed class ReplStdinPump
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly TextReader _input;
    private readonly Func<CancellationTokenSource?> _getActiveCts;
    private Task? _pumpTask;

    internal ReplStdinPump(TextReader input, Func<CancellationTokenSource?> getActiveCts)
    {
        _input        = input;
        _getActiveCts = getActiveCts;
    }

    /// <summary>Idempotent — a second call is a no-op.</summary>
    internal void Start() => _pumpTask ??= Task.Run(PumpLoopAsync);

    private async Task PumpLoopAsync()
    {
        while (true)
        {
            string? line;
            try   { line = await _input.ReadLineAsync(); }
            catch { line = null; }

            if (line is null)
            {
                _lines.Writer.TryComplete();
                return;
            }

            if (IsInterruptLine(line))
            {
                var c = _getActiveCts();
                if (c is not null && !c.IsCancellationRequested) c.Cancel();
                continue; // handled here — never queued for ReadInputAsync/ReadApprovalResponseAsync
            }

            _lines.Writer.TryWrite(line);
        }
    }

    internal static bool IsInterruptLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("type", out var t) && t.GetString() is "interrupt";
        }
        catch { return false; }
    }

    /// <summary>Returns the next non-interrupt line's "text" field, or null once stdin is closed.</summary>
    internal async Task<string?> ReadInputAsync()
    {
        while (await _lines.Reader.WaitToReadAsync())
            if (_lines.Reader.TryRead(out var line))
                return ExtractText(line);
        return null;
    }

    internal static string? ExtractText(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("text", out var text)) return text.GetString();
        }
        catch { }
        return line;
    }

    /// <summary>
    /// Blocks for the webview's answer to a pending <c>approval_request</c> event (see
    /// <see cref="fuseraft.Cli.JsonBridgeHumanApprovalService"/>), e.g.
    /// <c>{"type":"approval_response","approved":true}</c>. Only ever called from within a single
    /// shell-tool-call approval gate, never concurrently with <see cref="ReadInputAsync"/> (that's
    /// only awaited between turns), so both can safely share this pump's one line channel.
    /// Anything other than a well-formed approval with <c>approved:true</c> — malformed JSON, the
    /// wrong "type", or stdin closing because the panel/process went away — denies the command
    /// rather than risking a false approval.
    /// </summary>
    internal async Task<bool> ReadApprovalResponseAsync()
    {
        while (await _lines.Reader.WaitToReadAsync())
            if (_lines.Reader.TryRead(out var line))
                return ExtractApproval(line);
        return false;
    }

    internal static bool ExtractApproval(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("type", out var typeEl) &&
                typeEl.GetString() is "approval_response" &&
                doc.RootElement.TryGetProperty("approved", out var approvedEl) &&
                approvedEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return approvedEl.GetBoolean();
        }
        catch { }
        return false;
    }
}
