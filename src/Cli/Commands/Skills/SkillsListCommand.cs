using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;

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

        var entries = new List<(string Slug, string Description)>();
        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d))
        {
            var mdPath = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(mdPath)) continue;
            var content = await File.ReadAllTextAsync(mdPath, cancellationToken);
            var slug    = Path.GetFileName(dir);
            var desc    = SkillsHelpers.ExtractDescription(content);
            entries.Add((slug, desc));
        }

        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No skills installed. Use [bold]fuseraft skills add <path>[/] to add one.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn(new TableColumn("[bold]Slug[/]"))
            .AddColumn(new TableColumn("[bold]Description[/]"));

        foreach (var (slug, desc) in entries)
            table.AddRow(Markup.Escape(slug), Markup.Escape(desc));

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]{entries.Count} skill(s) in {Markup.Escape(root)}[/]");
        return 0;
    }
}
