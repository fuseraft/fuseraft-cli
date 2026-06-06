using System.Text.RegularExpressions;

namespace fuseraft.Core;

/// <summary>
/// Parses .fuseraft/.fuseraftignore and answers whether a virtual path is ephemeral.
/// Virtual paths strip the global root and project slug:
///   ~/.fuseraft/sessions/{slug}/{id}/read_cache.json  → "sessions/{id}/read_cache.json"
///   ~/.fuseraft/state/{slug}/knowledge_findings.json  → "state/knowledge_findings.json"
///   ~/.fuseraft/logs/{slug}/app.log                   → "logs/app.log"
/// Gitignore semantics: last matching rule wins; "!" negates.
/// </summary>
public sealed class FuseraftIgnoreRules
{
    public static readonly FuseraftIgnoreRules Empty = new([]);

    private readonly List<(Regex Pattern, bool Negate)> _rules;

    public bool HasRules => _rules.Count > 0;

    private FuseraftIgnoreRules(string[] lines)
    {
        _rules = [];
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            bool negate  = line.StartsWith('!');
            var  pattern = negate ? line[1..] : line;

            // Trailing / means directory — expand to match all files under it.
            if (pattern.EndsWith('/')) pattern += "**";

            var regex = ToRegex(pattern);
            if (regex is not null)
                _rules.Add((regex, negate));
        }
    }

    public static FuseraftIgnoreRules Load(string? path = null)
    {
        path ??= ".fuseraft/.fuseraftignore";
        return File.Exists(path) ? new FuseraftIgnoreRules(File.ReadAllLines(path)) : Empty;
    }

    /// <summary>
    /// Returns true if <paramref name="virtualPath"/> is marked ephemeral.
    /// Last matching rule wins; "!" rules override to keep.
    /// </summary>
    public bool IsEphemeral(string virtualPath)
    {
        virtualPath = virtualPath.Replace('\\', '/');
        bool ephemeral = false;
        foreach (var (pattern, negate) in _rules)
        {
            if (pattern.IsMatch(virtualPath))
                ephemeral = !negate;
        }
        return ephemeral;
    }

    private static Regex? ToRegex(string pattern)
    {
        try
        {
            pattern = pattern.Replace('\\', '/');
            // Escape for regex, then restore glob semantics.
            // Order matters: replace ** before * to avoid double-processing.
            var s = Regex.Escape(pattern)
                .Replace(@"\*\*/", "(.+/)?")  // **/ → zero-or-more path components
                .Replace(@"\*\*",  ".+")       // **  → one-or-more of anything
                .Replace(@"\*",    "[^/]+")    // *   → one path component segment
                .Replace(@"\?",    "[^/]");    // ?   → single non-separator char
            return new Regex("^" + s + "$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        }
        catch
        {
            return null;
        }
    }
}
