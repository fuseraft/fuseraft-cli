using System.Text.Json;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Thin JSON-over-stdio bridge used when the REPL runs inside the VS Code
/// webview panel. All events are JSONL written to stdout; input is read as
/// JSONL from stdin and the "text" field is extracted.
/// </summary>
internal static class ReplJsonBridge
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Captured once, before any command handler can redirect Console.Out to a
    // capture buffer (see ReplTurn's slash-command output capture). Emitted
    // events must always reach the real stdout, never a redirected one, or
    // they get swallowed into another event's captured text instead of
    // arriving as their own JSONL line.
    private static readonly TextWriter _stdout = Console.Out;

    internal static void Emit(object payload)
    {
        _stdout.WriteLine(JsonSerializer.Serialize(payload, _opts));
    }

    /// <summary>
    /// Sentinel returned by <see cref="ReadInput"/> when the extension sends an
    /// <c>{"type":"interrupt"}</c> message (Windows path, where SIGINT cannot be
    /// delivered to a child process). The loop handles this by cancelling the
    /// active request and continuing rather than breaking the session.
    /// </summary>
    internal const string InterruptToken = "\x01interrupt\x01";

    /// <summary>
    /// Reads one JSON line from stdin and returns the "text" field value.
    /// Returns <see cref="InterruptToken"/> when a <c>{"type":"interrupt"}</c>
    /// message is received. Falls back to the raw line for non-JSON input.
    /// Returns null on EOF.
    /// </summary>
    internal static string? ReadInput()
    {
        var line = Console.ReadLine();
        if (line is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("type", out var typeEl) &&
                typeEl.GetString() is "interrupt")
                return InterruptToken;
            if (doc.RootElement.TryGetProperty("text", out var text))
                return text.GetString();
        }
        catch { }
        return line;
    }

    /// <summary>
    /// Blocks for one JSON line from stdin carrying the webview's answer to a pending
    /// <c>approval_request</c> event (see <see cref="fuseraft.Cli.JsonBridgeHumanApprovalService"/>),
    /// e.g. <c>{"type":"approval_response","approved":true}</c>. Only ever called from within a
    /// single shell-tool-call approval gate, never concurrently with <see cref="ReadInput"/> (that
    /// is only read between turns), so there is no contention over stdin. Anything other than a
    /// well-formed approval with <c>approved:true</c> — malformed JSON, the wrong "type", or EOF
    /// because the panel/process went away — denies the command rather than risking a false
    /// approval.
    /// </summary>
    internal static bool ReadApprovalResponse()
    {
        var line = Console.ReadLine();
        if (line is null) return false;
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
