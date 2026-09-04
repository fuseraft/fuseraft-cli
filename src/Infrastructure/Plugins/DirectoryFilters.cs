namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Directories that hold build output, dependencies, or VCS metadata rather than source —
/// walking into them wastes tool calls and, for content search, can blow up result size by
/// matching inside compiled binaries (.dll/.pdb) read as text. Shared by every plugin that
/// recursively enumerates files from a directory the model does not pin to a specific path.
/// </summary>
internal static class DirectoryFilters
{
    internal static readonly string[] DefaultExcludedDirs =
        [".git", "node_modules", "bin", "obj", ".vs", ".idea", ".nuget", ".venv", "__pycache__", ".fuseraft", "vendor"];

    // Checks only path segments below `root`, not `root`'s own path. Without this, a caller
    // that explicitly points `root` at (or inside) an excluded tree — e.g. searching directly
    // in a package cache located under a ".nuget" or "vendor" directory — would have every
    // single result filtered out, because the excluded name is also a prefix segment of every
    // returned path. Exclusion is meant to stop an unscoped walk from wandering into these
    // trees, not to block a caller who asked to look there on purpose.
    internal static bool IsExcluded(string path, string root, string[]? excludedDirs = null)
    {
        var sep  = Path.DirectorySeparatorChar;
        var dirs = excludedDirs ?? DefaultExcludedDirs;

        string relative;
        try { relative = Path.GetRelativePath(root, path); }
        catch { relative = path; }

        return dirs.Any(d =>
            relative.Contains($"{sep}{d}{sep}", StringComparison.Ordinal) ||
            relative.StartsWith($"{d}{sep}", StringComparison.Ordinal) ||
            relative.EndsWith($"{sep}{d}", StringComparison.Ordinal) ||
            relative.Equals(d, StringComparison.Ordinal));
    }
}
