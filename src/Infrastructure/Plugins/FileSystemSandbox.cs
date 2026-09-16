using Microsoft.Extensions.FileSystemGlobbing;
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
///
/// <see cref="ResolveSafeDirectory"/> is also shared by <see cref="ShellPlugin"/> (working
/// directory) and <see cref="GitPlugin"/> (repo path) — despite the class name, it is the
/// general-purpose "directory, not a file path" half of sandbox enforcement, kept here
/// alongside <see cref="ResolveSafe"/> (the file-path half) rather than duplicated per plugin.
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

    // Case sensitivity for root/prefix comparisons: ignore case on Windows (where "Bin" and
    // "bin" name the same directory), ordinal everywhere else.
    private static readonly StringComparison RootComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    // Appends the OS separator so that "/sandbox" is not treated as a prefix of "/sandboxExtra",
    // then checks whether resolvedCheck (itself already separator-terminated by the caller)
    // falls under root.
    private static bool IsUnderRoot(string resolvedCheck, string root) =>
        resolvedCheck.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, RootComparison);

    // Builds a case-insensitive glob matcher from a deny pattern list, or null when the list is
    // empty — callers can pass the result straight to ResolveSafe's denyMatcher parameter without
    // a separate null check. Shared by FileSystemPlugin and FileSystemManagementOps so both read
    // the same deny policy from a single FileSystemPlugin instance (see FileSystemPlugin.DenyMatcher).
    internal static Matcher? BuildDenyMatcher(IReadOnlyList<string>? denyPatterns)
    {
        if (denyPatterns is not { Count: > 0 }) return null;
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        foreach (var pattern in denyPatterns) matcher.AddInclude(pattern);
        return matcher;
    }

    // Resolves 'path' to its canonical absolute form and checks it against the sandbox: the
    // primary root, any additional (--include) root, or an exempted prefix.
    // Returns a [DENIED] error string when the path escapes all of them, null when safe.
    //
    // denyMatcher (optional) is checked before the sandbox-root early-return below, so a deny
    // rule (e.g. blocking .env) applies even in an unsandboxed session — "don't leak secrets
    // into context" shouldn't depend on whether a directory sandbox happens to be configured.
    internal static string? ResolveSafe(
        string path, string? sandboxRoot, IReadOnlyList<string> exemptedPrefixes,
        IReadOnlyList<string> additionalRoots, out string resolved, Matcher? denyMatcher = null)
    {
        var expandedPath = ProcessHelper.ExpandHome(StripWrappingQuotes(path));
        resolved = sandboxRoot is not null && !Path.IsPathRooted(expandedPath)
            ? Path.GetFullPath(expandedPath, sandboxRoot)
            : Path.GetFullPath(expandedPath);

        if (denyMatcher is not null)
        {
            var relBase  = sandboxRoot ?? Path.GetDirectoryName(resolved) ?? resolved;
            var relative = Path.GetRelativePath(relBase, resolved).Replace('\\', '/');
            if (denyMatcher.Match(relative).HasMatches)
                return PluginResult.Denied(
                    $"Path '{resolved}' matches a FileSystem deny rule and is blocked for all operations " +
                    "(likely a credentials file). Do not read, write, or inspect it directly — if a script " +
                    "needs its values, let the script source it internally rather than surfacing its content.");
        }

        if (sandboxRoot is null)
            return null;

        var resolvedCheck = resolved.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (IsUnderRoot(resolvedCheck, sandboxRoot) || additionalRoots.Any(r => IsUnderRoot(resolvedCheck, r)))
            return null;

        // Allow paths explicitly exempted from the sandbox (e.g. fuseraft's own runtime state dir).
        if (exemptedPrefixes.Any(ep => resolvedCheck.StartsWith(ep, RootComparison)))
            return null;

        var rootsNote = additionalRoots.Count > 0
            ? $"'{sandboxRoot}' (+ {additionalRoots.Count} included root(s))"
            : $"'{sandboxRoot}'";
        return PluginResult.Denied($"Path '{resolved}' is outside the configured sandbox {rootsNote}.");
    }

    // Validates that a directory argument (a shell working directory, a git repo path) stays
    // within the sandbox: the primary root or any additional (--include) root. When a sandbox
    // is active and no directory is specified, defaults to the primary sandbox root (never an
    // additional one) so commands never run in an uncontrolled — or ambiguous — directory.
    // Unlike <see cref="ResolveSafe"/>, this never full-paths or checks the directory when no
    // sandbox is configured — callers pass the argument straight through to a subprocess that
    // resolves relative/null paths against its own working directory itself.
    // Returns a [DENIED] error string on violation, null when safe.
    internal static string? ResolveSafeDirectory(
        string? directory, string? sandboxRoot, IReadOnlyList<string> additionalRoots, out string? resolved)
    {
        if (sandboxRoot is null)
        {
            resolved = directory;
            return null;
        }

        resolved = Path.GetFullPath(directory ?? sandboxRoot);
        var resolvedCheck = resolved.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (IsUnderRoot(resolvedCheck, sandboxRoot) || additionalRoots.Any(r => IsUnderRoot(resolvedCheck, r)))
            return null;

        var rootsNote = additionalRoots.Count > 0
            ? $"'{sandboxRoot}' (+ {additionalRoots.Count} included root(s))"
            : $"'{sandboxRoot}'";
        return PluginResult.Denied($"Directory '{resolved}' is outside the configured sandbox {rootsNote}.");
    }
}
