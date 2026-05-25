using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;

namespace fuseraft.Cli.Commands.Log;

// fuseraft log app

public sealed class LogAppSettings : CommandSettings
{
    [CommandOption("-n|--last")]
    [Description("Show only the last N lines. Defaults to 50.")]
    public int Last { get; set; } = 50;

    [CommandOption("--level")]
    [Description("Filter by log level prefix: inf, wrn, err, dbg.")]
    public string? Level { get; set; }

    [CommandOption("--path")]
    [Description("Override the log file path. Defaults to .fuseraft/logs/app.log.")]
    public string? Path { get; set; }
}

public sealed class LogAppCommand : AsyncCommand<LogAppSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, LogAppSettings settings, CancellationToken cancellationToken)
    {
        var path = !string.IsNullOrWhiteSpace(settings.Path)
            ? FuseraftPaths.ExpandPath(settings.Path)
            : System.IO.Path.GetFullPath(FuseraftPaths.LocalAppLog);

        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine("[dim]No application log found.[/]");
            AnsiConsole.MarkupLine($"[dim]Expected path: {Markup.Escape(path)}[/]");
            return 0;
        }

        var lines = await File.ReadAllLinesAsync(path, cancellationToken);

        // Filter by level if requested (matches Serilog format: [HH:mm:ss LEV])
        if (!string.IsNullOrWhiteSpace(settings.Level))
        {
            var lvl = settings.Level.Trim().ToUpperInvariant();
            lines = lines
                .Where(l => l.Contains($" {lvl}]", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        // Take last N
        if (settings.Last > 0 && lines.Length > settings.Last)
            lines = lines[^settings.Last..];

        if (lines.Length == 0)
        {
            AnsiConsole.MarkupLine("[dim]No matching log lines.[/]");
            return 0;
        }

        foreach (var line in lines)
            AnsiConsole.MarkupLine(ColorizeAppLogLine(line));

        AnsiConsole.MarkupLine($"[dim]{lines.Length} line{(lines.Length == 1 ? "" : "s")}  ·  {Markup.Escape(path)}[/]");
        return 0;
    }

    private static string ColorizeAppLogLine(string line)
    {
        // Serilog format: [HH:mm:ss LEV] Message
        if (line.Length < 15) return Markup.Escape(line);
        if (line.Contains(" ERR]")) return $"[red]{Markup.Escape(line)}[/]";
        if (line.Contains(" WRN]")) return $"[yellow]{Markup.Escape(line)}[/]";
        if (line.Contains(" DBG]")) return $"[dim]{Markup.Escape(line)}[/]";
        return $"[dim]{Markup.Escape(line)}[/]";
    }
}
