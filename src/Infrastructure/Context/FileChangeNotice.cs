using System.Text;

namespace fuseraft.Infrastructure.Context;

/// <summary>
/// The message telling the agent which files it has seen changed. It names them and nothing more: the agent reads what it
/// needs, with read_file's own limits, instead of getting diffs that a file with long lines (JSONL, minified code) would blow up.
/// </summary>
public static class FileChangeNotice
{
    internal const int MaxFiles = 10;

    /// <param name="during">The tool that was running, or null for changes made outside fuseraft.</param>
    public static string Build(IReadOnlyList<FileChange> changes, string? during)
    {
        var sb = new StringBuilder(during is null
            ? "[fuseraft: files you've seen changed outside fuseraft]\n"
            : $"[fuseraft: files you've seen changed while {during} ran]\n");

        foreach (var (change, i) in changes.Take(MaxFiles).Select((c, i) => (c, i + 1)))
            sb.Append($"{i}. `{Display(change.Path)}` {(change.Deleted ? "was deleted" : "changed")}.\n");
        if (changes.Count > MaxFiles) sb.Append($"…and {changes.Count - MaxFiles} more.\n");

        if (changes.Any(c => !c.Deleted))
            sb.Append("Your copies of these are out of date. Re-read before editing; write_file on them is refused until you do.\n");

        return sb.ToString().TrimEnd();
    }

    // Relative to the working directory when it's under it, as the agent usually names files.
    private static string Display(string path)
    {
        var cwd = Directory.GetCurrentDirectory();
        var relative = Path.GetRelativePath(cwd, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative;
    }
}
