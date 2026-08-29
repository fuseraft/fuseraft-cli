using System.ComponentModel;
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

        var entries = new List<(string Slug, string Description, string? Compatibility, bool Valid)>();
        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d))
        {
            var mdPath = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(mdPath)) continue;
            var content     = await File.ReadAllTextAsync(mdPath, cancellationToken);
            var slug        = Path.GetFileName(dir);
            var frontmatter = SkillFrontmatterSpec.TryParse(content);
            var desc        = frontmatter?.Description ?? string.Empty;
            var valid       = SkillFrontmatterSpec.Validate(frontmatter, slug).Count == 0;
            entries.Add((slug, desc, frontmatter?.Compatibility, valid));
        }

        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No skills installed. Use [bold]fuseraft skills add <path>[/] to add one.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn(new TableColumn("[bold]Slug[/]"))
            .AddColumn(new TableColumn("[bold]Description[/]"))
            .AddColumn(new TableColumn("[bold]Requires[/]"))
            .AddColumn(new TableColumn("[bold]Spec[/]"));

        foreach (var (slug, desc, compatibility, valid) in entries)
        {
            var specMark = valid ? "[green]✓[/]" : "[red]✗[/]";
            table.AddRow(
                Markup.Escape(slug),
                Markup.Escape(desc),
                Markup.Escape(compatibility ?? ""),
                specMark);
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]{entries.Count} skill(s) in {Markup.Escape(root)}[/]");
        if (entries.Any(e => !e.Valid))
            AnsiConsole.MarkupLine("[dim]Run [bold]fuseraft skills validate[/] for details on the ✗ entries.[/]");
        return 0;
    }
}
