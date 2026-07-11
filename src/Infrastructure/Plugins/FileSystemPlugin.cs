using System.ComponentModel;
using Microsoft.Extensions.AI;
using fuseraft.Core;
using fuseraft.Infrastructure;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Gives agents read/write access to the local filesystem.
///
/// When <paramref name="sandboxRoot"/> is provided (recommended for production), all path
/// arguments are resolved to their absolute canonical form and rejected if they fall outside
/// the sandbox tree. This prevents path-traversal attacks and accidental access to sensitive
/// files such as SSH keys or environment files.
///
/// <paramref name="exemptedPaths"/> lists path prefixes that bypass the sandbox check.
/// Used to allow fuseraft's own runtime state directory (<c>~/.fuseraft/</c>) even when
/// a project sandbox is active, so agents can write session artifacts (briefs, events, etc.)
/// without those paths being denied.
/// </summary>
public sealed class FileSystemPlugin : ITurnResettable
{
    // Canonical form of the sandbox root, or null when unrestricted.
    private readonly string? _sandboxRoot;
    // Absolute path prefixes that are always accessible even when sandboxed.
    // Used to allow fuseraft's own runtime state dir (~/.fuseraft/) regardless of the project sandbox.
    private readonly IReadOnlyList<string> _exemptedPrefixes;
    private readonly int     _readFileSizeLimit;
    private readonly string  _summaryDir;
    private readonly FileVersionStore?  _versionStore;
    private readonly SessionReadCache?  _sessionCache;
    private readonly Action?            _onWrite;
    private readonly Action?            _onCacheHit;

    // Per-turn read cache: cleared at the start of each agent turn so re-reading the same
    // file within a single turn is caught and short-circuited before dumping redundant
    // content into the model's context.
    private readonly HashSet<string> _readThisTurn = new(StringComparer.OrdinalIgnoreCase);

    // Paths that were successfully patch_file'd this turn. A write_file to any of these
    // paths is blocked: the write is derived from the agent's stale mental model, not the
    // current disk state, so it would silently clobber the patch that was just applied.
    private readonly HashSet<string> _patchedThisTurn = new(StringComparer.OrdinalIgnoreCase);

    // Paths written via write_file this turn. Used in CheckSessionCache to suppress the
    // session-level cache hit for the first within-turn read after a write, so agents can
    // still read back and verify what they just wrote. Cleared by BeginTurn().
    private readonly HashSet<string> _writtenThisTurn = new(StringComparer.OrdinalIgnoreCase);

    // Per-turn cumulative read budget (chars). Prevents individual tool calls from
    // individually respecting the per-call size limit while still collectively flooding
    // the in-turn context with hundreds of thousands of chars of file content — the
    // primary cause of 400k+ input-token turns. Cleared by BeginTurn().
    // Default: 150,000 chars ≈ 37,500 tokens. Agents making >N large file reads per turn
    // receive a "budget exhausted" error and must proceed with the context they have.
    private int _readBudgetUsed;
    private readonly int _readBudgetPerTurn;

    // Pre-read byte threshold: if the file exceeds this, stream just the first 30 lines +
    // a line count for the preview instead of allocating a full string array. Internal so
    // FileSystemManagementOps.GetFileSummaryAsync can apply the same threshold.
    internal const int LargeFileByteThreshold = 25_000;
    // maxLines values larger than this are treated as cold reads — an agent passing
    // maxLines: 99999 is asking for everything and should be gated the same as omitting it.
    private const int LargeFileColdReadLines  = 500;

    public FileSystemPlugin(string? sandboxRoot = null, int readFileSizeLimit = 20_000, int readBudgetPerTurn = 150_000, FileVersionStore? versionStore = null, SessionReadCache? sessionCache = null, Action? onWrite = null, Action? onCacheHit = null, IReadOnlyList<string>? exemptedPaths = null)
    {
        _sandboxRoot       = sandboxRoot is not null ? FuseraftPaths.ExpandPath(sandboxRoot) : null;
        _exemptedPrefixes  = (exemptedPaths ?? [])
            .Select(p => FuseraftPaths.ExpandPath(p).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToList();
        _readFileSizeLimit = readFileSizeLimit > 0 ? readFileSizeLimit : 20_000;
        _readBudgetPerTurn = readBudgetPerTurn > 0 ? readBudgetPerTurn : 150_000;
        var baseDir        = _sandboxRoot ?? Directory.GetCurrentDirectory();
        _summaryDir        = Path.Combine(baseDir, ".fuseraft", "summaries");
        _versionStore      = versionStore;
        _sessionCache      = sessionCache;
        _onWrite           = onWrite;
        _onCacheHit        = onCacheHit;
    }

    /// <inheritdoc cref="ITurnResettable.BeginTurn"/>
    void ITurnResettable.BeginTurn()
    {
        _readThisTurn.Clear();
        _patchedThisTurn.Clear();
        _writtenThisTurn.Clear();
        _readBudgetUsed = 0;
    }

    // Exposed so FileSystemManagementOps (registered as "FileSystem"'s second backing object,
    // see PluginRegistry.RegisterAdditional) shares the exact same per-turn HashSet instances —
    // InvalidatePathAsync calls from either object must clear entries the other one added.
    internal HashSet<string> ReadThisTurnState    => _readThisTurn;
    internal HashSet<string> WrittenThisTurnState => _writtenThisTurn;
    internal HashSet<string> PatchedThisTurnState => _patchedThisTurn;

    [Description("Read text file content. Use startLine+maxLines for large files. Binary files rejected.")]
    public async Task<string> ReadFileAsync(
        [Description("File path.")] string path,
        [Description("1-based start line.")] int startLine = 1,
        [Description("Max lines to return.")] int maxLines = 0)
    {
        var denial = FileSystemSandbox.ResolveSafe(path, _sandboxRoot, _exemptedPrefixes, out var resolved);
        if (denial is not null) return denial;

        if (!File.Exists(resolved))
            return PluginResult.Error($"File not found: {resolved}");

        // Compute FileInfo once — used by the session cache check and the cold-read gate.
        var fileInfo = new FileInfo(resolved);

        var cacheResult = CheckSessionCache(resolved, fileInfo, startLine, maxLines);
        if (cacheResult is not null) return cacheResult;

        // Reject binary files early by sniffing the first 8 KB for null bytes.
        using (var probe = File.OpenRead(resolved))
        {
            var buf = new byte[Math.Min(8192, probe.Length)];
            int read = await probe.ReadAsync(buf);
            if (Array.IndexOf(buf, (byte)0, 0, read) >= 0)
                return PluginResult.Error(
                    $"'{resolved}' appears binary — cannot read as text. Use shell_run with 'file', 'strings', or 'xxd'.");
        }

        var effectiveStart = Math.Max(1, startLine);

        var largeFileResult = await GateLargeFileAsync(resolved, fileInfo, effectiveStart, maxLines);
        if (largeFileResult is not null) return largeFileResult;

        var allLines   = await File.ReadAllLinesAsync(resolved);
        var totalLines = allLines.Length;

        if (effectiveStart > totalLines)
            return PluginResult.Error(
                $"startLine {effectiveStart} exceeds file length ({totalLines} lines): {resolved}");

        // Slice to the requested range (convert to 0-based index).
        var slice = allLines.AsSpan(effectiveStart - 1);
        if (maxLines > 0 && slice.Length > maxLines)
            slice = slice[..maxLines];

        var budgetResult = ReadWithBudget(resolved, fileInfo, slice, effectiveStart, totalLines, startLine, maxLines, out var content);
        if (budgetResult is not null) return budgetResult;

        return content!;
    }

    // Session-level read cache: if the file is in the cache and unchanged on disk
    // (matching mtime + size), return a hint instead of re-dumping the full content.
    // Only fires on cold reads (no startLine/maxLines override), same condition as the
    // per-turn cache below. After compaction the content may no longer be in context,
    // so agents can pass startLine/maxLines to force a targeted re-read.
    // Also handles the turn-level read cache — identical file reads within one agent turn
    // return a short reminder instead of re-dumping the full content into context. The cache
    // is cleared by ITurnResettable.BeginTurn() at the start of each agent turn.
    // Reads with a non-default range (startLine > 1 or maxLines > 0) bypass the cache
    // so agents can page through a file in sections.
    // Returns a result string when a cache hit is detected, or null to continue reading.
    private string? CheckSessionCache(string resolved, FileInfo fileInfo, int startLine, int maxLines)
    {
        if (startLine <= 1 && maxLines <= 0 && _sessionCache is not null
            && !_writtenThisTurn.Contains(resolved)
            && _sessionCache.TryGetHit(resolved, fileInfo, out var cacheHit))
        {
            _onCacheHit?.Invoke();
            string hint;
            if (cacheHit!.ReadCount == 0)
            {
                var ago = FormatTimeAgo(DateTime.UtcNow - cacheHit.LastReadUtc);
                hint = $"'{resolved}' was written this session ({ago} ago) and has not changed " +
                       $"since. The content is in your conversation history via the write_file call " +
                       $"(unless compacted away). Use grep_file to search within it, or pass " +
                       $"startLine/maxLines to force a targeted re-read.";
            }
            else
            {
                var ago   = FormatTimeAgo(DateTime.UtcNow - cacheHit.LastReadUtc);
                var times = cacheHit.ReadCount == 1 ? "once" : $"{cacheHit.ReadCount} times";
                hint = $"'{resolved}' has not changed since it was last read this session " +
                       $"({times}, {ago} ago). Content from that read is in your conversation " +
                       $"history (unless compacted away). Use grep_in_file to locate a specific " +
                       $"section, or pass startLine/maxLines to force a targeted re-read.";
            }
            return PluginResult.Info(hint);
        }

        if (startLine <= 1 && maxLines <= 0 && !_readThisTurn.Add(resolved))
        {
            _onCacheHit?.Invoke();
            return PluginResult.Info(
                $"'{resolved}' already read this turn — content is in context. " +
                $"Use grep_in_file to locate a section, then read_file with startLine/maxLines for a targeted excerpt.");
        }

        return null;
    }

    // Cold-read gate: fires when no meaningful maxLines cap is set ("give me everything"),
    // regardless of startLine — a large file requested from line 2 with no cap is just as
    // expensive as one from line 1. Byte pre-check avoids allocating a full string array
    // for a file we're about to redirect.
    // Returns a result string when the large-file gate fires (preview or budget error), or null to continue.
    private async Task<string?> GateLargeFileAsync(string resolved, FileInfo fileInfo, int effectiveStart, int maxLines)
    {
        bool isColdRead = maxLines <= 0 || maxLines > LargeFileColdReadLines;
        if (isColdRead && fileInfo.Length > LargeFileByteThreshold)
        {
            var (coldLines, coldLineCount, coldSizeBytes) = await FileSystemSandbox.StreamPreviewLinesAsync(resolved, 30);
            var preview = string.Join('\n', coldLines) +
                $"\n\n[Large file — {coldLineCount:N0} lines ({coldSizeBytes:N0} bytes). " +
                $"Cold-reading would flood your context. " +
                $"Use grep_file to locate the relevant section, then read_file with startLine/maxLines.]";
            if (_readBudgetUsed + preview.Length > _readBudgetPerTurn)
                return PluginResult.Error(
                    $"Read budget exhausted ({_readBudgetUsed:N0}/{_readBudgetPerTurn:N0} chars). " +
                    $"Proceed with context already available — use patch_file or shell_run. Budget resets next turn.");
            _readBudgetUsed += preview.Length;
            _sessionCache?.RecordRead(resolved, fileInfo);
            return preview;
        }

        return null;
    }

    // Applies the character cap across the selected lines, checks the per-turn read budget,
    // appends a navigation hint when the output is a partial view, and records the read in
    // the session cache for full cold reads.
    // Returns an error string when the budget is exhausted, or null on success (content is set via out parameter).
    private string? ReadWithBudget(string resolved, FileInfo fileInfo, ReadOnlySpan<string> slice,
        int effectiveStart, int totalLines, int startLine, int maxLines, out string? content)
    {
        // Apply character cap across the selected lines.
        var sb = new System.Text.StringBuilder();
        int totalChars = 0;
        int linesIncluded = 0;
        bool charTruncated = false;

        foreach (var line in slice)
        {
            var lineWithNl = line + "\n";
            var available  = Math.Min(lineWithNl.Length, _readFileSizeLimit - totalChars);
            sb.Append(lineWithNl, 0, available);
            totalChars += available;
            linesIncluded++;
            if (totalChars >= _readFileSizeLimit) { charTruncated = true; break; }
        }

        var endLine = effectiveStart + linesIncluded - 1;
        var built   = sb.ToString();

        // Per-turn read budget: reject this read if adding its content would exceed the
        // cumulative char limit for this turn. Large numbers of file reads is the primary
        // driver of 400k+ input-token turns — once the budget is hit, the agent must
        // proceed with what it already has in context rather than reading more files.
        if (_readBudgetUsed + built.Length > _readBudgetPerTurn)
        {
            content = null;
            return PluginResult.Error(
                $"Read budget exhausted ({_readBudgetUsed:N0}/{_readBudgetPerTurn:N0} chars). " +
                $"Proceed with context already available — use patch_file or shell_run. Budget resets next turn.");
        }

        _readBudgetUsed += built.Length;

        built = AnnotateTypographicWarnings(built, effectiveStart, endLine, totalLines, startLine, maxLines, charTruncated);

        // Record successful full cold reads in the session cache so subsequent attempts
        // on the same unchanged file are short-circuited with a "content unchanged" hint.
        // Partial reads (startLine > 1 or maxLines > 0) are not cached — agents requesting
        // specific ranges are actively paging and should continue to receive content.
        if (startLine <= 1 && maxLines <= 0)
            _sessionCache?.RecordRead(resolved, fileInfo);

        content = built;
        return null;
    }

    // Appends a navigation hint when the output is a partial view of the file.
    private static string AnnotateTypographicWarnings(string content, int effectiveStart, int endLine,
        int totalLines, int startLine, int maxLines, bool charTruncated)
    {
        bool lineTruncated = (maxLines > 0 && totalLines - effectiveStart + 1 > maxLines) || charTruncated;
        if (effectiveStart > 1 || lineTruncated)
        {
            var hint = charTruncated
                ? $"\n\n[TRUNCATED at char limit — showed lines {effectiveStart}–{endLine} of {totalLines}. " +
                  $"Use startLine={endLine + 1} to continue reading.]"
                : lineTruncated
                    ? $"\n\n[Showing lines {effectiveStart}–{endLine} of {totalLines}. " +
                      $"Use startLine={endLine + 1} to read the next section.]"
                    : $"\n\n[Showing lines {effectiveStart}–{endLine} of {totalLines}.]";
            content += hint;
        }
        return content;
    }

    [Description("Replace exact oldText with newText. Preferred over write_file for edits.")]
    public async Task<string> PatchFileAsync(
        [Description("File path.")] string path,
        [Description("Exact text to replace.")] string oldText,
        [Description("Replacement text.")] string newText)
    {
        if (string.IsNullOrEmpty(oldText))
            return PluginResult.Error("oldText must not be empty.");

        var denial = FileSystemSandbox.ResolveSafe(path, _sandboxRoot, _exemptedPrefixes, out var resolved);
        if (denial is not null) return denial;

        if (!File.Exists(resolved))
            return PluginResult.Error($"File not found: {resolved}");

        var content = await File.ReadAllTextAsync(resolved);
        var ext     = Path.GetExtension(resolved).ToLowerInvariant();

        // Apply the same normalisations WriteFileAsync applies so that patch arguments are
        // consistent with what was actually written to disk.  Without this, over-escaped
        // quotes (\" instead of ") silently prevent the match even though the file and
        // oldText look identical when printed.
        oldText = FilePatchDiffing.NormalizePatchText(oldText, ext);
        newText = FilePatchDiffing.NormalizePatchText(newText, ext);

        // Normalise line endings in both the file content and the search text so that
        // \r\n / \n mismatches from tool-call JSON serialisation don't cause false misses.
        var normalContent = content.Replace("\r\n", "\n");
        var normalOld     = oldText.Replace("\r\n", "\n");
        var normalNew     = newText.Replace("\r\n", "\n");

        var idx = normalContent.IndexOf(normalOld, StringComparison.Ordinal);
        if (idx < 0)
        {
            // Release the write-once lock so write_file can serve as a recovery path.
            // Keeping the lock when oldText is not found leaves the agent with no valid
            // exit: patch_file cannot match, write_file is blocked, and the turn deadlocks.
            _patchedThisTurn.Remove(resolved);

            // Give the agent enough information to correct itself without a full re-read.
            var lineHint     = FilePatchDiffing.CountLines(normalContent, normalOld);
            var mismatchHint = FilePatchDiffing.FindFirstMismatchingLine(normalContent, normalOld);
            var excerpt      = FilePatchDiffing.ExtractExcerpt(normalContent, normalOld, contextLines: 8);
            var excerptNote  = excerpt.Length > 0
                ? $"\nNearest content in file:\n{excerpt}\n"
                : string.Empty;
            return PluginResult.Error(
                $"oldText not found in '{resolved}'. " +
                $"The text must match exactly including whitespace, indentation, and line endings. " +
                $"{lineHint}" +
                $"{mismatchHint}" +
                $"{excerptNote}" +
                $"Read the file with read_file to get exact text before retrying patch_file.");
        }

        // Reject ambiguous matches — require the search string to be unique.
        var secondIdx = normalContent.IndexOf(normalOld, idx + 1, StringComparison.Ordinal);
        if (secondIdx >= 0)
            return PluginResult.Error(
                $"oldText appears more than once in '{resolved}'. " +
                $"Include more surrounding lines in oldText to make it unique.");

        // Splice entirely in normalised (LF-only) space so that idx and normalOld.Length
        // stay in sync with the string being sliced.  Using the original CRLF content here
        // would drift: each \r\n before the match adds one extra character to content that
        // isn't present in normalContent, shifting the prefix/suffix boundaries and
        // corrupting the file.
        var patched = normalContent[..idx]
            + normalNew
            + normalContent[(idx + normalOld.Length)..];

        // Re-expand to CRLF when the file was originally Windows-style.
        if (content.Contains("\r\n"))
            patched = patched.Replace("\n", "\r\n");

        await File.WriteAllTextAsync(resolved, patched);

        // Invalidate caches — content has changed.
        _readThisTurn.Remove(resolved);
        _sessionCache?.Invalidate(resolved);
        var patchSp = FileSystemSandbox.SummaryPath(resolved, _summaryDir);
        if (File.Exists(patchSp)) File.Delete(patchSp);

        // Record that this path was patched so write_file can detect the pattern.
        _patchedThisTurn.Add(resolved);
        _onWrite?.Invoke();

        var oldLines = normalOld.Split('\n').Length;
        var newLines = normalNew.Split('\n').Length;
        return PluginResult.Ok(
            $"Patched '{resolved}': replaced {oldLines}-line block with {newLines}-line block " +
            $"at character offset {idx}.");
    }

    private static string FormatTimeAgo(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 60)  return $"{(int)elapsed.TotalSeconds}s";
        if (elapsed.TotalMinutes < 60)  return $"{(int)elapsed.TotalMinutes}m";
        return $"{elapsed.TotalHours:F1}h";
    }

    [Description("Create or overwrite a file. Prefer patch_file for edits on large files.")]
    public async Task<string> WriteFileAsync(
        [Description("File path.")] string path,
        [Description("File content.")] string content,
        [Description("Skip escape-sequence normalisation.")] bool raw = false,
        [Description("Expected current version (0 = skip check). Write fails with VERSION_MISMATCH when the file has been modified since this version was read.")] int baseVersion = 0)
    {
        if (content is null)
            return PluginResult.Error(
                "The 'content' parameter is required but was not provided. Pass the file text as 'content' separately.");

        var pathDenial = ValidateWritePath(path, out var resolved);
        if (pathDenial is not null) return pathDenial;

        var versionDenial = await CheckVersionConflictAsync(resolved!, baseVersion);
        if (versionDenial is not null) return versionDenial;

        var truncationDenial = await FilePatchDiffing.EnsureFileExistsAsync(resolved!, content);
        if (truncationDenial is not null) return truncationDenial;

        var ext = Path.GetExtension(resolved!).ToLowerInvariant();

        var diffDenial = FilePatchDiffing.ComputeAndReportDiff(resolved!, content, ext, raw, out content, out bool normalised);
        if (diffDenial is not null) return diffDenial;

        return await CommitWriteAsync(resolved!, content, normalised);
    }

    // Validates the path argument: checks for embedded newlines, resolves through the sandbox,
    // and blocks writes to paths that were already patch_file'd this turn.
    // Returns a denial string on failure, or null on success (resolved is set via out parameter).
    private string? ValidateWritePath(string path, out string? resolved)
    {
        resolved = null;

        // Guard against models that accidentally embed file content in the path argument
        // (e.g. passing "my/file.go\npackage main\n..." as the path). A valid path never
        // contains newline characters; anything after the first newline is almost certainly
        // file content that belongs in the content parameter instead.
        if (path.Contains('\n') || path.Contains('\r'))
            return PluginResult.Error(
                "The 'path' argument contains a newline character, which is not valid in a " +
                "file path. Did you accidentally include file content in the path? " +
                "Pass the file path as 'path' and the file text as 'content' separately.");

        var denial = FileSystemSandbox.ResolveSafe(path, _sandboxRoot, _exemptedPrefixes, out var r);
        if (denial is not null) return denial;
        resolved = r;

        // Block write_file on a path that was already patch_file'd this turn. The agent's
        // full-file content is derived from its pre-patch mental model and would silently
        // overwrite the patch that was just applied.
        if (_patchedThisTurn.Contains(resolved))
            return PluginResult.Error(
                $"WRITE BLOCKED — '{resolved}' was already patched this turn. " +
                $"Calling write_file now would overwrite that patch with stale content. " +
                $"Use patch_file again for any additional edits.");

        return null;
    }

    // Version conflict check: when baseVersion > 0, reject the write if the current
    // stored version differs so agents cannot silently overwrite concurrent changes.
    // Returns an error string on conflict, or null when the check passes.
    private async Task<string?> CheckVersionConflictAsync(string resolved, int baseVersion)
    {
        if (baseVersion > 0 && _versionStore is not null)
        {
            var currentVersion = await _versionStore.GetVersionAsync(resolved);
            if (currentVersion != baseVersion)
                return PluginResult.Error(
                    $"VERSION_MISMATCH: '{resolved}' is at version {currentVersion} " +
                    $"but baseVersion={baseVersion} was supplied. " +
                    $"Call get_file_info to read the current version, then reissue the write with the correct baseVersion.");
        }
        return null;
    }

    // Writes content to disk, invalidates caches, bumps the version store, and returns the
    // success result string.
    private async Task<string> CommitWriteAsync(string resolved, string content, bool normalised)
    {
        var dir = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(resolved, content);

        // Allow a within-turn verification read by removing from the per-turn set.
        // Prime the session cache (ReadCount:0) so later-turn reads get a "was written"
        // hint instead of re-injecting the full content into context.
        // _writtenThisTurn suppresses the session-cache check for the first within-turn
        // read so agents can still verify the content they just wrote.
        _readThisTurn.Remove(resolved);
        _writtenThisTurn.Add(resolved);
        _sessionCache?.RecordWrite(resolved, new FileInfo(resolved));

        // Bump the version store so get_file_info and future baseVersion checks stay accurate.
        int? newVersion = null;
        if (_versionStore is not null)
        {
            var hash = FileVersionStore.HashContent(content);
            newVersion = await _versionStore.BumpVersionAsync(resolved, hash);
        }

        var writeSp = FileSystemSandbox.SummaryPath(resolved, _summaryDir);
        if (File.Exists(writeSp)) File.Delete(writeSp);

        var note = normalised
            ? $" (content was normalised: code fences or over-escaped quotes were stripped)"
            : string.Empty;
        var versionNote = newVersion.HasValue ? $" [v{newVersion}]" : string.Empty;
        _onWrite?.Invoke();
        return PluginResult.Ok($"Written {content.Length} chars to {resolved}{note}{versionNote}");
    }

}
