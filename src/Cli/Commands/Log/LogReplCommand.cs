using System.ComponentModel;
using Spectre.Console.Cli;
using fuseraft.Core;

namespace fuseraft.Cli.Commands.Log;

// fuseraft log repl

public sealed class LogReplSettings : CommandSettings
{
    [CommandOption("-n|--last")]
    [Description("Show only the last N entries.")]
    public int? Last { get; set; }

    [CommandOption("--session")]
    [Description("Filter by session ID (prefix match).")]
    public string? Session { get; set; }

    [CommandOption("--event")]
    [Description("Filter by event type (e.g. command, skill_curation_complete).")]
    public string? Event { get; set; }

    [CommandOption("--path")]
    [Description("Override the log file path. Defaults to all session logs under .fuseraft/logs/repl_events/.")]
    public string? Path { get; set; }
}

public sealed class LogReplCommand : AsyncCommand<LogReplSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, LogReplSettings settings, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.Path))
        {
            var path = FuseraftPaths.ExpandPath(settings.Path);
            return await EventLogViewer.RenderAsync(path, settings.Last, settings.Session, settings.Event, cancellationToken);
        }

        var slug = FuseraftPaths.ProjectSlug(Directory.GetCurrentDirectory());
        var dir  = FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalReplEventsDir, slug);

        if (!string.IsNullOrWhiteSpace(settings.Session))
        {
            // Each REPL session gets its own log file — resolve an exact match first, then
            // fall back to a prefix match against the other files in the project's directory.
            var trimmed = settings.Session.Trim();
            var exact   = FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalReplEventsLog, trimmed, slug);
            var path = File.Exists(exact)
                ? exact
                : Directory.Exists(dir)
                    ? Directory.GetFiles(dir, "*.jsonl")
                        .FirstOrDefault(f => System.IO.Path.GetFileNameWithoutExtension(f)
                            .StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
                    : null;
            return await EventLogViewer.RenderAsync(path ?? exact, settings.Last, null, settings.Event, cancellationToken);
        }

        // No session specified — collect every session's log for this project.
        IReadOnlyList<string> paths = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.jsonl").OrderBy(p => p).ToList()
            : [];

        return await EventLogViewer.RenderAsync(paths, settings.Last, settings.Session, settings.Event, cancellationToken);
    }
}
