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
    [Description("Override the log file path. Defaults to .fuseraft/logs/repl_events.jsonl.")]
    public string? Path { get; set; }
}

public sealed class LogReplCommand : AsyncCommand<LogReplSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, LogReplSettings settings, CancellationToken cancellationToken)
    {
        var path = !string.IsNullOrWhiteSpace(settings.Path)
            ? FuseraftPaths.ExpandPath(settings.Path)
            : FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalReplEventsLog, FuseraftPaths.ProjectSlug(Directory.GetCurrentDirectory()));

        return await EventLogViewer.RenderAsync(path, settings.Last, settings.Session, settings.Event, cancellationToken);
    }
}
