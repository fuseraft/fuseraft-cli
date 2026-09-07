using System.Text.Json;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Thin JSON-over-stdio bridge used when the REPL runs inside the VS Code webview panel. All
/// events are JSONL written to stdout. Stdin reading lives in <see cref="ReplStdinPump"/> instead
/// (a single background reader owns it for the whole session — see that class for why).
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
}
