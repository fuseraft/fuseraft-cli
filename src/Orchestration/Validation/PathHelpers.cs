namespace fuseraft.Orchestration.Validation;

internal static class PathHelpers
{
    internal static string NormalizePath(string path)
    {
        path = path.Replace('\\', '/').Trim();
        if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        return path;
    }

    internal static bool PathsMatch(string written, string required)
    {
        if (string.Equals(written, required, StringComparison.OrdinalIgnoreCase)) return true;
        if (written.EndsWith("/" + required, StringComparison.OrdinalIgnoreCase)) return true;
        if (required.EndsWith("/" + written, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
