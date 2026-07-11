using System.Text.RegularExpressions;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Terminal-presentation utilities (spinner, drip-print, ANSI stripping) used both by turn
/// execution and by the sub-agent REPL commands. Extracted from <see cref="ReplTurn"/> — these
/// take no <see cref="ReplSessionContext"/> and were already independently consumed by
/// <c>ReplCommands.Agents.cs</c> for <c>/diagnose</c>/<c>/explore</c>/<c>/locate</c>-style
/// sub-agent commands, unrelated to turn execution.
/// </summary>
internal static class ReplConsole
{
    internal static readonly string[] SpinnerFrames = OperatingSystem.IsWindows()
        ? ["-", "\\", "|", "/"]
        : ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    // Drip-prints text character by character so large chunks don't pop in all at once.
    // Skips the delay when output is redirected (e.g. piped to a file).
    internal static async Task WriteChunkSmoothAsync(string text, CancellationToken ct)
    {
        if (Console.IsOutputRedirected || text.Length == 0)
        {
            Console.Write(text);
            return;
        }
        foreach (var ch in text)
        {
            Console.Write(ch);
            await Task.Delay(2, ct);
        }
    }

    internal static async Task RunSpinnerAsync(string label, CancellationToken ct, DateTime? startedAt = null)
    {
        var i = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var elapsed = startedAt.HasValue
                    ? $" ({(int)(DateTime.UtcNow - startedAt.Value).TotalSeconds}s)"
                    : string.Empty;
                var frame = SpinnerFrames[i % SpinnerFrames.Length];
                var text  = $"{frame} {label}{elapsed}";

                // Clamp to one terminal line so the text never wraps. When a line wraps,
                // the subsequent \r\x1b[2K only clears the continuation line and leaves
                // the first visual line as a ghost — producing the multi-line cascade.
                // Guard against Console.WindowWidth failing on non-interactive consoles.
                if (!Console.IsOutputRedirected)
                {
                    var width = 0;
                    try { width = Console.WindowWidth; } catch { }
                    if (width > 4 && text.Length > width - 1)
                        text = text[..(width - 2)] + "…";
                }

                // \r   — move to column 0
                // \x1b[2K — erase entire line (prevents leftover chars when label shrinks)
                Console.Write($"\r\x1b[2K\x1b[2m{text}\x1b[0m");
                i++;
                await Task.Delay(80, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    internal static void ClearSpinnerLine()
    {
        Console.Write("\r\x1b[2K");
    }

    // Strips ANSI escape sequences (CSI colour codes, OSC sequences, etc.)
    // from text captured while AnsiConsole runs in no-colour mode.  The
    // pattern is intentionally broad so residual escape bytes do not leak
    // into the JSON token emitted to the webview.
    private static readonly Regex _ansiPattern =
        new(@"\x1b(?:\[[^m]*m|\][^\x07]*\x07|[()][AB012]|[=>])", RegexOptions.Compiled);

    internal static string StripAnsi(string text) => _ansiPattern.Replace(text, string.Empty);
}
