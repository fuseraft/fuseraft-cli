using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;
using fuseraft.Core.Skills;

namespace fuseraft.Cli.Commands.Skills;

// fuseraft skills validate [path]

public sealed class SkillsValidateSettings : CommandSettings
{
    [CommandArgument(0, "[path]")]
    [Description("Path to a skill directory to validate. Omit to validate every skill in ~/.fuseraft/skills.")]
    public string? Path { get; set; }
}

/// <summary>
/// fuseraft's equivalent of the <c>skills-ref validate</c> tool the Agent Skills specification
/// (<see href="https://agentskills.io/specification#validation"/>) recommends authors run before
/// shipping a skill — checks a SKILL.md's frontmatter against every naming and field-length rule
/// the spec defines, using the exact same validator fuseraft's orchestration skills provider
/// applies at load time.
/// </summary>
public sealed class SkillsValidateCommand : AsyncCommand<SkillsValidateSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, SkillsValidateSettings settings, CancellationToken cancellationToken)
    {
        List<string> skillDirs;

        if (!string.IsNullOrWhiteSpace(settings.Path))
        {
            var dir = FuseraftPaths.ExpandPath(settings.Path);
            if (!Directory.Exists(dir))
            {
                AnsiConsole.MarkupLine($"[red]✗ Not a directory: {Markup.Escape(settings.Path)}[/]");
                return 1;
            }
            skillDirs = [dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)];
        }
        else
        {
            var root = FuseraftPaths.GlobalSkills;
            if (!Directory.Exists(root))
            {
                AnsiConsole.MarkupLine("[dim]No skills installed. Use [bold]fuseraft skills add <path>[/] to add one.[/]");
                return 0;
            }
            skillDirs = Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        }

        var allValid = true;
        foreach (var dir in skillDirs)
        {
            var name    = Path.GetFileName(dir);
            var skillMd = Path.Combine(dir, "SKILL.md");

            if (!File.Exists(skillMd))
            {
                allValid = false;
                AnsiConsole.MarkupLine($"[red]✗[/] [bold]{Markup.Escape(name)}[/] — no SKILL.md found");
                continue;
            }

            var content     = await File.ReadAllTextAsync(skillMd, cancellationToken);
            var frontmatter = SkillFrontmatterSpec.TryParse(content);
            var violations  = SkillFrontmatterSpec.Validate(frontmatter, name);

            if (violations.Count == 0)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] [bold]{Markup.Escape(name)}[/]");
                continue;
            }

            allValid = false;
            AnsiConsole.MarkupLine($"[red]✗[/] [bold]{Markup.Escape(name)}[/]");
            foreach (var violation in violations)
                AnsiConsole.MarkupLine($"    [red]•[/] {Markup.Escape(violation)}");
        }

        if (!allValid)
            AnsiConsole.MarkupLine(
                "\n[yellow]A skill listed above works fine in the REPL's lenient loader but is silently dropped " +
                "by 'fuseraft run' orchestration sessions, which require full spec conformance.[/]");

        return allValid ? 0 : 1;
    }
}
