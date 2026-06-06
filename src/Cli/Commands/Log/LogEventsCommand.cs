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

        var globalSessionsRoot = System.IO.Path.Combine(FuseraftPaths.GlobalRoot, "logs", "sessions");

        if (!string.IsNullOrWhiteSpace(settings.Session))
        {
            // Search all project-slug subdirs in the global sessions root for a
            // matching session ID prefix, then fall back to the legacy local path.
            string? path = null;
            if (Directory.Exists(globalSessionsRoot))
            {
                path = Directory.GetDirectories(globalSessionsRoot)
                    .SelectMany(Directory.GetDirectories)
                    .FirstOrDefault(d => System.IO.Path.GetFileName(d)
                        .StartsWith(settings.Session, StringComparison.OrdinalIgnoreCase));
                if (path is not null)
                    path = System.IO.Path.Combine(path, "events.jsonl");
            }
            path ??= System.IO.Path.GetFullPath(
                FuseraftPaths.ExpandSessionId(FuseraftPaths.LocalEventsLog, settings.Session));
            return await EventLogViewer.RenderAsync(path, settings.Last, null, settings.Event, cancellationToken);
        }

        // No session specified — collect all global session event logs.
        IReadOnlyList<string> paths = Directory.Exists(globalSessionsRoot)
            ? Directory.GetDirectories(globalSessionsRoot)
                .SelectMany(Directory.GetDirectories)
                .Select(d => System.IO.Path.Combine(d, "events.jsonl"))
                .Where(File.Exists)
                .OrderBy(p => p)
                .ToList()
            : [];

        return await EventLogViewer.RenderAsync(paths, settings.Last, settings.Session, settings.Event, cancellationToken);
    }
}
