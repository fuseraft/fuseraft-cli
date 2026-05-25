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
}
