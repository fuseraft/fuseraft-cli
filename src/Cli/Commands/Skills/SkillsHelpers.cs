using System.Text.RegularExpressions;
using fuseraft.Core.Skills;

namespace fuseraft.Cli.Commands.Skills;

/// <summary>
/// Bootstrapping helpers exclusive to <c>fuseraft skills add</c>, which — unlike every other
/// skill-related surface (REPL, orchestration, <c>skills validate</c>, <c>skills list</c>,
/// <see cref="fuseraft.Orchestration.Skills.SkillCurator"/>, all of which use
/// Microsoft.Agents.AI's <c>AgentFileSkillsSource</c>/<c>AgentSkillFrontmatter</c> directly) —
/// intentionally stays lenient: it derives an install slug from a raw title (spaces, uppercase,
/// ...) and rewrites the installed copy's <c>name:</c> field to match, rather than requiring the
/// source to already be spec-compliant. See docs/skills.md.
/// </summary>
internal static class SkillsHelpers
{
    private static readonly Regex SlugSanitizer = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    /// <summary>Extracts the slugified <c>name:</c> field, or <c>null</c> when absent/empty.</summary>
    internal static string? ExtractSlug(string content)
    {
        var name = FrontmatterFieldReader.ExtractField(content, "name");
        return string.IsNullOrWhiteSpace(name) ? null : ToSlug(name);
    }

    /// <summary>Converts an arbitrary title into a spec-valid slug candidate: lowercase, non-alphanumeric runs collapsed to single hyphens, no leading/trailing hyphens.</summary>
    internal static string ToSlug(string name) =>
        SlugSanitizer.Replace(name.ToLowerInvariant().Trim(), "-").Trim('-');

    /// <summary>
    /// Rewrites <paramref name="content"/>'s <c>name:</c> frontmatter field to
    /// <paramref name="slug"/> when it isn't already exactly that value (inserting one if the
    /// field was missing entirely). Ensures a skill installed under <c>&lt;slug&gt;/SKILL.md</c>
    /// always has a matching <c>name:</c> field — without this, a raw title that needed
    /// slugifying would leave the installed file internally inconsistent: fine in the REPL's
    /// lenient loader, but rejected by <c>AgentFileSkillsSource</c>'s name-matches-directory
    /// check, which orchestration and <c>skills validate</c> both enforce.
    /// </summary>
    internal static string CanonicalizeName(string content, string slug)
    {
        var currentName = FrontmatterFieldReader.ExtractField(content, "name");
        if (string.Equals(currentName, slug, StringComparison.Ordinal))
            return content;

        var frontmatterMatch = Regex.Match(content, @"\A^---\s*$(.*?)^---\s*$", RegexOptions.Multiline | RegexOptions.Singleline);
        if (!frontmatterMatch.Success)
            return content;

        var yaml     = frontmatterMatch.Groups[1].Value;
        var nameLine = $"name: {slug}";

        string newYaml;
        var existingNameLine = Regex.Match(yaml, @"^name\s*:[ \t]*.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (existingNameLine.Success)
        {
            newYaml = yaml[..existingNameLine.Index] + nameLine + yaml[(existingNameLine.Index + existingNameLine.Length)..];
        }
        else
        {
            // The captured yaml group starts right after "---" and before its own trailing
            // newline (the regex's '$' anchor is zero-width), so it always begins with '\n'.
            newYaml = "\n" + nameLine + "\n" + yaml.TrimStart('\n');
        }

        return content[..frontmatterMatch.Groups[1].Index] + newYaml + content[(frontmatterMatch.Groups[1].Index + frontmatterMatch.Groups[1].Length)..];
    }

    /// <summary>
    /// Mirrors every file under <paramref name="sourceDir"/> into <paramref name="destDir"/>,
    /// preserving relative subdirectory structure and creating <paramref name="destDir"/> if
    /// needed. Existing files at the destination are overwritten, and — critically for
    /// <c>fuseraft skills add</c> re-installing an already-installed skill — any file present
    /// in <paramref name="destDir"/> that no longer exists in <paramref name="sourceDir"/> is
    /// deleted. Without this, updating a skill whose author removed or renamed a bundled file
    /// (a <c>references/</c> doc, a <c>scripts/</c> file) leaves the stale copy behind forever:
    /// <c>read_skill_resource</c> would keep returning its old content indefinitely, since it
    /// reads straight from the installed directory with no knowledge the source ever changed.
    /// Git metadata (a <c>.git</c> directory, or the <c>.git</c> pointer file of a worktree or
    /// submodule) is never copied, so a stale one from an earlier install is pruned too.
    /// </summary>
    internal static void CopySkillDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        var sourceRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in EnumerateSkillFiles(sourceDir))
        {
            var relative = Path.GetRelativePath(sourceDir, filePath);
            sourceRelativePaths.Add(relative);
            var destFile = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(filePath, destFile, overwrite: true);
        }

        foreach (var existingFile in Directory.EnumerateFiles(destDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(destDir, existingFile);
            if (!sourceRelativePaths.Contains(relative))
            {
                File.Delete(existingFile);
            }
        }
    }

    // Walks manually rather than using SearchOption.AllDirectories so a large .git tree is never descended into.
    private static IEnumerable<string> EnumerateSkillFiles(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (!IsGitMetadata(file))
                yield return file;
        }

        foreach (var subDir in Directory.EnumerateDirectories(dir))
        {
            if (IsGitMetadata(subDir))
                continue;
            foreach (var file in EnumerateSkillFiles(subDir))
                yield return file;
        }
    }

    private static bool IsGitMetadata(string path) =>
        Path.GetFileName(path).Equals(".git", StringComparison.OrdinalIgnoreCase);
}
