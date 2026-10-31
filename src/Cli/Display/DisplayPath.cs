namespace fuseraft.Cli.Display;

/// <summary>Shortens a path for display: relative to a root when it lies under it, unchanged otherwise.</summary>
public static class DisplayPath
{
    public static string Relative(string path, string root)
    {
        try
        {
            var abs = Path.GetFullPath(path, root);
            var rel = Path.GetRelativePath(root, abs);
            var outside = rel == "." || rel == ".." || Path.IsPathRooted(rel)
                || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            return outside ? path : rel;
        }
        catch { return path; }
    }
}
