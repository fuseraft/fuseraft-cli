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

    internal static bool IsExcluded(string path, string[]? excludedDirs = null)
    {
        var sep  = Path.DirectorySeparatorChar;
        var dirs = excludedDirs ?? DefaultExcludedDirs;
        return dirs.Any(d => path.Contains($"{sep}{d}{sep}", StringComparison.Ordinal) ||
                              path.EndsWith($"{sep}{d}", StringComparison.Ordinal));
    }
}
