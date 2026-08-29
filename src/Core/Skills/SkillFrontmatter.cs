using System.Text.RegularExpressions;

namespace fuseraft.Core.Skills;

/// <summary>
/// The YAML frontmatter fields of a <c>SKILL.md</c> file as defined by the
/// <see href="https://agentskills.io/specification">Agent Skills specification</see>.
/// </summary>
/// <param name="Name">Raw <c>name:</c> value, or empty string if absent. Not guaranteed valid — use <see cref="SkillFrontmatterSpec.ValidateName"/>.</param>
/// <param name="Description">Raw <c>description:</c> value, or empty string if absent.</param>
/// <param name="License">Optional <c>license:</c> value.</param>
/// <param name="Compatibility">Optional <c>compatibility:</c> value.</param>
/// <param name="AllowedTools">Optional <c>allowed-tools:</c> value (space-separated, experimental per spec).</param>
/// <param name="Metadata">Optional <c>metadata:</c> map of string keys to string values.</param>
public sealed record SkillFrontmatter(
    string Name,
    string Description,
    string? License,
    string? Compatibility,
    string? AllowedTools,
    IReadOnlyDictionary<string, string>? Metadata);

/// <summary>
/// Single source of truth for parsing and validating <c>SKILL.md</c> frontmatter against the
/// Agent Skills specification (<see href="https://agentskills.io/specification"/>).
///
/// <para>
/// fuseraft has two separate skill-loading surfaces — the REPL's own hand-rolled loader
/// (<c>ReplSkillsLoader</c>/<c>SkillsPlugin</c>) and orchestration's <c>Microsoft.Agents.AI</c>
/// skills provider — that historically diverged in what they accepted. This type mirrors the
/// validation rules of Microsoft's <c>AgentSkillFrontmatter</c> exactly (same length limits, same
/// name regex) so both surfaces treat a given SKILL.md identically, and so the CLI-side commands
/// (<c>skills add</c>, <c>skills validate</c>, skill curation) can enforce the same rules before
/// ever writing a file to disk.
/// </para>
/// </summary>
public static class SkillFrontmatterSpec
{
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 1024;
    public const int MaxCompatibilityLength = 500;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    // Lowercase letters, numbers, and hyphens only; no leading/trailing/consecutive hyphens.
    private static readonly Regex ValidNameRegex =
        new(@"^[a-z0-9]([a-z0-9]*-[a-z0-9])*[a-z0-9]*$", RegexOptions.Compiled, RegexTimeout);

    // Matches the YAML frontmatter block delimited by "---" lines. Callers strip a leading
    // UTF-8 BOM (via TrimBom) before matching, since some editors prepend one.
    private static readonly Regex FrontmatterBlock =
        new(@"\A^---\s*$(.*?)^---\s*$", RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled, RegexTimeout);

    // Matches a top-level "key: value" line (no leading indentation) — double-quoted,
    // single-quoted, or bare scalar values.
    private static readonly Regex TopLevelKeyValue =
        new(@"^([A-Za-z][\w-]*)\s*:[ \t]*(?:""([^""]*)""|'([^']*)'|(\S.*?))?\s*$", RegexOptions.Multiline | RegexOptions.Compiled, RegexTimeout);

    // Matches a "metadata:" line followed by one or more indented sub-lines.
    private static readonly Regex MetadataBlock =
        new(@"^metadata\s*:\s*$\r?\n((?:[ \t]+\S.*(?:\r?\n|\z))+)", RegexOptions.Multiline | RegexOptions.Compiled, RegexTimeout);

    // Matches an indented "key: value" line within a metadata block.
    private static readonly Regex IndentedKeyValue =
        new(@"^[ \t]+([A-Za-z][\w-]*)\s*:[ \t]*(?:""([^""]*)""|'([^']*)'|(\S.*?))?\s*$", RegexOptions.Multiline | RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex SlugSanitizer = new(@"[^a-z0-9]+", RegexOptions.Compiled, RegexTimeout);

    /// <summary>
    /// Parses the YAML frontmatter block from a SKILL.md file's content. Returns <c>null</c>
    /// when there is no frontmatter block at all, or the block contains none of the recognized
    /// fields. This is a raw parse — it does not validate the values; call
    /// <see cref="Validate"/> to check spec conformance.
    /// </summary>
    public static SkillFrontmatter? TryParse(string? content)
    {
        if (string.IsNullOrEmpty(content)) return null;

        Match block;
        try { block = FrontmatterBlock.Match(TrimBom(content)); }
        catch (RegexMatchTimeoutException) { return null; }
        if (!block.Success) return null;

        var yaml = block.Groups[1].Value;

        string? name = null, description = null, license = null, compatibility = null, allowedTools = null;
        foreach (Match m in TopLevelKeyValue.Matches(yaml))
        {
            var value = ExtractValue(m);
            switch (m.Groups[1].Value.ToLowerInvariant())
            {
                case "name":           name          = value; break;
                case "description":    description   = value; break;
                case "license":        license       = value; break;
                case "compatibility":  compatibility = value; break;
                case "allowed-tools":  allowedTools  = value; break;
            }
        }

        Dictionary<string, string>? metadata = null;
        var metadataMatch = MetadataBlock.Match(yaml);
        if (metadataMatch.Success)
        {
            metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in IndentedKeyValue.Matches(metadataMatch.Groups[1].Value))
                metadata[m.Groups[1].Value] = ExtractValue(m);
        }

        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(description) &&
            license is null && compatibility is null && allowedTools is null && metadata is null)
            return null;

        return new SkillFrontmatter(
            name ?? string.Empty,
            description ?? string.Empty,
            license,
            compatibility,
            allowedTools,
            metadata);
    }

    private static string ExtractValue(Match m) =>
        (m.Groups[2].Success ? m.Groups[2].Value
       : m.Groups[3].Success ? m.Groups[3].Value
       : m.Groups[4].Success ? m.Groups[4].Value
       : string.Empty).Trim();

    /// <summary>
    /// Validates a skill name: 1-64 characters, lowercase letters/numbers/hyphens only,
    /// no leading/trailing/consecutive hyphens.
    /// </summary>
    public static bool ValidateName(string? name, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            reason = "Skill name is required.";
            return false;
        }
        if (name.Length > MaxNameLength)
        {
            reason = $"Skill name must be {MaxNameLength} characters or fewer.";
            return false;
        }
        if (!ValidNameRegex.IsMatch(name))
        {
            reason = "Skill name must use only lowercase letters, numbers, and hyphens, and must not start or end with a hyphen or contain consecutive hyphens.";
            return false;
        }
        reason = null;
        return true;
    }

    /// <summary>Validates a skill description: required, non-empty, 1-1024 characters.</summary>
    public static bool ValidateDescription(string? description, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            reason = "Skill description is required.";
            return false;
        }
        if (description.Length > MaxDescriptionLength)
        {
            reason = $"Skill description must be {MaxDescriptionLength} characters or fewer.";
            return false;
        }
        reason = null;
        return true;
    }

    /// <summary>Validates the optional compatibility field: at most 500 characters.</summary>
    public static bool ValidateCompatibility(string? compatibility, out string? reason)
    {
        if (compatibility?.Length > MaxCompatibilityLength)
        {
            reason = $"Skill compatibility must be {MaxCompatibilityLength} characters or fewer.";
            return false;
        }
        reason = null;
        return true;
    }

    /// <summary>
    /// Full conformance check for a parsed frontmatter against a specific skill directory name —
    /// the same checks fuseraft's orchestration skills provider applies. Returns every violation
    /// found (empty list means fully compliant).
    /// </summary>
    public static IReadOnlyList<string> Validate(SkillFrontmatter? frontmatter, string directoryName)
    {
        var violations = new List<string>();

        if (frontmatter is null)
        {
            violations.Add("No YAML frontmatter block found (SKILL.md must start with a '---' delimited block).");
            return violations;
        }

        if (!ValidateName(frontmatter.Name, out var nameReason))
        {
            violations.Add(nameReason!);
        }
        else if (!string.Equals(frontmatter.Name, directoryName, StringComparison.Ordinal))
        {
            violations.Add($"'name: {frontmatter.Name}' does not match parent directory name '{directoryName}'.");
        }

        if (!ValidateDescription(frontmatter.Description, out var descReason))
            violations.Add(descReason!);

        if (!ValidateCompatibility(frontmatter.Compatibility, out var compatReason))
            violations.Add(compatReason!);

        return violations;
    }

    /// <summary>
    /// Converts an arbitrary string into a spec-valid slug: lowercase, non-alphanumeric runs
    /// collapsed to single hyphens, no leading/trailing hyphens. Used to derive a directory
    /// name (and, after normalization, the <c>name:</c> field) from a skill's raw title.
    /// </summary>
    public static string ToSlug(string name) =>
        SlugSanitizer.Replace((name ?? string.Empty).ToLowerInvariant().Trim(), "-").Trim('-');

    /// <summary>
    /// Rewrites the <c>name:</c> line of a SKILL.md's frontmatter to <paramref name="slug"/>,
    /// leaving the rest of the file untouched. Used when installing or curating a skill whose
    /// original <c>name:</c> field doesn't match the slug it will be installed under — without
    /// this, the file on disk and its own directory name would disagree, which fuseraft's
    /// orchestration skills provider treats as an invalid skill and silently drops.
    /// Appends a <c>name:</c> line to the frontmatter block if one was missing entirely.
    /// </summary>
    public static string WithCanonicalName(string content, string slug)
    {
        var trimmed = TrimBom(content);
        Match block;
        try { block = FrontmatterBlock.Match(trimmed); }
        catch (RegexMatchTimeoutException) { return content; }
        if (!block.Success) return content;

        var yaml = block.Groups[1].Value;
        var nameLine = $"name: {slug}";

        string newYaml;
        var nameMatch = TopLevelKeyValue.Matches(yaml)
            .Cast<Match>()
            .FirstOrDefault(m => string.Equals(m.Groups[1].Value, "name", StringComparison.OrdinalIgnoreCase));

        if (nameMatch is not null)
        {
            newYaml = yaml[..nameMatch.Index] + nameLine + yaml[(nameMatch.Index + nameMatch.Length)..];
        }
        else
        {
            // The captured yaml group starts right after "---" and before its own trailing
            // newline (the '$' anchor is zero-width), so it always begins with '\n' — restore
            // that leading newline here to keep "name:" on its own line after "---".
            newYaml = "\n" + nameLine + "\n" + yaml.TrimStart('\n');
        }

        return trimmed[..block.Groups[1].Index] + newYaml + trimmed[(block.Groups[1].Index + block.Groups[1].Length)..];
    }

    /// <summary>Strips a leading UTF-8 BOM, which some editors prepend and which would
    /// otherwise prevent the frontmatter block regex from matching at position 0.</summary>
    private static string TrimBom(string content) =>
        content.Length > 0 && content[0] == '\uFEFF' ? content[1..] : content;
}
