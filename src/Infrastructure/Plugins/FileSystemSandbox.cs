using fuseraft.Core;
using fuseraft.Infrastructure;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Sandbox path resolution and per-turn cache invalidation shared by
/// <see cref="FileSystemPlugin"/>'s read/patch/write pipeline and
/// <see cref="FileSystemManagementOps"/>'s directory/inspection tools. Every method takes its
/// former field reads as explicit parameters instead, so the two classes can share this logic
/// without sharing an instance — only the per-turn <c>HashSet&lt;string&gt;</c>s passed into
/// <see cref="InvalidatePathAsync"/> are shared by reference between them.
/// </summary>
internal static class FileSystemSandbox
{
    // Streams the first `previewCount` lines without allocating the full file into a string
    // array. Returns the preview lines, total line count, and file size in bytes.
    internal static async Task<(List<string> Lines, int TotalLines, long SizeBytes)>
        StreamPreviewLinesAsync(string path, int previewCount)
    {
        var preview   = new List<string>(previewCount);
        int lineCount = 0;
        using var sr  = new StreamReader(path);
        string? ln;
        while ((ln = await sr.ReadLineAsync()) is not null)
        {
            lineCount++;
            if (preview.Count < previewCount) preview.Add(ln);
        }
        return (preview, lineCount, new FileInfo(path).Length);
    }

    // Removes a path from every per-turn set, the session cache, the version store, and the
    // summary cache. Call this on deletion, on the source side of a move, and on the
    // destination side of a copy/move to clear stale state before priming fresh state.
    internal static async Task InvalidatePathAsync(
        string resolved, string summaryDir,
        HashSet<string> readThisTurn, HashSet<string> writtenThisTurn, HashSet<string> patchedThisTurn,
        SessionReadCache? sessionCache, FileVersionStore? versionStore)
    {
        readThisTurn.Remove(resolved);
        writtenThisTurn.Remove(resolved);
        patchedThisTurn.Remove(resolved);
        sessionCache?.Invalidate(resolved);
        if (versionStore is not null)
            await versionStore.RemoveAsync(resolved);
        var sp = SummaryPath(resolved, summaryDir);
        if (File.Exists(sp)) File.Delete(sp);
    }

    // Derives a stable summary-cache filename from the resolved path so the same file always
    // maps to the same summary regardless of how the agent specified it (relative vs absolute).
    internal static string SummaryPath(string resolvedFilePath, string summaryDir)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(resolvedFilePath));
        var hex  = Convert.ToHexString(hash)[..16].ToLowerInvariant();
        return Path.Combine(summaryDir, $"{hex}.md");
    }

    // Strips one layer of wrapping quotes a model sometimes includes in a path argument
    // (e.g. passing `"file.txt"` instead of `file.txt`, out of habit from shell-quoting a
    // path with spaces). A quote character is illegal in a Windows path and vanishingly rare
    // as an actual leading/trailing character in a Unix one, so unwrapping a matched pair is
    // safe and turns an opaque "invalid path" OS error into a working call.
    private static string StripWrappingQuotes(string path)
    {
        var trimmed = path.Trim();

        // Length > 2 (not >= 2) so a quoted-empty-string argument (`""` or `''`) is left alone
        // rather than stripped down to an empty path — Path.GetFullPath("", sandboxRoot)
        // resolves to the sandbox root itself, which callers don't expect a bare path argument
        // to ever produce.
        if (trimmed.Length > 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            return trimmed[1..^1];
        }
        return trimmed;
    }

    // Resolves 'path' to its canonical absolute form and checks it against the sandbox.
    // Returns a [DENIED] error string when the path escapes the sandbox, null when safe.
    internal static string? ResolveSafe(
        string path, string? sandboxRoot, IReadOnlyList<string> exemptedPrefixes, out string resolved)
    {
        var expandedPath = ProcessHelper.ExpandHome(StripWrappingQuotes(path));
        resolved = sandboxRoot is not null && !Path.IsPathRooted(expandedPath)
            ? Path.GetFullPath(expandedPath, sandboxRoot)
            : Path.GetFullPath(expandedPath);

        if (sandboxRoot is null)
            return null;

        // Append the OS separator so that "/sandbox" is not treated as a prefix of "/sandboxExtra".
        var sandboxPrefix = sandboxRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolvedCheck = resolved.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!resolvedCheck.StartsWith(sandboxPrefix, comparison))
        {
            // Allow paths explicitly exempted from the sandbox (e.g. fuseraft's own runtime state dir).
            if (exemptedPrefixes.Any(ep => resolvedCheck.StartsWith(ep, comparison)))
                return null;

            return PluginResult.Denied($"Path '{resolved}' is outside the configured sandbox '{sandboxRoot}'.");
        }

        return null;
    }
}
