using fuseraft.Core.Skills;

namespace fuseraft.Cli.Commands.Skills;

internal static class SkillsHelpers
{
    /// <summary>Extracts the slugified <c>name:</c> field, or <c>null</c> when absent/empty.</summary>
    internal static string? ExtractSlug(string content)
    {
        var name = SkillFrontmatterSpec.TryParse(content)?.Name;
        return string.IsNullOrWhiteSpace(name) ? null : ToSlug(name);
    }

    internal static string ExtractDescription(string content) =>
        SkillFrontmatterSpec.TryParse(content)?.Description ?? string.Empty;

    internal static string ToSlug(string name) => SkillFrontmatterSpec.ToSlug(name);

    /// <summary>
    /// Rewrites <paramref name="content"/>'s <c>name:</c> frontmatter field to
    /// <paramref name="slug"/> when it isn't already exactly that value. Ensures a skill
    /// installed under <c>&lt;slug&gt;/SKILL.md</c> always has a matching <c>name:</c> field —
    /// without this, a raw name that needed slugifying (spaces, uppercase, etc.) would leave the
    /// installed file internally inconsistent: fine in the REPL's lenient loader, but silently
    /// dropped by fuseraft's orchestration skills provider, which requires an exact match.
    /// </summary>
    internal static string CanonicalizeName(string content, string slug)
    {
        var currentName = SkillFrontmatterSpec.TryParse(content)?.Name;
        return string.Equals(currentName, slug, StringComparison.Ordinal)
            ? content
            : SkillFrontmatterSpec.WithCanonicalName(content, slug);
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
