using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Infrastructure.Memory;

namespace fuseraft.Cli.Commands.Memory;

// fuseraft memory delete <name>
// fuseraft memory delete --all

public sealed class MemoryDeleteSettings : CommandSettings
{
    [CommandArgument(0, "[name]")]
    [Description("Name of the memory to delete (as shown by '/memory' in the REPL).")]
    public string? Name { get; init; }

    [CommandOption("--all")]
    [Description("Delete every stored memory instead of a single named entry.")]
    public bool All { get; init; }

    [CommandOption("--agent <agent>")]
    [Description("Target the named agent's memory store (~/.fuseraft/memory/agents/<agent>) instead of the REPL memory store.")]
    public string? Agent { get; init; }

    [CommandOption("-y|--yes")]
    [Description("Skip the confirmation prompt when using --all.")]
    public bool Yes { get; init; }
}

public sealed class MemoryDeleteCommand : AsyncCommand<MemoryDeleteSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        MemoryDeleteSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.All && !string.IsNullOrEmpty(settings.Name))
        {
            AnsiConsole.MarkupLine("[red]✗ Specify either <name> or --all, not both.[/]");
            return 1;
        }

        if (!settings.All && string.IsNullOrEmpty(settings.Name))
        {
            AnsiConsole.MarkupLine("[yellow]Usage: fuseraft memory delete <name>[/]");
            AnsiConsole.MarkupLine("[yellow]       fuseraft memory delete --all[/]");
            return 1;
        }

        var store = string.IsNullOrEmpty(settings.Agent)
            ? MemoryStore.ForRepl()
            : MemoryStore.ForAgent(settings.Agent);
        var label = string.IsNullOrEmpty(settings.Agent) ? "REPL" : $"agent '{settings.Agent}'";

        var entries = await store.LoadAllAsync(cancellationToken);
        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine($"[dim]No memories stored for {label}.[/]");
            return 0;
        }

        if (settings.All)
        {
            if (!settings.Yes)
            {
                if (Console.IsInputRedirected)
                {
                    AnsiConsole.MarkupLine("[red]✗ Refusing to wipe memory in a non-interactive session without --yes.[/]");
                    return 1;
                }

                if (!AnsiConsole.Confirm(
                        $"[yellow]Delete all {entries.Count} {label} memor{(entries.Count == 1 ? "y" : "ies")}? This cannot be undone.[/]",
                        false))
                {
                    AnsiConsole.MarkupLine("[dim]Aborted.[/]");
                    return 0;
                }
            }

            var deletedCount = 0;
            foreach (var entry in entries)
            {
                if (await store.DeleteAsync(entry.Name, ct: cancellationToken))
                    deletedCount++;
            }

            AnsiConsole.MarkupLine(
                $"[green]✓[/] Deleted [bold]{deletedCount}[/] {label} memor{(deletedCount == 1 ? "y" : "ies")}.");
            return 0;
        }

        var deleted = await store.DeleteAsync(settings.Name!, ct: cancellationToken);
        if (deleted)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Deleted memory [bold]{Markup.Escape(settings.Name!)}[/] ({label}).");
            return 0;
        }

        AnsiConsole.MarkupLine($"[red]✗ No memory named '{Markup.Escape(settings.Name!)}' in {label}.[/]");
        var names = entries.Select(e => e.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        AnsiConsole.MarkupLine($"[dim]Available: {Markup.Escape(string.Join(", ", names))}[/]");
        return 1;
    }
}
