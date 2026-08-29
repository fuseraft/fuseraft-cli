using System.ComponentModel;
using Microsoft.Agents.AI;
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
/// shipping a skill. The pass/fail verdict comes from <see cref="AgentFileSkillsSource"/> — the
/// same discovery pipeline the REPL and orchestration both use at runtime, so a skill that
/// passes here is guaranteed to load identically in both. Per-failure reasons come from
/// <see cref="AgentSkillFrontmatter"/>'s own validating constructor, fed by the minimal raw
/// <c>name:</c>/<c>description:</c>/<c>compatibility:</c> extraction in
/// <see cref="FrontmatterFieldReader"/> — nothing here re-implements the specification's rules.
/// </summary>
public sealed class SkillsValidateCommand : AsyncCommand<SkillsValidateSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, SkillsValidateSettings settings, CancellationToken cancellationToken)
    {
        string searchRoot;
        List<string> candidateDirs;

        if (!string.IsNullOrWhiteSpace(settings.Path))
        {
            var dir = FuseraftPaths.ExpandPath(settings.Path);
            if (!Directory.Exists(dir))
            {
                AnsiConsole.MarkupLine($"[red]✗ Not a directory: {Markup.Escape(settings.Path)}[/]");
                return 1;
            }
            searchRoot   = dir;
            candidateDirs = [Normalize(dir)];
        }
        else
        {
            searchRoot = FuseraftPaths.GlobalSkills;
            if (!Directory.Exists(searchRoot))
            {
                AnsiConsole.MarkupLine("[dim]No skills installed. Use [bold]fuseraft skills add <path>[/] to add one.[/]");
                return 0;
            }
            candidateDirs = [.. Directory.EnumerateDirectories(searchRoot).Select(Normalize).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)];
        }

        var source = new AgentFileSkillsSource(searchRoot, FuseraftSkillsSources.RunScriptAsync);
        var passed = await source.GetSkillsAsync(new AgentSkillsSourceContext(SkillDiscoveryAgent.Create(), session: null), cancellationToken);
        var passedByDir = passed
            .OfType<AgentFileSkill>()
            .ToDictionary(s => Normalize(s.Path), s => s, StringComparer.OrdinalIgnoreCase);

        var allValid = true;
        foreach (var dir in candidateDirs)
        {
            var name    = Path.GetFileName(dir);
            var skillMd = Path.Combine(dir, "SKILL.md");

            if (!File.Exists(skillMd))
            {
                allValid = false;
                AnsiConsole.MarkupLine($"[red]✗[/] [bold]{Markup.Escape(name)}[/] — no SKILL.md found");
                continue;
            }

            if (passedByDir.ContainsKey(dir))
            {
                AnsiConsole.MarkupLine($"[green]✓[/] [bold]{Markup.Escape(name)}[/]");
                continue;
            }

            allValid = false;
            AnsiConsole.MarkupLine($"[red]✗[/] [bold]{Markup.Escape(name)}[/]");
            foreach (var violation in await DescribeViolationsAsync(skillMd, name, cancellationToken))
                AnsiConsole.MarkupLine($"    [red]•[/] {Markup.Escape(violation)}");
        }

        if (!allValid)
            AnsiConsole.MarkupLine(
                "\n[yellow]A skill listed above works fine in the REPL's lenient loader but is silently dropped " +
                "by 'fuseraft run' orchestration sessions, which require full spec conformance.[/]");

        return allValid ? 0 : 1;
    }

    /// <summary>
    /// Explains why a skill directory that <see cref="AgentFileSkillsSource"/> silently dropped
    /// failed, by handing the same raw <c>name:</c>/<c>description:</c>/<c>compatibility:</c>
    /// values to <see cref="AgentSkillFrontmatter"/>'s own validating constructor and reporting
    /// its exception message (or a name/directory mismatch, the one thing that constructor
    /// doesn't check since it has no notion of a directory).
    /// </summary>
    private static async Task<IReadOnlyList<string>> DescribeViolationsAsync(string skillMdPath, string directoryName, CancellationToken cancellationToken)
    {
        var content       = await File.ReadAllTextAsync(skillMdPath, cancellationToken);
        var rawName       = FrontmatterFieldReader.ExtractField(content, "name");
        var rawDescription = FrontmatterFieldReader.ExtractField(content, "description");
        var rawCompatibility = FrontmatterFieldReader.ExtractField(content, "compatibility");

        if (rawName is null && rawDescription is null)
            return ["No YAML frontmatter block found (SKILL.md must start with a '---' delimited block with 'name:' and 'description:' fields)."];

        var violations = new List<string>();
        AgentSkillFrontmatter? frontmatter = null;
        try
        {
            frontmatter = new AgentSkillFrontmatter(rawName ?? string.Empty, rawDescription ?? string.Empty, rawCompatibility);
        }
        catch (ArgumentException ex)
        {
            violations.Add(ex.Message);
        }

        if (frontmatter is not null && !string.Equals(frontmatter.Name, directoryName, StringComparison.Ordinal))
            violations.Add($"'name: {frontmatter.Name}' does not match its directory name '{directoryName}'.");

        return violations.Count > 0 ? violations : ["Does not conform to the Agent Skills specification (reason unknown — check for stray YAML syntax)."];
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
