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

    internal static void Emit(object payload)
    {
        Console.WriteLine(JsonSerializer.Serialize(payload, _opts));
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

}
