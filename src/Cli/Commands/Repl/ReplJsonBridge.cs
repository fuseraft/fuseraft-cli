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
    // arriving as their own JSONL line. Settable only so tests can read the events.
    internal static TextWriter Writer { get; set; } = Console.Out;

    // Subagents running in parallel emit from their own threads; one line must never split another.
    private static readonly Lock _gate = new();

    internal static void Emit(object payload)
    {
        var line = JsonSerializer.Serialize(payload, _opts);
        lock (_gate) Writer.WriteLine(line);
    }

    // Tool output and thinking can be huge; the panel keeps them for its viewer, so cap what crosses the pipe.
    internal const int MaxToolOutputChars = 100_000;

    internal static string Cap(string text) =>
        text.Length <= MaxToolOutputChars ? text : text[..MaxToolOutputChars] + $"\n… ({text.Length - MaxToolOutputChars:N0} more characters not shown)";
}
