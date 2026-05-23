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
    /// Reads one JSON line from stdin and returns the "text" field value.
    /// Falls back to returning the raw line if it cannot be parsed as JSON.
    /// Returns null on EOF.
    /// </summary>
    internal static string? ReadInput()
    {
        var line = Console.ReadLine();
        if (line is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("text", out var text))
                return text.GetString();
        }
        catch { }
        return line;
    }
}
