using System.Text;
using fuseraft.Core;
using fuseraft.Core.Skills;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>Full result of a skill-directory scan: the catalog plugin plus any diagnostics.</summary>
/// <param name="Plugin">The assembled <see cref="SkillsPlugin"/>, or <c>null</c> when no skills were found.</param>
/// <param name="CatalogBlock">Catalog text for the REPL system prompt, or <c>null</c> when no skills were found.</param>
/// <param name="Warnings">
/// Human-readable reasons a discovered <c>SKILL.md</c> was skipped — always because it declared
/// a <c>name:</c>, <c>description:</c>, or <c>compatibility:</c> field that violates the Agent
/// Skills specification (<see href="https://agentskills.io/specification"/>). A skill that omits
/// frontmatter entirely is never skipped — see the "leniency" note on <see cref="BuildSkills(IEnumerable{string})"/>.
/// </param>
internal sealed record SkillsLoadResult(
    SkillsPlugin?         Plugin,
    string?               CatalogBlock,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Scans skill directories, parses SKILL.md frontmatter, and assembles the
/// <see cref="SkillsPlugin"/> instance and catalog block injected into the REPL
/// system prompt at startup.
/// </summary>
internal static class ReplSkillsLoader
{
    // Matches orchestration's AgentFileSkillsSource search depth: root (0), skill dir (1),
    // an optional one level of vendor namespacing (2). Bounded so a search dir pointed at a
    // large or cyclic tree can't cause a runaway scan.
    private const int MaxSkillSearchDepth = 2;

    /// <summary>
    /// Returns the priority-ordered list of directories to scan for skills in a
    /// normal REPL session (project-local → user-global → install-bundled).
    /// </summary>
    internal static string[] GetDefaultSearchDirs()
    {
        var cwd  = Directory.GetCurrentDirectory();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            Path.Combine(cwd,  ".fuseraft", "skills"),
            Path.Combine(cwd,  ".agents",   "skills"),
            FuseraftPaths.GlobalSkills,
            Path.Combine(home, ".agents",   "skills"),
            Path.Combine(AppContext.BaseDirectory, "skills"),
        ];
    }

    /// <summary>
    /// Convenience overload used by <see cref="ReplCommand"/> — searches the default dirs.
    /// </summary>
    internal static (SkillsPlugin? Plugin, string? CatalogBlock) BuildSkills() =>
        BuildSkills(GetDefaultSearchDirs());

    /// <summary>
    /// Scans <paramref name="searchDirs"/> for <c>SKILL.md</c> files and returns a
    /// <see cref="SkillsPlugin"/> together with a catalog string suitable for appending to the
    /// REPL system prompt. Discards any skip warnings — see <see cref="BuildSkillsDetailed"/>
    /// for a caller that wants them.
    ///
    /// <para>
    /// <b>Leniency:</b> a <c>SKILL.md</c> with no frontmatter at all (or frontmatter with none
    /// of the recognized fields) is still loaded, using its directory name as the slug — this
    /// REPL surface does not require a <c>name:</c>/<c>description:</c> field the way fuseraft's
    /// orchestration skills provider does. But when a <c>name:</c>, <c>description:</c>, or
    /// <c>compatibility:</c> field <i>is</i> declared, it is validated against the same rules
    /// orchestration enforces, and a violation (most commonly <c>name:</c> not matching the
    /// directory name) skips the skill — silently accepting it here would let a skill work in
    /// the REPL while remaining invisible to <c>fuseraft run</c> orchestration sessions.
    /// </para>
    /// </summary>
    internal static (SkillsPlugin? Plugin, string? CatalogBlock) BuildSkills(IEnumerable<string> searchDirs)
    {
        var result = BuildSkillsDetailed(searchDirs);
        return (result.Plugin, result.CatalogBlock);
    }

    /// <summary>Same scan as <see cref="BuildSkills(IEnumerable{string})"/>, but also returns skip warnings.</summary>
    internal static SkillsLoadResult BuildSkillsDetailed(IEnumerable<string> searchDirs)
    {
        // slug → directory containing SKILL.md; first occurrence across searchDirs wins.
        var skillDirs     = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var descriptions  = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var compatibility = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var warnings      = new List<string>();

        foreach (var searchDir in searchDirs.Where(Directory.Exists))
        {
            List<string> skillMds;
            try
            {
                skillMds = FindSkillMdFiles(searchDir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException)                 { continue; }

            foreach (var skillMd in skillMds)
            {
                var skillDir = Path.GetDirectoryName(skillMd);
                if (skillDir is null) continue;
                var dirName = Path.GetFileName(skillDir);
                if (string.IsNullOrEmpty(dirName) || skillDirs.ContainsKey(dirName)) continue;

                string content;
                try
                {
                    content = File.ReadAllText(skillMd);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                if (!TryValidateFrontmatter(content, skillDir, dirName, warnings,
                        out var description, out var compat))
                    continue;

                skillDirs[dirName]     = skillDir;
                descriptions[dirName]  = description;
                compatibility[dirName] = compat;
            }
        }

        if (skillDirs.Count == 0) return new SkillsLoadResult(null, null, warnings);

        var sb = new StringBuilder();
        sb.AppendLine("## SKILLS available");
        foreach (var slug in skillDirs.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            var desc   = descriptions.GetValueOrDefault(slug);
            var compat = compatibility.GetValueOrDefault(slug);
            var line   = !string.IsNullOrWhiteSpace(desc) ? $"- {slug}: {desc}" : $"- {slug}";
            if (!string.IsNullOrWhiteSpace(compat))
                line += $" [requires: {compat}]";
            sb.AppendLine(line);
        }
        sb.AppendLine();
        sb.Append("Call load_skill(\"<slug>\") to get full step-by-step instructions before applying a skill.");

        return new SkillsLoadResult(new SkillsPlugin(skillDirs), sb.ToString(), warnings);
    }

    /// <summary>
    /// Parses <paramref name="content"/>'s frontmatter and validates any spec-covered field that
    /// is actually present. Returns <c>false</c> (and appends a warning) only when a declared
    /// field violates the spec — a skill with no frontmatter, or frontmatter missing these
    /// fields entirely, always passes (see the leniency note on <see cref="BuildSkills(IEnumerable{string})"/>).
    /// </summary>
    private static bool TryValidateFrontmatter(
        string content, string skillDir, string dirName, List<string> warnings,
        out string? description, out string? compatibility)
    {
        description   = null;
        compatibility = null;

        var fm = SkillFrontmatterSpec.TryParse(content);
        if (fm is null) return true;

        if (!string.IsNullOrEmpty(fm.Name))
        {
            if (!SkillFrontmatterSpec.ValidateName(fm.Name, out var nameReason))
            {
                warnings.Add($"Skipped skill at '{skillDir}': {nameReason}");
                return false;
            }
            if (!string.Equals(fm.Name, dirName, StringComparison.Ordinal))
            {
                warnings.Add(
                    $"Skipped skill at '{skillDir}': name '{fm.Name}' does not match its directory " +
                    $"name '{dirName}' (this skill would also be invisible to 'fuseraft run' orchestration sessions).");
                return false;
            }
        }

        if (!string.IsNullOrEmpty(fm.Description))
        {
            if (!SkillFrontmatterSpec.ValidateDescription(fm.Description, out var descReason))
            {
                warnings.Add($"Skipped skill at '{skillDir}': {descReason}");
                return false;
            }
            description = fm.Description;
        }

        if (!SkillFrontmatterSpec.ValidateCompatibility(fm.Compatibility, out var compatReason))
        {
            warnings.Add($"Skipped skill at '{skillDir}': {compatReason}");
            return false;
        }
        compatibility = fm.Compatibility;

        return true;
    }

    /// <summary>
    /// Finds every <c>SKILL.md</c> under <paramref name="root"/>, recursing at most
    /// <see cref="MaxSkillSearchDepth"/> levels and refusing to follow symlinked directories —
    /// unbounded, symlink-following recursion could otherwise be tricked (via a symlink planted
    /// in a project-controlled search dir) into scanning arbitrary parts of the filesystem, or
    /// hang on a symlink cycle. Once a directory yields a SKILL.md, its subdirectories are
    /// treated as part of that skill (references/scripts/assets), not as independent skill roots.
    /// </summary>
    private static List<string> FindSkillMdFiles(string root)
    {
        var results = new List<string>();
        FindSkillMdFiles(root, results, depth: 0);
        return results;
    }

    private static void FindSkillMdFiles(string directory, List<string> results, int depth)
    {
        var candidate = Path.Combine(directory, "SKILL.md");
        if (File.Exists(candidate))
        {
            if (!SkillPathGuard.IsReparsePoint(candidate))
                results.Add(candidate);
            return;
        }

        if (depth >= MaxSkillSearchDepth) return;

        IEnumerable<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; }

        foreach (var sub in subdirs)
        {
            if (SkillPathGuard.IsReparsePoint(sub)) continue;
            FindSkillMdFiles(sub, results, depth + 1);
        }
    }

    /// <summary>
    /// Reads only the <c>description:</c> field from a SKILL.md YAML frontmatter block.
    /// Returns <c>null</c> when the field is absent, empty, or the file is unreadable.
    /// Kept as a thin wrapper over <see cref="SkillFrontmatterSpec"/> for callers that only need
    /// the description of a single known file.
    /// </summary>
    internal static string? ParseSkillDescription(string skillMdPath)
    {
        try
        {
            var content = File.ReadAllText(skillMdPath);
            var fm      = SkillFrontmatterSpec.TryParse(content);
            return string.IsNullOrWhiteSpace(fm?.Description) ? null : fm.Description;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
