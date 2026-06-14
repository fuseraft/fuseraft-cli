using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Scans skill directories, parses SKILL.md frontmatter, and assembles the
/// <see cref="SkillsPlugin"/> instance and catalog block injected into the REPL
/// system prompt at startup.
/// </summary>
internal static class ReplSkillsLoader
{
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
            Path.Combine(home, ".fuseraft", "skills"),
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
    /// Scans <paramref name="searchDirs"/> for <c>SKILL.md</c> files, builds a
    /// slug-to-directory map (first occurrence across dirs wins), and returns a
    /// <see cref="SkillsPlugin"/> together with a catalog string suitable for
    /// appending to the REPL system prompt.
    ///
    /// <para>Returns <c>(null, null)</c> when no skills are found.</para>
    /// <para>
    /// Inaccessible directories are silently skipped so a permissions error on
    /// one dir does not block skills from other dirs.
    /// </para>
    /// </summary>
    internal static (SkillsPlugin? Plugin, string? CatalogBlock) BuildSkills(IEnumerable<string> searchDirs)
    {
        // slug → directory containing SKILL.md; first occurrence wins.
        var skillDirs    = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var descriptions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var searchDir in searchDirs.Where(Directory.Exists))
        {
            IEnumerable<string> skillMds;
            try
            {
                skillMds = Directory.EnumerateFiles(searchDir, "SKILL.md", SearchOption.AllDirectories);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException)                 { continue; }

            foreach (var skillMd in skillMds)
            {
                var skillDir = Path.GetDirectoryName(skillMd);
                if (skillDir is null) continue;
                var slug = Path.GetFileName(skillDir);
                if (string.IsNullOrEmpty(slug) || skillDirs.ContainsKey(slug)) continue;

                skillDirs[slug]    = skillDir;
                descriptions[slug] = ParseSkillDescription(skillMd);
            }
        }

        if (skillDirs.Count == 0) return (null, null);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## SKILLS available");
        foreach (var slug in skillDirs.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            var desc = descriptions.GetValueOrDefault(slug);
            sb.AppendLine(!string.IsNullOrWhiteSpace(desc) ? $"- {slug}: {desc}" : $"- {slug}");
        }
        sb.AppendLine();
        sb.Append("Call load_skill(\"<slug>\") to get full step-by-step instructions before applying a skill.");

        return (new SkillsPlugin(skillDirs), sb.ToString());
    }

    /// <summary>
    /// Reads only the <c>description:</c> field from a SKILL.md YAML frontmatter block.
    /// Returns <c>null</c> when the field is absent, empty, or the file is unreadable.
    /// </summary>
    internal static string? ParseSkillDescription(string skillMdPath)
    {
        try
        {
            var inFrontmatter = false;
            foreach (var line in File.ReadLines(skillMdPath))
            {
                var trimmed = line.Trim();
                if (trimmed == "---")
                {
                    if (!inFrontmatter) { inFrontmatter = true; continue; }
                    break; // closing delimiter
                }
                if (!inFrontmatter) break; // no opening delimiter on first line

                if (trimmed.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = trimmed["description:".Length..].Trim().Trim('"').Trim('\'');
                    return string.IsNullOrWhiteSpace(value) ? null : value;
                }
            }
            return null;
        }
        catch { return null; }
    }
}
