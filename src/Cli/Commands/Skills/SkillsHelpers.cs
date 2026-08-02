using System.Text.RegularExpressions;

namespace fuseraft.Cli.Commands.Skills;

internal static class SkillsHelpers
{
    private static readonly Regex NameFrontmatter =
        new(@"^name:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static readonly Regex DescriptionFrontmatter =
        new(@"^description:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    internal static string? ExtractSlug(string content)
    {
        var m = NameFrontmatter.Match(content);
        if (!m.Success) return null;
        var name = m.Groups[1].Value.Trim().Trim('"').Trim('\'');
        return string.IsNullOrWhiteSpace(name) ? null : ToSlug(name);
    }

    internal static string ExtractDescription(string content)
    {
        var m = DescriptionFrontmatter.Match(content);
        if (!m.Success) return string.Empty;
        return m.Groups[1].Value.Trim().Trim('"').Trim('\'');
    }

    internal static string ToSlug(string name) =>
        Regex.Replace(name.ToLowerInvariant().Trim(), @"[^a-z0-9]+", "-").Trim('-');

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
