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
    [Description("Override the log file path. When omitted, resolves by --session or reads all sessions.")]
    public string? Path { get; set; }
}

public sealed class LogEventsCommand : AsyncCommand<LogEventsSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, LogEventsSettings settings, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.Path))
        {
            var path = FuseraftPaths.ExpandPath(settings.Path);
            return await EventLogViewer.RenderAsync(path, settings.Last, settings.Session, settings.Event, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(settings.Session))
        {
            var path = System.IO.Path.GetFullPath(
                FuseraftPaths.ExpandSessionId(FuseraftPaths.LocalEventsLog, settings.Session));
            return await EventLogViewer.RenderAsync(path, settings.Last, null, settings.Event, cancellationToken);
        }

        // No session specified — collect all session event logs.
        var sessionsDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(FuseraftPaths.LocalLogs, "sessions"));
        IReadOnlyList<string> paths = Directory.Exists(sessionsDir)
            ? Directory.GetDirectories(sessionsDir)
                .Select(d => System.IO.Path.Combine(d, "events.jsonl"))
                .OrderBy(p => p)
                .ToList()
            : [];

        return await EventLogViewer.RenderAsync(paths, settings.Last, settings.Session, settings.Event, cancellationToken);
    }
}
