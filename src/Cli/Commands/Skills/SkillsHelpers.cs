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
    /// Recursively copies every file under <paramref name="sourceDir"/> into
    /// <paramref name="destDir"/>, preserving relative subdirectory structure and creating
    /// <paramref name="destDir"/> if needed. Existing files at the destination are overwritten.
    /// Used by <c>fuseraft skills add</c> so bundled <c>references/</c> and <c>scripts/</c>
    /// files travel with SKILL.md instead of being silently dropped.
    /// </summary>
    internal static void CopySkillDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var filePath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, filePath);
            var destFile = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(filePath, destFile, overwrite: true);
        }
    }
}
