using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Commands.Skills;

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
        try
        {
            await index.RemoveAsync(slug, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("e_sqlite3") || ex.Message.Contains("SQLite"))
        {
            // Skill directory already deleted; index cleanup is best-effort.
            AnsiConsole.MarkupLine($"[yellow]⚠[/] Skill files removed but index update failed: {Markup.Escape(ex.Message)}");
        }

        AnsiConsole.MarkupLine($"[green]✓[/] Removed [bold]{Markup.Escape(slug)}[/].");
        return 0;
    }
}
