using System.ComponentModel;
using Microsoft.Agents.AI;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;
using fuseraft.Core.Skills;

namespace fuseraft.Cli.Commands.Skills;

// fuseraft skills list

public sealed class SkillsListSettings : CommandSettings { }

public sealed class SkillsListCommand : AsyncCommand<SkillsListSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, SkillsListSettings settings, CancellationToken cancellationToken)
    {
        var root = FuseraftPaths.GlobalSkills;

        if (!Directory.Exists(root))
        {
            AnsiConsole.MarkupLine("[dim]No skills installed. Use [bold]fuseraft skills add <path>[/] to add one.[/]");
            return 0;
        }

        var dirs = Directory.EnumerateDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, "SKILL.md")))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (dirs.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No skills installed. Use [bold]fuseraft skills add <path>[/] to add one.[/]");
            return 0;
        }

        // The same discovery pipeline the REPL and orchestration use at runtime — a skill shown
        // here with a real description/compatibility is guaranteed to load identically in both.
        var source = new AgentFileSkillsSource(root, FuseraftSkillsSources.RunScriptAsync);
        var skills = await source.GetSkillsAsync(new AgentSkillsSourceContext(SkillDiscoveryAgent.Create(), session: null), cancellationToken);
        var bySlug = skills.ToDictionary(s => s.Frontmatter.Name, StringComparer.Ordinal);

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn(new TableColumn("[bold]Slug[/]"))
            .AddColumn(new TableColumn("[bold]Description[/]"))
            .AddColumn(new TableColumn("[bold]Requires[/]"))
            .AddColumn(new TableColumn("[bold]Spec[/]"));

        foreach (var dir in dirs)
        {
            var slug  = Path.GetFileName(dir);
            var valid = bySlug.TryGetValue(slug, out var skill);
            table.AddRow(
                Markup.Escape(slug),
                Markup.Escape(valid ? skill!.Frontmatter.Description : ""),
                Markup.Escape(valid ? skill!.Frontmatter.Compatibility ?? "" : ""),
                valid ? "[green]✓[/]" : "[red]✗[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]{dirs.Count} skill(s) in {Markup.Escape(root)}[/]");
        if (bySlug.Count < dirs.Count)
            AnsiConsole.MarkupLine("[dim]Run [bold]fuseraft skills validate[/] for details on the ✗ entries.[/]");
        return 0;
    }
}
