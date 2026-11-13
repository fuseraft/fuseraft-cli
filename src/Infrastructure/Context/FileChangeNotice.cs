using System.Text;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Infrastructure.Context;

/// <summary>
/// The message telling the agent which files it has seen changed, and how. Changes made outside fuseraft come with a
/// short diff, since the agent can't know them; for changes made while one of its tools ran, it knows what it ran.
/// </summary>
public static class FileChangeNotice
{
    internal const int MaxFiles     = 10;
    internal const int MaxDiffLines = 40;

    /// <param name="during">The tool that was running, or null for changes made outside fuseraft.</param>
    public static string Build(IReadOnlyList<FileChange> changes, string? during)
    {
        var sb = new StringBuilder(during is null
            ? "[fuseraft: files you've seen changed outside fuseraft]\n"
            : $"[fuseraft: files you've seen changed while {during} ran]\n");
        var diffBudget = during is null ? MaxDiffLines : 0;

        foreach (var (change, i) in changes.Take(MaxFiles).Select((c, i) => (c, i + 1)))
        {
            var path = Display(change.Path);
            if (change.Deleted)
            {
                sb.Append($"{i}. `{path}` was deleted.\n");
                continue;
            }
            var diff = change.Before is not null && change.After is not null ? Diff(change.Before, change.After) : null;
            sb.Append(diff is null
                ? $"{i}. `{path}` changed.\n"
                : $"{i}. `{path}` changed (+{diff.Value.Added.Count}/−{diff.Value.Removed.Count} lines).\n");
            if (diff is { } d && diffBudget > 0)
            {
                var lines = d.Removed.Select(l => "   - " + l).Concat(d.Added.Select(l => "   + " + l)).ToList();
                foreach (var line in lines.Take(diffBudget)) sb.Append(line).Append('\n');
                if (lines.Count > diffBudget) sb.Append($"   … {lines.Count - diffBudget} more changed lines\n");
                diffBudget -= Math.Min(diffBudget, lines.Count);
            }
        }
        if (changes.Count > MaxFiles) sb.Append($"…and {changes.Count - MaxFiles} more.\n");

        if (changes.Any(c => !c.Deleted))
            sb.Append("Your copies of these are out of date. Re-read before editing; write_file on them is refused until you do.\n");
        foreach (var c in changes.Where(c => c.NewSecrets.Count > 0))
            sb.Append($"Warning: `{Display(c.Path)}` now holds the value of {string.Join(", ", c.NewSecrets)} in plain text. " +
                      "If that wasn't intended, remove it and tell the user, who may need to rotate it.\n");

        return EnvSecretMasker.Mask(sb.ToString().TrimEnd());
    }

    // Relative to the working directory when it's under it, as the agent usually names files.
    private static string Display(string path)
    {
        var cwd = Directory.GetCurrentDirectory();
        var relative = Path.GetRelativePath(cwd, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative;
    }

    // The block between the common head and tail: exact for a single edited region, and a fair count for scattered ones.
    internal static (List<string> Removed, List<string> Added)? Diff(string before, string after)
    {
        var a = before.Replace("\r\n", "\n").Split('\n');
        var b = after.Replace("\r\n", "\n").Split('\n');
        var head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head]) head++;
        var tail = 0;
        while (tail < a.Length - head && tail < b.Length - head && a[^(tail + 1)] == b[^(tail + 1)]) tail++;
        return ([.. a[head..(a.Length - tail)]], [.. b[head..(b.Length - tail)]]);
    }
}
