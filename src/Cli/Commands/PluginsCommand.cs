using System.ComponentModel;
using Microsoft.Extensions.AI;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Cli.Commands;

public sealed class PluginsSettings : CommandSettings
{
    [CommandOption("-p|--plugin")]
    [Description("Show details for a single plugin. Omit to list all.")]
    public string? PluginName { get; set; }
}

/// <summary>
/// Lists all registered plugins and their functions.
/// </summary>
public sealed class PluginsCommand(PluginRegistry registry) : Command<PluginsSettings>
{
    protected override int Execute(CommandContext context, PluginsSettings settings, CancellationToken cancellationToken)
    {
        var names = registry.RegisteredPlugins.OrderBy(n => n).ToList();

        if (names.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No plugins registered.[/]");
            return 0;
        }

        // Filter to a single plugin if requested.
        if (settings.PluginName is { } filter)
        {
            names = names
                .Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (names.Count == 0)
            {
                AnsiConsole.MarkupLine($"[yellow]No plugin matching '[bold]{Markup.Escape(filter)}[/]'.[/]");
                return 1;
            }
        }

        foreach (var name in names)
            RenderPlugin(name);

        AnsiConsole.MarkupLine(
            $"[dim]{names.Count} plugin(s) · " +
            $"{names.Sum(n => CountFunctions(n))} function(s) total[/]");

        return 0;
    }

    private void RenderPlugin(string name)
    {
        // MCP-sourced AIFunction list
        if (registry.TryGetAIFunctions(name, out var aiFunctions))
        {
            AnsiConsole.MarkupLine($"[bold cyan]{Markup.Escape(name)}[/]  [dim]MCP[/]");

            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .AddColumn(new TableColumn("[bold]Function[/]").LeftAligned())
                .AddColumn(new TableColumn("[bold]Description[/]").LeftAligned());

            foreach (var fn in aiFunctions.OrderBy(f => f.Name))
            {
                table.AddRow(
                    $"[aqua]{Markup.Escape(fn.Name)}[/]",
                    $"[dim]{Markup.Escape(fn.Description ?? string.Empty)}[/]");
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            return;
        }

        // Built-in plugin (plain object(s) with [Description] attributes — usually one, but
        // "FileSystem" registers a second object for its directory/inspection tools).
        if (!registry.TryGetAll(name, out var plugins)) return;

        var functions = plugins.SelectMany(PluginRegistry.GetFunctionsFromObject);
        var typeNames = string.Join(" + ", plugins.Select(p => p.GetType().Name));

        AnsiConsole.MarkupLine($"[bold cyan]{Markup.Escape(name)}[/]  [dim]{Markup.Escape(typeNames)}[/]");

        var builtInTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]Function[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Description[/]").LeftAligned());

        foreach (var fn in functions)
        {
            builtInTable.AddRow(
                $"[aqua]{Markup.Escape(fn.Name)}[/]",
                $"[dim]{Markup.Escape(fn.Description ?? string.Empty)}[/]");
        }

        AnsiConsole.Write(builtInTable);
        AnsiConsole.WriteLine();
    }

    private int CountFunctions(string name)
    {
        if (registry.TryGetAIFunctions(name, out var aiFunctions))
            return aiFunctions.Count;
        if (!registry.TryGetAll(name, out var plugins)) return 0;
        return plugins.Sum(p => PluginRegistry.GetFunctionsFromObject(p).Count);
    }
}
