using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;
using fuseraft.Core.Interfaces;

namespace fuseraft.Cli.Commands;

public sealed class SessionsSettings : CommandSettings
{
    [CommandOption("-a|--all")]
    [Description("Include completed sessions (default: incomplete only).")]
    public bool All { get; set; }

    [CommandOption("-d|--delete")]
    [Description("Delete a session by ID, or 'all' to delete every completed session.")]
    public string? Delete { get; set; }

    [CommandOption("--prune")]
    [Description("Delete sessions whose config file no longer exists on disk (orphaned sessions).")]
    public bool Prune { get; set; }

    [CommandOption("--project")]
    [Description("Filter by project path fragment (e.g. 'brewer' or 'fuseraft-cli').")]
    public string? Project { get; set; }

    [CommandOption("--cleanup")]
    [Description("Delete sessions older than --older-than, removing both global checkpoints and local session directories.")]
    public bool Cleanup { get; set; }

    [CommandOption("--older-than <age>")]
    [Description("Age threshold for --cleanup (e.g. 7d, 2w, 24h). Defaults to 30d when omitted.")]
    public string? OlderThan { get; set; }
}

/// <summary>
/// Lists and manages persisted session checkpoints.
/// </summary>
public sealed class SessionsCommand(ISessionStore sessionStore) : AsyncCommand<SessionsSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, SessionsSettings settings, CancellationToken cancellationToken)
    {
        // Prune orphaned sessions (config file no longer exists on disk).
        if (settings.Prune)
        {
            var all = await sessionStore.ListIndexAsync(cancellationToken);
            var orphaned = all
                .Where(s => string.IsNullOrEmpty(s.ConfigPath) || !File.Exists(s.ConfigPath))
                .ToList();

            if (orphaned.Count == 0)
            {
                AnsiConsole.MarkupLine("[green]✓ No orphaned sessions found.[/]");
                return 0;
            }

            foreach (var s in orphaned)
                await sessionStore.DeleteAsync(s.SessionId, cancellationToken);

            AnsiConsole.MarkupLine($"[green]✓ Pruned {orphaned.Count} orphaned session(s).[/]");
            return 0;
        }

        // Cleanup mode — age-based deletion of checkpoints + local session directories
        if (settings.Cleanup)
        {
            var age    = ParseAge(settings.OlderThan);
            var cutoff = DateTime.UtcNow - age;
            var all    = await sessionStore.ListIndexAsync(cancellationToken);

            IEnumerable<Core.Models.SessionIndexEntry> candidates = all
                .Where(s => s.LastUpdatedAt < cutoff);

            if (!string.IsNullOrWhiteSpace(settings.Project))
                candidates = candidates.Where(s => s.WorkingDirectory is { } wd &&
                    wd.Contains(settings.Project, StringComparison.OrdinalIgnoreCase));

            var toDelete = candidates.ToList();

            if (toDelete.Count == 0)
            {
                AnsiConsole.MarkupLine($"[green]✓ No sessions older than {FormatAge(age)} found.[/]");
                return 0;
            }

            int localDirsRemoved = 0;
            foreach (var s in toDelete)
            {
                await sessionStore.DeleteAsync(s.SessionId, cancellationToken);

                if (s.WorkingDirectory is { Length: > 0 })
                {
                    var localDir = Path.Combine(s.WorkingDirectory, FuseraftPaths.LocalSessions, s.SessionId);
                    if (Directory.Exists(localDir))
                    {
                        Directory.Delete(localDir, recursive: true);
                        localDirsRemoved++;
                    }
                }
            }

            AnsiConsole.MarkupLine(
                $"[green]✓ Deleted {toDelete.Count} session(s) older than {FormatAge(age)}" +
                (localDirsRemoved > 0 ? $" ({localDirsRemoved} local director{(localDirsRemoved == 1 ? "y" : "ies")} removed)" : string.Empty) +
                ".[/]");
            return 0;
        }

        // Delete mode
        if (settings.Delete is { } target)
        {
            if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var all = await sessionStore.ListIndexAsync(cancellationToken);
                var completed = all.Where(s => s.IsComplete).ToList();

                if (completed.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No completed sessions to delete.[/]");
                    return 0;
                }

                foreach (var s in completed)
                    await sessionStore.DeleteAsync(s.SessionId, cancellationToken);

                AnsiConsole.MarkupLine($"[green]✓ Deleted {completed.Count} completed session(s).[/]");
                return 0;
            }

            var checkpoint = await sessionStore.LoadAsync(target, cancellationToken);
            if (checkpoint is null)
            {
                AnsiConsole.MarkupLine($"[red]✗ Session not found:[/] {Markup.Escape(target)}");
                return 1;
            }

            await sessionStore.DeleteAsync(target, cancellationToken);
            AnsiConsole.MarkupLine($"[green]✓ Deleted session {Markup.Escape(target)}.[/]");
            return 0;
        }

        // List mode — uses the lightweight index; no message history loaded.
        var sessions = await sessionStore.ListIndexAsync(cancellationToken);

        IEnumerable<Core.Models.SessionIndexEntry> visible = settings.All
            ? sessions
            : sessions.Where(s => !s.IsComplete);

        if (!string.IsNullOrWhiteSpace(settings.Project))
            visible = visible.Where(s => s.WorkingDirectory is { } wd &&
                wd.Contains(settings.Project, StringComparison.OrdinalIgnoreCase));

        var list = visible.ToList();

        if (list.Count == 0)
        {
            AnsiConsole.MarkupLine(settings.All
                ? "[dim]No sessions found.[/]"
                : "[dim]No incomplete sessions found. Use [bold]--all[/] to see completed sessions.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Session ID[/]")
            .AddColumn("[bold]Status[/]")
            .AddColumn("[bold]Turns[/]")
            .AddColumn("[bold]Project[/]")
            .AddColumn("[bold]Started[/]")
            .AddColumn("[bold]Last Updated[/]")
            .AddColumn("[bold]Task[/]");

        foreach (var s in list)
        {
            var status = s.IsComplete
                ? "[green]complete[/]"
                : "[yellow]incomplete[/]";

            var project = ProjectLabel(s.WorkingDirectory);

            table.AddRow(
                $"[bold]{s.SessionId}[/]",
                status,
                s.TurnCount.ToString(),
                Markup.Escape(project),
                s.StartedAt.ToString("yyyy-MM-dd HH:mm"),
                s.LastUpdatedAt.ToString("yyyy-MM-dd HH:mm"),
                Markup.Escape(StringHelpers.Truncate(s.Task, 55)));
        }

        AnsiConsole.Write(table);

        if (!settings.All)
            AnsiConsole.MarkupLine(
                $"[dim]{list.Count} incomplete session(s). " +
                $"Resume with: [bold]fuseraft run --resume <id>[/][/]");

        return 0;
    }

    private static TimeSpan ParseAge(string? age)
    {
        if (string.IsNullOrWhiteSpace(age)) return TimeSpan.FromDays(30);
        var s = age.Trim().ToLowerInvariant();
        if (s.EndsWith('w') && int.TryParse(s[..^1], out var weeks)) return TimeSpan.FromDays(weeks * 7);
        if (s.EndsWith('d') && int.TryParse(s[..^1], out var days))  return TimeSpan.FromDays(days);
        if (s.EndsWith('h') && int.TryParse(s[..^1], out var hours)) return TimeSpan.FromHours(hours);
        if (int.TryParse(s, out var n)) return TimeSpan.FromDays(n);
        return TimeSpan.FromDays(30);
    }

    private static string FormatAge(TimeSpan age) =>
        age.TotalDays >= 1 ? $"{(int)age.TotalDays}d" : $"{(int)age.TotalHours}h";

    /// <summary>Returns the last two path components, e.g. "fuseraft/brewer".</summary>
    private static string ProjectLabel(string? workingDir)
    {
        if (string.IsNullOrEmpty(workingDir)) return "—";
        var parts = workingDir.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            ? string.Join("/", parts[^2..])
            : parts[^1];
    }
}
