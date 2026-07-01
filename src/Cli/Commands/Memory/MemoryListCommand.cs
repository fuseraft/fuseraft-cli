using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Infrastructure.Memory;

namespace fuseraft.Cli.Commands.Memory;

// fuseraft memory list
// fuseraft memory list --agent <agent>

public sealed class MemoryListSettings : CommandSettings
{
    [CommandOption("--agent <agent>")]
    [Description("Target the named agent's memory store (~/.fuseraft/memory/agents/<agent>) instead of the REPL memory store.")]
    public string? Agent { get; init; }
}

public sealed class MemoryListCommand : AsyncCommand<MemoryListSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        MemoryListSettings settings,
        CancellationToken cancellationToken)
    {
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

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn(new TableColumn("[bold]Name[/]"))
            .AddColumn(new TableColumn("[bold]Type[/]"))
            .AddColumn(new TableColumn("[bold]Description[/]"));

        foreach (var entry in entries.OrderBy(e => e.Type).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            table.AddRow(Markup.Escape(entry.Name), Markup.Escape(entry.Type), Markup.Escape(entry.Description));

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]{entries.Count} memor{(entries.Count == 1 ? "y" : "ies")} for {label}.[/]");
        return 0;
    }
}
