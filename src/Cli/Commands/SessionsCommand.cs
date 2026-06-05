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
