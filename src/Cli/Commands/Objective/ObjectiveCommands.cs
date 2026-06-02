using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;
using fuseraft.Infrastructure;

namespace fuseraft.Cli.Commands.Objective;

// ── fuseraft objective create ────────────────────────────────────────────────

public sealed class ObjectiveCreateSettings : CommandSettings
{
    [CommandOption("--title|-t <title>")]
    [Description("Short title for the objective.")]
    public string? Title { get; init; }

    [CommandOption("--description|-d <desc>")]
    [Description("What this objective achieves and why it matters.")]
    public string Description { get; init; } = "";

    [CommandOption("--tasks <tasks>")]
    [Description("Comma-separated list of initial remaining tasks.")]
    public string? Tasks { get; init; }
}

public sealed class ObjectiveCreateCommand : AsyncCommand<ObjectiveCreateSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        ObjectiveCreateSettings settings,
        CancellationToken cancellationToken)
    {
        var title = settings.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = AnsiConsole.Ask<string>("[bold]Title:[/]");
            if (string.IsNullOrWhiteSpace(title))
            {
                AnsiConsole.MarkupLine("[red]Title is required.[/]");
                return 1;
            }
        }

        var tasks = string.IsNullOrWhiteSpace(settings.Tasks)
            ? null
            : settings.Tasks.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0);

        var store   = new ObjectiveStore(FuseraftPaths.LocalObjectives);
        var manager = new ObjectiveManager(store);
        var obj     = await manager.CreateAsync(title, settings.Description, tasks, cancellationToken);

        AnsiConsole.MarkupLine($"[green]Created[/] [bold]{Markup.Escape(obj.Id)}[/]: {Markup.Escape(obj.Title)}");
        return 0;
    }
}

// ── fuseraft objective list ──────────────────────────────────────────────────

public sealed class ObjectiveListSettings : CommandSettings
{
    [CommandOption("--status|-s <status>")]
    [Description("Filter by status: Active, Paused, Completed, Abandoned.")]
    public string? Status { get; init; }

    [CommandOption("--all|-a")]
    [Description("Show all objectives regardless of status (same as omitting --status).")]
    public bool All { get; init; }
}

public sealed class ObjectiveListCommand : AsyncCommand<ObjectiveListSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        ObjectiveListSettings settings,
        CancellationToken cancellationToken)
    {
        var store   = new ObjectiveStore(FuseraftPaths.LocalObjectives);
        var manager = new ObjectiveManager(store);
        var all     = await manager.ListAllAsync(cancellationToken);

        var filtered = settings.All || string.IsNullOrWhiteSpace(settings.Status)
            ? all
            : all.Where(o => o.Status.Equals(settings.Status, StringComparison.OrdinalIgnoreCase)).ToList();

        if (filtered.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No objectives found.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]ID[/]")
            .AddColumn("[bold]Title[/]")
            .AddColumn("[bold]Status[/]")
            .AddColumn("[bold]Progress[/]");

        foreach (var o in filtered)
        {
            var total = o.CompletedTasks.Count + o.RemainingTasks.Count;
            var prog  = total > 0 ? $"{o.PercentComplete:F0}% ({o.CompletedTasks.Count}/{total})" : "—";
            var statusColor = o.Status switch
            {
                "Active"    => "green",
                "Paused"    => "yellow",
                "Completed" => "blue",
                _           => "grey"
            };
            table.AddRow(
                Markup.Escape(o.Id),
                Markup.Escape(o.Title),
                $"[{statusColor}]{Markup.Escape(o.Status)}[/]",
                Markup.Escape(prog));
        }

        AnsiConsole.Write(table);
        return 0;
    }
}

// ── fuseraft objective status ────────────────────────────────────────────────

public sealed class ObjectiveStatusSettings : CommandSettings
{
    [CommandArgument(0, "[id]")]
    [Description("Objective ID to inspect (e.g. OBJ-0001).")]
    public string? Id { get; init; }
}

public sealed class ObjectiveStatusCommand : AsyncCommand<ObjectiveStatusSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        ObjectiveStatusSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Id))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Provide an objective ID, e.g. [bold]fuseraft objective status OBJ-0001[/]");
            return 1;
        }

        var store   = new ObjectiveStore(FuseraftPaths.LocalObjectives);
        var manager = new ObjectiveManager(store);
        var obj     = await manager.GetAsync(settings.Id.Trim(), cancellationToken);

        if (obj is null)
        {
            AnsiConsole.MarkupLine($"[red]Not found:[/] No objective with ID '{Markup.Escape(settings.Id)}'.");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(obj.Id)}[/] — {Markup.Escape(obj.Title)}");
        AnsiConsole.MarkupLine($"Status: [bold]{Markup.Escape(obj.Status)}[/]");
        if (!string.IsNullOrWhiteSpace(obj.Description))
            AnsiConsole.MarkupLine($"Description: {Markup.Escape(obj.Description)}");

        var total = obj.CompletedTasks.Count + obj.RemainingTasks.Count;
        if (total > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"Progress: [bold]{obj.PercentComplete:F0}%[/] ({obj.CompletedTasks.Count}/{total} tasks)");

            if (obj.CompletedTasks.Count > 0)
            {
                AnsiConsole.MarkupLine("[green]Completed:[/]");
                foreach (var t in obj.CompletedTasks)
                    AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(t)}");
            }
            if (obj.RemainingTasks.Count > 0)
            {
                AnsiConsole.MarkupLine("[yellow]Remaining:[/]");
                foreach (var t in obj.RemainingTasks)
                    AnsiConsole.MarkupLine($"  • {Markup.Escape(t)}");
            }
        }

        if (obj.Sessions.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"Sessions: {Markup.Escape(string.Join(", ", obj.Sessions))}");
        }

        return 0;
    }
}
