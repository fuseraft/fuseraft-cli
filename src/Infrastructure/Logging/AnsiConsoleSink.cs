using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Spectre.Console;

namespace fuseraft.Infrastructure.Logging;

/// <summary>
/// Serilog sink that writes through <see cref="AnsiConsole"/> instead of directly to
/// <see cref="System.Console.Out"/>. This ensures log output respects Spectre.Console's
/// live-display management (Status spinners, Progress bars) so log lines never land on
/// the same terminal row as a spinner or live-rendered panel.
/// </summary>
internal sealed class AnsiConsoleSink(ITextFormatter formatter) : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        using var sw = new StringWriter();
        formatter.Format(logEvent, sw);
        // TrimEnd strips the trailing newline that the formatter appends; MarkupLine adds it back.
        // Markup.Escape prevents Spectre from misinterpreting brackets in log messages as markup.
        AnsiConsole.MarkupLine(Markup.Escape(sw.ToString().TrimEnd('\r', '\n')));
    }
}
