using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Commands.Skills;

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
        try
        {
            await index.IndexAsync(slug, destPath, content, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("e_sqlite3") || ex.Message.Contains("SQLite"))
        {
            AnsiConsole.MarkupLine($"[red]✗ Skill index unavailable:[/] {Markup.Escape(ex.Message)}");
            // The skill file was already written; report partial success so the user isn't blocked.
            var verb2 = isUpdate ? "Updated" : "Added";
            AnsiConsole.MarkupLine($"[green]✓[/] {verb2} [bold]{Markup.Escape(slug)}[/] → {Markup.Escape(destPath)} [dim](index skipped)[/]");
            return 0;
        }

        var verb = isUpdate ? "Updated" : "Added";
        AnsiConsole.MarkupLine($"[green]✓[/] {verb} [bold]{Markup.Escape(slug)}[/] → {Markup.Escape(destPath)}");
        return 0;
    }
}
