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
}

/// <summary>
/// Lists and manages persisted session checkpoints.
/// </summary>
public sealed class SessionsCommand(ISessionStore sessionStore) : AsyncCommand<SessionsSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, SessionsSettings settings, CancellationToken cancellationToken)
    {
        // Delete mode
        if (settings.Delete is { } target)
        {
            if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var all = await sessionStore.ListAsync();
                var completed = all.Where(s => s.IsComplete).ToList();

                if (completed.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No completed sessions to delete.[/]");
                    return 0;
                }

                foreach (var s in completed)
                    await sessionStore.DeleteAsync(s.SessionId);

                AnsiConsole.MarkupLine($"[green]✓ Deleted {completed.Count} completed session(s).[/]");
                return 0;
            }

            var checkpoint = await sessionStore.LoadAsync(target);
            if (checkpoint is null)
            {
                AnsiConsole.MarkupLine($"[red]✗ Session not found:[/] {Markup.Escape(target)}");
                return 1;
            }

            await sessionStore.DeleteAsync(target);
            AnsiConsole.MarkupLine($"[green]✓ Deleted session {Markup.Escape(target)}.[/]");
            return 0;
        }

        // List mode
        var sessions = await sessionStore.ListAsync();
        var visible = settings.All ? sessions : sessions.Where(s => !s.IsComplete).ToList();

        if (visible.Count == 0)
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
            .AddColumn("[bold]Started[/]")
            .AddColumn("[bold]Last Updated[/]")
            .AddColumn("[bold]Task[/]");

        foreach (var s in visible)
        {
            var status = s.IsComplete
                ? "[green]complete[/]"
                : "[yellow]incomplete[/]";

            table.AddRow(
                $"[bold]{s.SessionId}[/]",
                status,
                s.Messages.Count.ToString(),
                s.StartedAt.ToString("yyyy-MM-dd HH:mm"),
                s.LastUpdatedAt.ToString("yyyy-MM-dd HH:mm"),
                Markup.Escape(StringHelpers.Truncate(s.Task, 55)));
        }

        AnsiConsole.Write(table);

        if (!settings.All)
            AnsiConsole.MarkupLine(
                $"[dim]{visible.Count} incomplete session(s). " +
                $"Resume with: [bold]fuseraft run --resume <id>[/][/]");

        return 0;
    }
}
