using System.Text.RegularExpressions;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Renders a matched line for <c>grep_file</c>/<c>search_content</c> without letting one huge
/// line flood the result. A short line comes back whole. A line over the cap — a one-line XML
/// export, a minified bundle, a JSON blob — comes back as one window per match, centred on the
/// match and tagged with its 1-based column. Cutting such a line from the start instead (the old
/// behaviour) hid every match past the cap, so a field 20k characters into a 38k one-liner was
/// either reported as "no match" or shown as an unrelated 500-character prefix.
/// </summary>
internal static class MatchExcerpt
{
    internal const int MaxLineLength      = 1000;
    internal const int MaxExcerptsPerLine = 5;

    /// <summary>
    /// Text to print for each match on <paramref name="line"/>: the line itself when it
    /// fits in <see cref="MaxLineLength"/>, otherwise up to <see cref="MaxExcerptsPerLine"/>
    /// windows of about that size, each prefixed with <c>col N:</c>, plus a note when more
    /// matches on the line were left out. Empty when nothing on the line matches. A short line
    /// is returned untouched (not trimmed), so callers keep their own formatting.
    /// </summary>
    internal static List<string> For(string line, Regex regex)
    {
        if (line.Length <= MaxLineLength)
            return regex.IsMatch(line) ? [line] : [];

        var excerpts  = new List<string>();
        var match     = regex.Match(line);
        int omitted   = 0;
        int coveredTo = -1;

        while (match.Success)
        {
            // A match inside the previous window is already on screen; don't repeat it.
            if (match.Index >= coveredTo)
            {
                if (excerpts.Count == MaxExcerptsPerLine)
                {
                    omitted++;
                }
                else
                {
                    var length = Math.Min(match.Length, MaxLineLength);
                    var start  = Math.Max(0, match.Index - (MaxLineLength - length) / 2);
                    var end    = Math.Min(line.Length, start + MaxLineLength);
                    start      = Math.Max(0, end - MaxLineLength);

                    var prefix = start > 0 ? "…" : string.Empty;
                    var suffix = end < line.Length ? "…" : string.Empty;
                    excerpts.Add($"col {match.Index + 1}: {prefix}{line[start..end]}{suffix}");
                    coveredTo = end;
                }
            }

            match = match.NextMatch();  // steps past an empty match on its own
        }

        if (omitted > 0)
            excerpts[^1] += $" [+{omitted} more match(es) on this {line.Length:N0}-char line not shown]";

        return excerpts;
    }

    /// <summary>A non-matching context line, cut from the start like before — it is only there for orientation.</summary>
    internal static string Context(string line) =>
        line.Length > MaxLineLength
            ? line[..MaxLineLength] + $"... [+{line.Length - MaxLineLength} chars truncated]"
            : line;
}
