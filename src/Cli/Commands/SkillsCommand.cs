using System.ComponentModel;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Commands;

// fuseraft skills add <source>

public sealed class SkillsAddSettings : CommandSettings
{
    [CommandArgument(0, "<source>")]
    [Description("Path to a skill directory (containing SKILL.md) or directly to a SKILL.md file.")]
    public string Source { get; set; } = string.Empty;
}

public sealed class SkillsAddCommand : AsyncCommand<SkillsAddSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, SkillsAddSettings settings, CancellationToken cancellationToken)
    {
        var sourcePath = FuseraftPaths.ExpandPath(settings.Source);

        string skillMdPath;
        if (File.Exists(sourcePath) && Path.GetFileName(sourcePath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            skillMdPath = sourcePath;
        else if (Directory.Exists(sourcePath))
        {
            skillMdPath = Path.Combine(sourcePath, "SKILL.md");
            if (!File.Exists(skillMdPath))
            {
                AnsiConsole.MarkupLine($"[red]✗ No SKILL.md found in {Markup.Escape(sourcePath)}[/]");
                return 1;
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗ Path not found: {Markup.Escape(settings.Source)}[/]");
            return 1;
        }

        var content = await File.ReadAllTextAsync(skillMdPath, cancellationToken);
        var slug    = SkillsHelpers.ExtractSlug(content)
                      ?? SkillsHelpers.ToSlug(Path.GetFileName(Path.GetDirectoryName(skillMdPath)) ?? "skill");

        if (string.IsNullOrWhiteSpace(slug))
        {
            AnsiConsole.MarkupLine("[red]✗ Could not derive a slug. Add a 'name:' field to the SKILL.md frontmatter.[/]");
            return 1;
        }

        var destDir  = Path.Combine(FuseraftPaths.GlobalSkills, slug);
        var destPath = Path.Combine(destDir, "SKILL.md");
        var isUpdate = File.Exists(destPath);

        Directory.CreateDirectory(destDir);
        await File.WriteAllTextAsync(destPath, content, cancellationToken);

        await using var index = new SkillIndex();
        await index.IndexAsync(slug, destPath, content, cancellationToken);

        var verb = isUpdate ? "Updated" : "Added";
        AnsiConsole.MarkupLine($"[green]✓[/] {verb} [bold]{Markup.Escape(slug)}[/] → {Markup.Escape(destPath)}");
        return 0;
    }
}

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

// fuseraft skills remove <slug>

public sealed class SkillsRemoveSettings : CommandSettings
{
    [CommandArgument(0, "<slug>")]
    [Description("Slug of the skill to remove (as shown by 'fuseraft skills list').")]
    public string Slug { get; set; } = string.Empty;
}

public sealed class SkillsRemoveCommand : AsyncCommand<SkillsRemoveSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, SkillsRemoveSettings settings, CancellationToken cancellationToken)
    {
        var slug    = settings.Slug.Trim();
        var destDir = Path.Combine(FuseraftPaths.GlobalSkills, slug);

        if (!Directory.Exists(destDir))
        {
            AnsiConsole.MarkupLine(
                $"[red]✗ Skill '{Markup.Escape(slug)}' not found.[/] " +
                $"Run [bold]fuseraft skills list[/] to see installed skills.");
            return 1;
        }

        Directory.Delete(destDir, recursive: true);

        await using var index = new SkillIndex();
        await index.RemoveAsync(slug, cancellationToken);

        AnsiConsole.MarkupLine($"[green]✓[/] Removed [bold]{Markup.Escape(slug)}[/].");
        return 0;
    }
}

// Shared helpers

file static class SkillsHelpers
{
    private static readonly Regex NameFrontmatter =
        new(@"^name:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static readonly Regex DescriptionFrontmatter =
        new(@"^description:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    internal static string? ExtractSlug(string content)
    {
        var m = NameFrontmatter.Match(content);
        if (!m.Success) return null;
        var name = m.Groups[1].Value.Trim().Trim('"').Trim('\'');
        return string.IsNullOrWhiteSpace(name) ? null : ToSlug(name);
    }

    internal static string ExtractDescription(string content)
    {
        var m = DescriptionFrontmatter.Match(content);
        if (!m.Success) return string.Empty;
        return m.Groups[1].Value.Trim().Trim('"').Trim('\'');
    }

    internal static string ToSlug(string name) =>
        Regex.Replace(name.ToLowerInvariant().Trim(), @"[^a-z0-9]+", "-").Trim('-');
}
