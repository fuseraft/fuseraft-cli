using System.ComponentModel;
using Spectre.Console.Cli;
using fuseraft.Core;

namespace fuseraft.Cli.Commands.Log;

// fuseraft log events

public sealed class LogEventsSettings : CommandSettings
{
    [CommandOption("-n|--last")]
    [Description("Show only the last N entries.")]
    public int? Last { get; set; }

    [CommandOption("--session")]
    [Description("Filter by session ID (prefix match).")]
    public string? Session { get; set; }

    [CommandOption("--event")]
    [Description("Filter by event type (e.g. session_error, tool_blocked).")]
    public string? Event { get; set; }

    [CommandOption("--path")]
    [Description("Override the log file path. Defaults to .fuseraft/logs/events.jsonl.")]
    public string? Path { get; set; }
}

public sealed class LogEventsCommand : AsyncCommand<LogEventsSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, LogEventsSettings settings, CancellationToken cancellationToken)
    {
        var path = !string.IsNullOrWhiteSpace(settings.Path)
            ? FuseraftPaths.ExpandPath(settings.Path)
            : System.IO.Path.GetFullPath(FuseraftPaths.LocalEventsLog);

        return await EventLogViewer.RenderAsync(path, settings.Last, settings.Session, settings.Event, cancellationToken);
    }
}
