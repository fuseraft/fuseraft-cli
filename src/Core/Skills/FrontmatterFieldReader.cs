using System.Text.RegularExpressions;

namespace fuseraft.Core.Skills;

/// <summary>
/// Reads a single top-level frontmatter field's raw value from a SKILL.md file's content —
/// nothing more.
///
/// <para>
/// This is the one remaining piece of hand-written skill-related code in fuseraft, and it
/// deliberately does no validation of its own. Every real spec question (is this name valid
/// kebab-case, does it match its directory, is the description within the length limit, ...) is
/// answered exclusively by Microsoft.Agents.AI's <c>AgentSkillFrontmatter</c>/
/// <c>AgentFileSkillsSource</c>. Those classes have no public entry point that parses a raw
/// string outside of the full file-discovery pipeline, which itself requires the file to already
/// live at a directory whose name matches its own <c>name:</c> field — a chicken-and-egg problem
/// for the two places that need to know a candidate's intended name <i>before</i> it's placed
/// anywhere: <c>fuseraft skills add</c> (installing a skill whose source directory doesn't yet
/// match) and <c>SkillCurator</c> (writing a freshly-generated skill to disk for the first time).
/// This method exists solely to answer "what does this file currently call itself" for that
/// narrow bootstrapping purpose.
/// </para>
/// </summary>
public static class FrontmatterFieldReader
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static readonly Regex FrontmatterBlock =
        new(@"\A^---\s*$(.*?)^---\s*$", RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex TopLevelKeyValue =
        new(@"^([A-Za-z][\w-]*)\s*:[ \t]*(?:""([^""]*)""|'([^']*)'|(\S.*?))?\s*$", RegexOptions.Multiline | RegexOptions.Compiled, RegexTimeout);

    /// <summary>
    /// Returns the raw value of a top-level <paramref name="key"/> line inside
    /// <paramref name="content"/>'s YAML frontmatter block, or <c>null</c> when the frontmatter
    /// block, the key, or its value is absent.
    /// </summary>
    public static string? ExtractField(string? content, string key)
    {
        if (string.IsNullOrEmpty(content)) return null;

        Match block;
        try { block = FrontmatterBlock.Match(content); }
        catch (RegexMatchTimeoutException) { return null; }
        if (!block.Success) return null;

        foreach (Match m in TopLevelKeyValue.Matches(block.Groups[1].Value))
        {
            if (!string.Equals(m.Groups[1].Value, key, StringComparison.OrdinalIgnoreCase)) continue;

            var value = m.Groups[2].Success ? m.Groups[2].Value
                      : m.Groups[3].Success ? m.Groups[3].Value
                      : m.Groups[4].Success ? m.Groups[4].Value
                      : null;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        return null;
    }
}
