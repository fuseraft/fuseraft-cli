using System.ComponentModel;
using Microsoft.Agents.AI;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;
using fuseraft.Core.Skills;
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

        // sourceSkillDir is non-null only when settings.Source names a skill directory
        // (as opposed to a bare SKILL.md path) — only then do we know every file under it
        // belongs to the skill and is safe to copy alongside SKILL.md (references/, scripts/).
        string  skillMdPath;
        string? sourceSkillDir = null;
        if (File.Exists(sourcePath) && Path.GetFileName(sourcePath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            skillMdPath = sourcePath;
        else if (Directory.Exists(sourcePath))
        {
            skillMdPath    = Path.Combine(sourcePath, "SKILL.md");
            sourceSkillDir = sourcePath;
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

        // Guarantee the installed file's 'name:' field matches the directory it's installed
        // under — a raw name that needed slugifying (spaces, uppercase, ...) would otherwise
        // leave the two disagreeing, which works fine in the REPL's lenient loader but is
        // silently dropped by fuseraft's orchestration skills provider.
        content = SkillsHelpers.CanonicalizeName(content, slug);

        var destDir  = Path.Combine(FuseraftPaths.GlobalSkills, slug);
        var destPath = Path.Combine(destDir, "SKILL.md");
        var isUpdate = File.Exists(destPath);

        Directory.CreateDirectory(destDir);
        if (sourceSkillDir is not null)
        {
            // Copy the whole skill directory (SKILL.md plus references/, scripts/, and any
            // other bundled files) — copying SKILL.md alone silently strips everything a
            // skill's own instructions point to (load_skill/read_skill_resource/run_skill_script).
            SkillsHelpers.CopySkillDirectory(sourceSkillDir, destDir);
        }
        // Write (or overwrite, if just copied) SKILL.md with the possibly name-canonicalized content.
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

        // Canonicalizing the name only guarantees name-matches-directory; description/
        // compatibility could still be missing or too long. Confirm with the same
        // AgentFileSkillsSource pipeline orchestration and the REPL actually use, rather than
        // re-deriving the answer here.
        var checkSource = new AgentFileSkillsSource(destDir, FuseraftSkillsSources.RunScriptAsync);
        var checkResult = await checkSource.GetSkillsAsync(
            new AgentSkillsSourceContext(SkillDiscoveryAgent.Create(), session: null), cancellationToken);
        if (checkResult.Count == 0)
            AnsiConsole.MarkupLine(
                $"[yellow]⚠[/] '{Markup.Escape(slug)}' does not fully conform to the Agent Skills specification " +
                $"(name matches its directory, but check description/compatibility). It will work in the REPL but " +
                $"'fuseraft run' orchestration sessions will silently drop it — run [bold]fuseraft skills validate {Markup.Escape(slug)}[/] for details.");

        return 0;
    }
}
