using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace fuseraft.Cli.Commands;

public sealed class ShowConfigSettings : CommandSettings
{
    [CommandArgument(0, "[path]")]
    [Description("Config file to display. Defaults to .fuseraft/config/orchestration.yaml.")]
    public string Path { get; set; } = ".fuseraft/config/orchestration.yaml";

    [CommandOption("-l|--list")]
    [Description("List all config files (.json, .yaml, .yml) found under .fuseraft/config/.")]
    public bool List { get; set; }
}

/// <summary>
/// Renders a configuration file as rich tables.
/// </summary>
public sealed class ShowConfigCommand : Command<ShowConfigSettings>
{
    protected override int Execute(CommandContext context, ShowConfigSettings settings, CancellationToken cancellationToken)
    {
        if (settings.List)
            return ListConfigs();

        return ShowConfig(settings.Path);
    }

    // List mode

    private static int ListConfigs()
    {
        var configDir = ".fuseraft/config";
        if (!Directory.Exists(configDir))
        {
            AnsiConsole.MarkupLine("[yellow]No .fuseraft/config/ directory found.[/]");
            return 1;
        }

        var files = Directory.GetFiles(configDir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".json",  StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".yaml",  StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".yml",   StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
            .ToList();

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No config files in .fuseraft/config/.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title("[bold]Available Configs[/]")
            .AddColumn("[bold]File[/]")
            .AddColumn("[bold]Name[/]")
            .AddColumn("[bold]Agents[/]");

        foreach (var file in files)
        {
            try
            {
                var cfg = OrchestratorConfigLoader.LoadConfig(file);
                table.AddRow(
                    $"[dim]{Markup.Escape(file)}[/]",
                    Markup.Escape(cfg.Name),
                    string.Join(", ", cfg.Agents.Select(a => a.Name)));
            }
            catch (Exception ex)
            {
                table.AddRow($"[dim]{Markup.Escape(file)}[/]", $"[red]parse error: {Markup.Escape(ex.Message)}[/]", string.Empty);
            }
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[dim]Use [bold]fuseraft run --config <path>[/] to run a specific config.[/]");
        return 0;
    }

    // Detail mode

    private static int ShowConfig(string path)
    {
        OrchestrationConfig config;
        try
        {
            config = OrchestratorConfigLoader.LoadConfig(path);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        // Header
        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(config.Name)}[/]").LeftJustified());
        if (config.Description is not null)
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(config.Description)}[/]");
        AnsiConsole.WriteLine();

        // Agents table
        var agentTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title("[bold]Agents[/]")
            .AddColumn("[bold]Name[/]")
            .AddColumn("[bold]Model[/]")
            .AddColumn(new TableColumn("[bold]Temp[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]MaxTok[/]").RightAligned())
            .AddColumn("[bold]Plugins[/]");

        foreach (var agent in config.Agents)
        {
            var plugins = agent.Plugins.Count > 0
                ? string.Join(", ", agent.Plugins)
                : "[dim]—[/]";
            var maxTok = agent.Model.MaxTokens > 0
                ? agent.Model.MaxTokens.ToString()
                : "[dim]default[/]";

            agentTable.AddRow(
                $"[bold]{Markup.Escape(agent.Name)}[/]",
                $"[dim]{Markup.Escape(agent.Model.ModelId)}[/]",
                $"{agent.Model.Temperature:0.00}",
                maxTok,
                plugins);
        }

        AnsiConsole.Write(agentTable);
        AnsiConsole.WriteLine();

        // Strategy panel
        var selLine = $"[bold]Selection:[/]  {Markup.Escape(config.Selection.Type)}";
        var termLine = $"[bold]Termination:[/] {(config.Termination is not null ? DescribeTermination(config.Termination) : "default")}";

        AnsiConsole.Write(new Panel($"{selLine}\n{termLine}")
        {
            Header  = new PanelHeader(" Execution Settings ", Justify.Left),
            Border  = BoxBorder.Rounded,
            Padding = new Padding(1, 0)
        });

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]{path}[/]");
        return 0;
    }

    private static string DescribeTermination(TerminationStrategyConfig t)
    {
        var type = t.Type.ToLowerInvariant();
        var agents = t.AgentNames is { Length: > 0 }
            ? $" [dim](agents: {string.Join(", ", t.AgentNames)})[/]"
            : string.Empty;

        return type switch
        {
            "regex"         => $"regex [aqua]{Markup.Escape(t.Pattern ?? "?")}[/]{agents}  max={t.MaxIterations}",
            "structured"    => $"structured [aqua]{Markup.Escape(DescribeCondition(t.Condition))}[/]{agents}  max={t.MaxIterations}",
            "tokenbudget"   => $"tokenbudget [aqua]{t.MaxTokens} tokens[/]  max={t.MaxIterations}",
            "maxiterations" => $"max {t.MaxIterations} turns",
            "composite"     => $"composite ({t.Strategies?.Count ?? 0} rules)  max={t.MaxIterations}",
            _               => Markup.Escape(t.Type)
        };
    }

    private static string DescribeCondition(StructuredCondition? c)
    {
        if (c is null) return "?";
        if (c.Is is not null)       return $"{c.Field} == {c.Is}";
        if (c.IsNot is not null)    return $"{c.Field} != {c.IsNot}";
        if (c.Contains is not null) return $"{c.Field} contains {c.Contains}";
        if (c.Exists is not null)   return $"{c.Field} {(c.Exists.Value ? "exists" : "absent")}";
        return c.Field;
    }
}
