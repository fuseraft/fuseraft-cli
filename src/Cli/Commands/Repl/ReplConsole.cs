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

    // Set while some other code path (a human-approval prompt, most notably) needs
    // exclusive, uncorrupted control of the terminal. RunSpinnerAsync polls this every
    // tick and skips its own write for as long as it's non-zero, so a multi-line prompt
    // printed elsewhere isn't clobbered by the next 80ms `\r\x1b[2K<frame>` redraw — see
    // SuspendSpinner's doc comment for the bug this fixes.
    private static int _suspendCount;

    internal static async Task RunSpinnerAsync(string label, CancellationToken ct, DateTime? startedAt = null)
    {
        var i = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (Volatile.Read(ref _suspendCount) > 0)
                {
                    await Task.Delay(80, ct);
                    continue;
                }

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

    /// <summary>
    /// Suspends the spinner's own writes for the lifetime of the returned token, clearing
    /// the current spinner line first so a prompt can render into a clean line instead of
    /// racing the spinner's next ~80ms redraw.
    /// </summary>
    /// <remarks>
    /// Bug this fixes: <c>RunSpinnerAsync</c> runs on its own background task and writes
    /// <c>\r\x1b[2K&lt;frame&gt;</c> on a timer with zero coordination with anything else
    /// touching the console. Every <c>IHumanApprovalService</c> approval prompt is a
    /// blocking <c>Console.Write</c> + <c>Console.ReadLine()</c> issued from inside a tool
    /// call, i.e. while that same turn's "fusing…" spinner is still ticking — so the very
    /// next spinner frame silently erases and overwrites the prompt line. The process is
    /// still correctly blocked on <c>Console.ReadLine()</c> underneath (confirmed via a real
    /// REPL session: near-zero CPU for many minutes, and a blind "y"+Enter unblocks it) — the
    /// prompt is just invisible, so a real user sees what looks like an indefinite hang with
    /// no visible question, not something they'd know to answer.
    ///
    /// Safe to call even when no spinner is currently running (idle sessions, orchestration
    /// callers with no spinner concept) — it degrades to a harmless line-clear. Nestable: the
    /// spinner only resumes once every suspension has been disposed.
    /// </remarks>
    internal static IDisposable SuspendSpinner()
    {
        if (Interlocked.Increment(ref _suspendCount) == 1)
        {
            ClearSpinnerLine();
        }
        return new SpinnerSuspension();
    }

    private sealed class SpinnerSuspension : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Interlocked.Decrement(ref _suspendCount);
        }
    }

    // Strips ANSI escape sequences (CSI colour codes, OSC sequences, etc.)
    // from text captured while AnsiConsole runs in no-colour mode.  The
    // pattern is intentionally broad so residual escape bytes do not leak
    // into the JSON token emitted to the webview.
    private static readonly Regex _ansiPattern =
        new(@"\x1b(?:\[[^m]*m|\][^\x07]*\x07|[()][AB012]|[=>])", RegexOptions.Compiled);

    internal static string StripAnsi(string text) => _ansiPattern.Replace(text, string.Empty);
}
