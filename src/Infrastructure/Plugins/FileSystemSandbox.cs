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
    //
    // Internal (not private) so ResolveSafeDirectory can apply the same unwrapping to
    // directory arguments (shell_run's workingDirectory, GitPlugin's repoPath) — those route
    // through this method instead of ResolveAgainstRoot, so they'd otherwise miss the fix
    // entirely. See https://github.com/fuseraft/fuseraft-cli/issues/117.
    internal static string StripWrappingQuotes(string path)
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

    // The wording ResolveSafe uses for a deny-rule denial, and the marker IsDenyRuleDenial looks for.
    // One constant so the message and the check can't drift apart.
    private const string DenyRulePhrase = "matches a FileSystem deny rule";

    /// <summary>The standard denial for a path that matches a deny rule — shared so every tool words it alike.</summary>
    internal static string DenyRuleDenial(string resolvedPath) =>
        PluginResult.Denied(
            $"Path '{resolvedPath}' {DenyRulePhrase} and is blocked for all operations " +
            "(likely a credentials file). Do not read, write, or inspect it directly — if a script " +
            "needs its values, let the script source it internally rather than surfacing its content.");

    /// <summary>
    /// True when <paramref name="denial"/> is a <c>FileSystem</c> deny-rule denial (a credentials
    /// file, <c>.env</c>, a configured <c>Deny</c> glob) rather than a sandbox-boundary one. The two
    /// must never be treated alike: leaving the sandbox is a boundary a human can widen with an
    /// approval, but a deny rule is an explicit "never" — see
    /// <see cref="IncludedRootsState.DenyOrEscalateAsync"/>.
    /// </summary>
    internal static bool IsDenyRuleDenial(string? denial) =>
        denial is not null && denial.Contains(DenyRulePhrase, StringComparison.Ordinal);

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

    /// <summary>
    /// True when <paramref name="resolved"/> (an absolute path) matches <paramref name="denyMatcher"/>.
    /// The single definition of "is this path deny-ruled", shared by <see cref="ResolveSafe"/> and by
    /// tools that enumerate files themselves (the Search plugin) so they can't disagree.
    /// </summary>
    internal static bool MatchesDenyRule(Matcher? denyMatcher, string resolved, string? sandboxRoot)
    {
        if (denyMatcher is null) return false;

        var relBase  = sandboxRoot ?? Path.GetDirectoryName(resolved) ?? resolved;
        var relative = Path.GetRelativePath(relBase, resolved).Replace('\\', '/');
        if (denyMatcher.Match(relative).HasMatches) return true;

        // With no sandbox root the path above is just the file name, so a pattern that names a
        // directory (`**/.aws/credentials`) could never match. The same goes for a path that has
        // left the sandbox: its root-relative form is `../elsewhere/.env`, which no glob matches.
        // Also try the path relative to the filesystem root; matching either way keeps every
        // pattern that matched before.
        var leftSandbox = sandboxRoot is null || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)
                          || Path.IsPathRooted(relative);
        return leftSandbox
               && Path.GetPathRoot(resolved) is { Length: > 0 } fsRoot
               && denyMatcher.Match(Path.GetRelativePath(fsRoot, resolved).Replace('\\', '/')).HasMatches;
    }

    // Resolves 'path' to its canonical absolute form: expands ~, strips wrapping quotes, and
    // resolves a relative path against sandboxRoot (or the process CWD when sandboxRoot is
    // null). Shared by SandboxEnforcementFilter (the orchestration-side sandbox layer), which
    // previously reimplemented this exact expand/resolve sequence inline at five separate call
    // sites — see that class's history for why a single shared primitive matters here: a fix
    // to path resolution (a Windows UNC edge case, a new escape technique) previously had to
    // land in up to six places at once to actually take effect everywhere.
    internal static string ResolveAgainstRoot(string path, string? sandboxRoot)
    {
        var expandedPath = ProcessHelper.ExpandHome(StripWrappingQuotes(path));
        return sandboxRoot is not null && !Path.IsPathRooted(expandedPath)
            ? Path.GetFullPath(expandedPath, sandboxRoot)
            : Path.GetFullPath(expandedPath);
    }

    // True when 'resolved' falls outside sandboxRoot, every entry of additionalRoots, and every
    // entry of exemptedPrefixes. 'resolved' must already be a canonical absolute path (e.g. via
    // ResolveAgainstRoot) — this only compares prefixes, it does not resolve anything itself.
    internal static bool IsOutsideSandbox(
        string resolved, string sandboxRoot,
        IReadOnlyList<string>? exemptedPrefixes = null, IReadOnlyList<string>? additionalRoots = null)
    {
        var resolvedCheck = resolved.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (IsUnderRoot(resolvedCheck, sandboxRoot)) return false;
        if (additionalRoots is not null && additionalRoots.Any(r => IsUnderRoot(resolvedCheck, r))) return false;
        if (exemptedPrefixes is not null && exemptedPrefixes.Any(ep => resolvedCheck.StartsWith(ep, RootComparison))) return false;

        return true;
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
        resolved = ResolveAgainstRoot(path, sandboxRoot);

        if (MatchesDenyRule(denyMatcher, resolved, sandboxRoot))
            return DenyRuleDenial(resolved);

        if (sandboxRoot is null)
            return null;

        if (!IsOutsideSandbox(resolved, sandboxRoot, exemptedPrefixes, additionalRoots))
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
        // Unwrap a literal quoted directory argument (e.g. `"C:\workspace\source\QG"`) before
        // it reaches Path.GetFullPath or a subprocess — otherwise the quote characters end up
        // baked into the resolved path, producing a malformed working directory instead of a
        // clean sandbox denial or a working call. Applied even when unsandboxed (below), since
        // the quotes break the raw argument for the subprocess either way.
        if (directory is not null)
            directory = StripWrappingQuotes(directory);

        if (sandboxRoot is null)
        {
            resolved = directory;
            return null;
        }

        resolved = Path.GetFullPath(directory ?? sandboxRoot);

        if (!IsOutsideSandbox(resolved, sandboxRoot, additionalRoots: additionalRoots))
            return null;

        var rootsNote = additionalRoots.Count > 0
            ? $"'{sandboxRoot}' (+ {additionalRoots.Count} included root(s))"
            : $"'{sandboxRoot}'";
        return PluginResult.Denied($"Directory '{resolved}' is outside the configured sandbox {rootsNote}.");
    }
}
