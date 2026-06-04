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
/// </summary>
public sealed class FileSystemPlugin : ITurnResettable
{
    // Canonical form of the sandbox root, or null when unrestricted.
    private readonly string? _sandboxRoot;
    private readonly int     _readFileSizeLimit;
    private readonly string  _summaryDir;
    private readonly FileVersionStore?  _versionStore;
    private readonly SessionReadCache?  _sessionCache;
    private readonly Action?            _onWrite;

    // Per-turn read cache: cleared at the start of each agent turn so re-reading the same
    // file within a single turn is caught and short-circuited before dumping redundant
    // content into the model's context.
    private readonly HashSet<string> _readThisTurn = new(StringComparer.OrdinalIgnoreCase);

    // Paths that were successfully patch_file'd this turn. A write_file to any of these
    // paths is blocked: the write is derived from the agent's stale mental model, not the
    // current disk state, so it would silently clobber the patch that was just applied.
    private readonly HashSet<string> _patchedThisTurn = new(StringComparer.OrdinalIgnoreCase);

    // Per-turn cumulative read budget (chars). Prevents individual tool calls from
    // individually respecting the per-call size limit while still collectively flooding
    // the in-turn context with hundreds of thousands of chars of file content — the
    // primary cause of 400k+ input-token turns. Cleared by BeginTurn().
    // Default: 150,000 chars ≈ 37,500 tokens. Agents making >N large file reads per turn
    // receive a "budget exhausted" error and must proceed with the context they have.
    private int _readBudgetUsed;
    private readonly int _readBudgetPerTurn;

    // Pre-read byte threshold: if the file exceeds this, stream just the first 30 lines +
    // a line count for the preview instead of allocating a full string array.
    private const int LargeFileByteThreshold = 25_000;
    // maxLines values larger than this are treated as cold reads — an agent passing
    // maxLines: 99999 is asking for everything and should be gated the same as omitting it.
    private const int LargeFileColdReadLines  = 500;

    public FileSystemPlugin(string? sandboxRoot = null, int readFileSizeLimit = 20_000, int readBudgetPerTurn = 150_000, FileVersionStore? versionStore = null, SessionReadCache? sessionCache = null, Action? onWrite = null)
    {
        _sandboxRoot       = sandboxRoot is not null ? FuseraftPaths.ExpandPath(sandboxRoot) : null;
        _readFileSizeLimit = readFileSizeLimit > 0 ? readFileSizeLimit : 20_000;
        _readBudgetPerTurn = readBudgetPerTurn > 0 ? readBudgetPerTurn : 150_000;
        var baseDir        = _sandboxRoot ?? Directory.GetCurrentDirectory();
        _summaryDir        = Path.Combine(baseDir, ".fuseraft", "summaries");
        _versionStore      = versionStore;
        _sessionCache      = sessionCache;
        _onWrite           = onWrite;
    }

    /// <inheritdoc cref="ITurnResettable.BeginTurn"/>
    void ITurnResettable.BeginTurn()
    {
        _readThisTurn.Clear();
        _patchedThisTurn.Clear();
        _readBudgetUsed = 0;
    }

    [Description("Read text file content. Use startLine+maxLines for large files. Binary files rejected.")]
    public async Task<string> ReadFileAsync(
        [Description("File path.")] string path,
        [Description("1-based start line.")] int startLine = 1,
        [Description("Max lines to return.")] int maxLines = 0)
    {
        var denial = ResolveSafe(path, out var resolved);
        if (denial is not null) return denial;

        if (!File.Exists(resolved))
            return PluginResult.Error($"File not found: {resolved}");

        // Compute FileInfo once — used by the session cache check and the cold-read gate.
        var fileInfo = new FileInfo(resolved);

        // Session-level read cache: if the file is in the cache and unchanged on disk
        // (matching mtime + size), return a hint instead of re-dumping the full content.
        // Only fires on cold reads (no startLine/maxLines override), same condition as the
        // per-turn cache below. After compaction the content may no longer be in context,
        // so agents can pass startLine/maxLines to force a targeted re-read.
        if (startLine <= 1 && maxLines <= 0 && _sessionCache is not null
            && _sessionCache.TryGetHit(resolved, fileInfo, out var cacheHit))
        {
            var ago   = FormatTimeAgo(DateTime.UtcNow - cacheHit!.LastReadUtc);
            var times = cacheHit.ReadCount == 1 ? "once" : $"{cacheHit.ReadCount} times";
            return PluginResult.Info(
                $"'{resolved}' has not changed since it was last read this session " +
                $"({times}, {ago} ago). Content from that read is in your conversation " +
                $"history (unless compacted away). Use grep_in_file to locate a specific " +
                $"section, or pass startLine/maxLines to force a targeted re-read.");
        }

        // Turn-level read cache — identical file reads within one agent turn return a short
        // reminder instead of re-dumping the full content into context. The cache is cleared
        // by ITurnResettable.BeginTurn() at the start of each agent turn.
        // Reads with a non-default range (startLine > 1 or maxLines > 0) bypass the cache
        // so agents can page through a file in sections.
        if (startLine <= 1 && maxLines <= 0 && !_readThisTurn.Add(resolved))
            return PluginResult.Info(
                $"'{resolved}' already read this turn — content is in context. " +
                $"Use grep_in_file to locate a section, then read_file with startLine/maxLines for a targeted excerpt.");

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

        // Cold-read gate: fires when no meaningful maxLines cap is set ("give me everything"),
        // regardless of startLine — a large file requested from line 2 with no cap is just as
        // expensive as one from line 1. Byte pre-check avoids allocating a full string array
        // for a file we're about to redirect.
        bool isColdRead = maxLines <= 0 || maxLines > LargeFileColdReadLines;
        if (isColdRead && fileInfo.Length > LargeFileByteThreshold)
        {
            var (coldLines, coldLineCount, coldSizeBytes) = await StreamPreviewLinesAsync(resolved, 30);
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

        var allLines   = await File.ReadAllLinesAsync(resolved);
        var totalLines = allLines.Length;

        if (effectiveStart > totalLines)
            return PluginResult.Error(
                $"startLine {effectiveStart} exceeds file length ({totalLines} lines): {resolved}");

        // Slice to the requested range (convert to 0-based index).
        var slice = allLines.AsSpan(effectiveStart - 1);
        if (maxLines > 0 && slice.Length > maxLines)
            slice = slice[..maxLines];

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
        var content = sb.ToString();

        // Per-turn read budget: reject this read if adding its content would exceed the
        // cumulative char limit for this turn. Large numbers of file reads is the primary
        // driver of 400k+ input-token turns — once the budget is hit, the agent must
        // proceed with what it already has in context rather than reading more files.
        if (_readBudgetUsed + content.Length > _readBudgetPerTurn)
            return PluginResult.Error(
                $"Read budget exhausted ({_readBudgetUsed:N0}/{_readBudgetPerTurn:N0} chars). " +
                $"Proceed with context already available — use patch_file or shell_run. Budget resets next turn.");

        _readBudgetUsed += content.Length;

        // Append a navigation hint when the output is a partial view of the file.
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

        // Record successful full cold reads in the session cache so subsequent attempts
        // on the same unchanged file are short-circuited with a "content unchanged" hint.
        // Partial reads (startLine > 1 or maxLines > 0) are not cached — agents requesting
        // specific ranges are actively paging and should continue to receive content.
        if (startLine <= 1 && maxLines <= 0)
            _sessionCache?.RecordRead(resolved, fileInfo);

        return content;
    }

    [Description("Search a file (grep). Cheaper than full read_file.")]
    public async Task<string> GrepFileAsync(
        [Description("File path.")] string path,
        [Description("Text or regex pattern.")] string pattern,
        [Description("Context lines around match.")] int contextLines = 2,
        [Description("Max matches.")] int maxMatches = 30,
        CancellationToken cancellationToken = default)
    {
        var denial = ResolveSafe(path, out var resolved);
        if (denial is not null) return denial;

        if (!File.Exists(resolved))
            return PluginResult.Error($"File not found: {resolved}");

        // Some models HTML-encode characters in tool arguments (e.g. &lt; for <).
        pattern = System.Net.WebUtility.HtmlDecode(pattern);

        System.Text.RegularExpressions.Regex regex;
        try
        {
            regex = new System.Text.RegularExpressions.Regex(
                pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Multiline,
                TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException ex)
        {
            return PluginResult.Error($"Invalid pattern '{pattern}': {ex.Message}");
        }

        var ctx        = Math.Max(0, contextLines);
        var sb         = new System.Text.StringBuilder();
        int matches    = 0;
        int lineNumber = 0;
        int lastOutput = -1;
        int postCtxLeft = 0;
        var preCtxBuf  = new Queue<(int Num, string Text)>();

        using (var reader = new StreamReader(resolved))
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineNumber++;

                if (matches >= maxMatches) continue; // drain to count total lines

                if (regex.IsMatch(line))
                {
                    matches++;

                    // Separator if there is a gap before the pre-context window.
                    var firstPre = preCtxBuf.Count > 0 ? preCtxBuf.Peek().Num : lineNumber;
                    if (sb.Length > 0 && firstPre > lastOutput + 1)
                        sb.AppendLine("  ---");

                    foreach (var (n, t) in preCtxBuf)
                    {
                        sb.AppendLine($"{n,6}: {t}");
                        lastOutput = n;
                    }
                    preCtxBuf.Clear();

                    sb.AppendLine($"{lineNumber,6}: {line}");
                    lastOutput  = lineNumber;
                    postCtxLeft = ctx;
                }
                else if (postCtxLeft > 0)
                {
                    sb.AppendLine($"{lineNumber,6}: {line}");
                    lastOutput = lineNumber;
                    postCtxLeft--;
                }
                else
                {
                    preCtxBuf.Enqueue((lineNumber, line));
                    if (preCtxBuf.Count > ctx) preCtxBuf.Dequeue();
                }
            }
        }

        if (matches == 0)
            return PluginResult.Info($"No matches for '{pattern}' in {resolved}");

        var header = $"[{matches} match(s) in {resolved} ({lineNumber} lines total)]\n";
        if (matches >= maxMatches)
            header += $"[Result capped at {maxMatches} matches — use a more specific pattern to narrow results.]\n";

        return header + sb.ToString().TrimEnd();
    }

    [Description("Replace exact oldText with newText. Preferred over write_file for edits.")]
    public async Task<string> PatchFileAsync(
        [Description("File path.")] string path,
        [Description("Exact text to replace.")] string oldText,
        [Description("Replacement text.")] string newText)
    {
        if (string.IsNullOrEmpty(oldText))
            return PluginResult.Error("oldText must not be empty.");

        var denial = ResolveSafe(path, out var resolved);
        if (denial is not null) return denial;

        if (!File.Exists(resolved))
            return PluginResult.Error($"File not found: {resolved}");

        var content = await File.ReadAllTextAsync(resolved);
        var ext     = Path.GetExtension(resolved).ToLowerInvariant();

        // Apply the same normalisations WriteFileAsync applies so that patch arguments are
        // consistent with what was actually written to disk.  Without this, over-escaped
        // quotes (\" instead of ") silently prevent the match even though the file and
        // oldText look identical when printed.
        oldText = NormalizePatchText(oldText, ext);
        newText = NormalizePatchText(newText, ext);

        // Normalise line endings in both the file content and the search text so that
        // \r\n / \n mismatches from tool-call JSON serialisation don't cause false misses.
        var normalContent = content.Replace("\r\n", "\n");
        var normalOld     = oldText.Replace("\r\n", "\n");
        var normalNew     = newText.Replace("\r\n", "\n");

        var idx = normalContent.IndexOf(normalOld, StringComparison.Ordinal);
        if (idx < 0)
        {
            // Give the agent enough information to correct itself without a full re-read.
            var lineHint     = CountLines(normalContent, normalOld);
            var mismatchHint = FindFirstMismatchingLine(normalContent, normalOld);
            return PluginResult.Error(
                $"oldText not found in '{resolved}'. " +
                $"The text must match exactly including whitespace, indentation, and line endings. " +
                $"{lineHint}" +
                $"{mismatchHint}" +
                $"Use grep_in_file to locate the exact text, then copy it verbatim as oldText.");
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

        // Invalidate both caches — content has changed.
        _readThisTurn.Remove(resolved);
        _sessionCache?.Invalidate(resolved);

        // Record that this path was patched so write_file can detect the pattern.
        _patchedThisTurn.Add(resolved);
        _onWrite?.Invoke();

        var oldLines = normalOld.Split('\n').Length;
        var newLines = normalNew.Split('\n').Length;
        return PluginResult.Ok(
            $"Patched '{resolved}': replaced {oldLines}-line block with {newLines}-line block " +
            $"at character offset {idx}.");
    }

    private static string CountLines(string content, string searchText)
    {
        // Try to find the first line of the search text in the file for a useful hint.
        var firstSearchLine = searchText.Split('\n')[0].Trim();
        if (string.IsNullOrEmpty(firstSearchLine)) return string.Empty;

        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(firstSearchLine, StringComparison.Ordinal))
                return $"The first line of oldText ('{firstSearchLine}') was found near line {i + 1} — " +
                       $"check surrounding whitespace or indentation. ";
        }
        return string.Empty;
    }

    // Applies the same text normalisations WriteFileAsync applies so that oldText / newText
    // in a patch call are consistent with what is actually on disk.
    private static string NormalizePatchText(string text, string ext)
    {
        // Quote normalisation: LLMs sometimes over-escape " as \" in tool-call JSON. The
        // written file has bare ", so oldText must also have bare " or the match fails.
        if (QuoteNormalizeExtensions.Contains(ext) && text.Contains("\\\""))
            text = text.Replace("\\\"", "\"");

        // Escape-sequence expansion: only expand when there are no real newlines but
        // literal \n sequences are present — same heuristic as WriteFileAsync.
        if (!text.Contains('\n') && !text.Contains('\r') && text.Contains("\\n"))
            text = text
                .Replace("\\r\\n", "\r\n")
                .Replace("\\n",    "\n")
                .Replace("\\t",    "\t");

        return text;
    }

    // When the first line of searchText can be located in fileContent but a subsequent
    // line diverges, returns a hint identifying the first mismatching line so the agent
    // can correct oldText without a full re-read.
    private static string FindFirstMismatchingLine(string fileContent, string searchText)
    {
        var searchLines = searchText.Split('\n');
        var fileLines   = fileContent.Split('\n');

        if (searchLines.Length <= 1) return string.Empty;

        var firstLine = searchLines[0];
        for (int i = 0; i <= fileLines.Length - searchLines.Length; i++)
        {
            if (fileLines[i] != firstLine) continue;

            for (int j = 1; j < searchLines.Length; j++)
            {
                if (fileLines[i + j] == searchLines[j]) continue;

                return $"Line {j + 1} of oldText ('{Truncate(searchLines[j])}') " +
                       $"does not match file line {i + j + 1} ('{Truncate(fileLines[i + j])}'). ";
            }
        }

        return string.Empty;
    }

    private static string Truncate(string s, int max = 60)
        => s.Length <= max ? s : s[..max] + "…";

    private static string FormatTimeAgo(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 60)  return $"{(int)elapsed.TotalSeconds}s";
        if (elapsed.TotalMinutes < 60)  return $"{(int)elapsed.TotalMinutes}m";
        return $"{elapsed.TotalHours:F1}h";
    }

    // Extensions where a literal \" in the file is almost never intentional.
    // LLMs frequently over-escape quote characters in these languages (writing \" when
    // they mean "), producing syntax errors like `\"\"\"docstring\"\"\"` or
    // `f\"{x}\"`.  Normalising before write prevents the agent needing multiple
    // correction turns just to fix tooling-layer escaping artifacts.
    // C / C++ / C# / Rust are intentionally excluded because \" is a valid and common
    // string-escape sequence in those languages.
    private static readonly HashSet<string> QuoteNormalizeExtensions =
        [".py", ".js", ".ts", ".jsx", ".tsx", ".rb", ".sh", ".bash", ".zsh",
         ".lua", ".pl", ".r", ".swift", ".kt", ".scala", ".ex", ".exs", ".kiwi"];

    [Description("Get file version, size, and last-modified. Cheaper than read_file. Returns VERSION_NOT_TRACKED when the file exists but was not written through write_file.")]
    public async Task<string> StatFileAsync(
        [Description("File path.")] string path)
    {
        var denial = ResolveSafe(path, out var resolved);
        if (denial is not null) return denial;

        if (!File.Exists(resolved))
            return PluginResult.Error($"File not found: {resolved}");

        var info = new FileInfo(resolved);
        var size = info.Length;
        var mtime = info.LastWriteTimeUtc;

        if (_versionStore is not null)
        {
            var record = await _versionStore.StatAsync(resolved);
            if (record is not null)
                return PluginResult.Ok(
                    $"path={resolved} version={record.Version} " +
                    $"size={size} modified={mtime:O} hash={record.ContentHash ?? "(none)"}");
        }

        return PluginResult.Ok(
            $"path={resolved} version=NOT_TRACKED size={size} modified={mtime:O}");
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

        // Guard against models that accidentally embed file content in the path argument
        // (e.g. passing "my/file.go\npackage main\n..." as the path). A valid path never
        // contains newline characters; anything after the first newline is almost certainly
        // file content that belongs in the content parameter instead.
        if (path.Contains('\n') || path.Contains('\r'))
            return PluginResult.Error(
                "The 'path' argument contains a newline character, which is not valid in a " +
                "file path. Did you accidentally include file content in the path? " +
                "Pass the file path as 'path' and the file text as 'content' separately.");

        var denial = ResolveSafe(path, out var resolved);
        if (denial is not null) return denial;

        // Block write_file on a path that was already patch_file'd this turn. The agent's
        // full-file content is derived from its pre-patch mental model and would silently
        // overwrite the patch that was just applied.
        if (_patchedThisTurn.Contains(resolved))
            return PluginResult.Error(
                $"WRITE BLOCKED — '{resolved}' was already patched this turn. " +
                $"Calling write_file now would overwrite that patch with stale content. " +
                $"Use patch_file again for any additional edits.");

        // Version conflict check: when baseVersion > 0, reject the write if the current
        // stored version differs so agents cannot silently overwrite concurrent changes.
        if (baseVersion > 0 && _versionStore is not null)
        {
            var currentVersion = await _versionStore.GetVersionAsync(resolved);
            if (currentVersion != baseVersion)
                return PluginResult.Error(
                    $"VERSION_MISMATCH: '{resolved}' is at version {currentVersion} " +
                    $"but baseVersion={baseVersion} was supplied. " +
                    $"Call stat_file to read the current version, then reissue the write with the correct baseVersion.");
        }

        // Guard against model output truncation on large existing files.
        // When a model tries to write a file that is substantially larger on disk than the
        // content it is providing, the content is almost certainly truncated — the model ran
        // out of output tokens before finishing the file. Writing truncated content silently
        // would corrupt the file. Instead, return an error so the agent knows to use a
        // targeted edit tool (sed -i, or shell_run with a patch) rather than a full rewrite.
        //
        // Threshold: if the existing file is > 50 lines AND the new content has fewer than
        // 60 % of the existing line count, reject the write.
        if (File.Exists(resolved))
        {
            int existingLines = 0;
            await foreach (var _ in File.ReadLinesAsync(resolved)) existingLines++;
            var newLines = content.Split('\n').Length;
            if (existingLines > 50 && newLines < existingLines * 0.6)
                return PluginResult.Error(
                    $"WRITE BLOCKED — truncation guard: '{resolved}' currently has {existingLines} lines " +
                    $"but the content you provided has only {newLines} lines " +
                    $"({(double)newLines / existingLines:P0} of the original). " +
                    $"This almost always means your output was truncated before you finished writing the file.\n\n" +
                    $"DO NOT use write_file to rewrite large files. Instead, make targeted changes:\n" +
                    $"  • Use patch_file(path, oldText, newText) to replace an exact block — " +
                    $"this is the preferred approach for source-code edits.\n" +
                    $"  • Example: patch_file(\"{resolved}\", \"    Include,\\n\", \"    Include,\\n    ModuleIncludeAssign,\\n\")\n" +
                    $"  • Alternatively: shell_run with sed -i to insert/replace specific lines.\n" +
                    $"This approach is safer and avoids the token-limit truncation problem.");
        }

        var ext = Path.GetExtension(resolved).ToLowerInvariant();
        bool normalised = false;

        // Quote normalisation runs unconditionally for known extensions — it corrects a
        // JSON serialisation artifact (model double-escaping " as \") and must not be
        // skipped even when raw=true, which only controls escape-sequence expansion.
        if (QuoteNormalizeExtensions.Contains(ext) && content.Contains("\\\""))
        {
            content    = content.Replace("\\\"", "\"");
            normalised = true;
        }

        if (raw) goto write;

        // For .json files, normalise common LLM wrapping artifacts before writing.
        if (ext == ".json")
        {
            // Guard against blank/whitespace-only content — the model probably forgot
            // to include the content argument.  Returning an error here is cheaper than
            // a successful write that immediately fails downstream JSON validation.
            if (string.IsNullOrWhiteSpace(content))
                return PluginResult.Error(
                    "The 'content' argument is empty. Did you forget to include the JSON content? " +
                    "Pass the full JSON object as the 'content' parameter.");

            var trimmed = content.TrimStart();

            // Strip markdown code fences (```json ... ``` or ``` ... ```).
            // A valid JSON file should never start with ``` — strip the fence and trailing
            // ``` so the file contains only the raw JSON object/array.
            if (trimmed.StartsWith("```"))
            {
                // Skip the opening fence line (```json, ```, etc.)
                var firstNewline = trimmed.IndexOf('\n');
                if (firstNewline >= 0)
                    trimmed = trimmed[(firstNewline + 1)..];
                // Strip the closing ```
                var lastFence = trimmed.LastIndexOf("```");
                if (lastFence >= 0)
                    trimmed = trimmed[..lastFence];
                content   = trimmed.Trim();
                normalised = true;
            }
            // Strip XML <parameter name="content">…</parameter> wrappers.
            // Some models emit tool-call XML artifacts as literal content, e.g.:
            //   <parameter name="content">{"goal": ...}</parameter>
            // Extract just the inner text so the file contains valid JSON.
            else if (trimmed.StartsWith("<parameter", StringComparison.OrdinalIgnoreCase))
            {
                var closeTag = trimmed.IndexOf('>');
                if (closeTag >= 0)
                {
                    var inner = trimmed[(closeTag + 1)..];
                    var endTag = inner.LastIndexOf("</parameter>", StringComparison.OrdinalIgnoreCase);
                    if (endTag >= 0) inner = inner[..endTag];
                    content   = inner.Trim();
                    normalised = true;
                }
            }
        }

        // Detect double-escaped newlines: when a model constructs the tool-call JSON
        // argument by hand, it sometimes writes \\n instead of a real newline, so after
        // JSON deserialization the content string contains literal \n (backslash-n) rather
        // than actual newline characters. The tell-tale sign is a file with zero real
        // newlines but multiple literal \n sequences — replace them so the written file has
        // proper line endings instead of collapsing to a single line of escape sequences.
        if (!content.Contains('\n') && !content.Contains('\r') && content.Contains("\\n"))
        {
            content = content
                .Replace("\\r\\n", "\r\n")
                .Replace("\\n", "\n")
                .Replace("\\t", "\t");
            normalised = true;
        }

        write:
        var dir = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(resolved, content);

        // Invalidate both caches — content has changed so a subsequent read_file call should
        // return the new content, not a cache-hit message.
        _readThisTurn.Remove(resolved);
        _sessionCache?.Invalidate(resolved);

        // Bump the version store so stat_file and future baseVersion checks stay accurate.
        int? newVersion = null;
        if (_versionStore is not null)
        {
            var hash = FileVersionStore.HashContent(content);
            newVersion = await _versionStore.BumpVersionAsync(resolved, hash);
        }

        var note = normalised
            ? $" (content was normalised: code fences or over-escaped quotes were stripped)"
            : string.Empty;
        var versionNote = newVersion.HasValue ? $" [v{newVersion}]" : string.Empty;
        _onWrite?.Invoke();
        return PluginResult.Ok($"Written {content.Length} chars to {resolved}{note}{versionNote}");
    }

    [Description("List files recursively (max 500).")]
    public string ListFiles(
        [Description("Directory path.")] string directory,
        [Description("Glob pattern, e.g. '*.cs'.")] string pattern = "*")
    {
        var denial = ResolveSafe(directory, out var resolved);
        if (denial is not null) return denial;

        if (!Directory.Exists(resolved))
            return PluginResult.Error($"Directory not found: {resolved}");

        const int maxFiles = 500;
        var sep = Path.DirectorySeparatorChar;
        string[] ignoredDirs = [".git", "node_modules", "bin", "obj", ".vs", ".idea", ".nuget", ".venv", "__pycache__", ".fuseraft"];
        var files = Directory.EnumerateFiles(resolved, pattern, SearchOption.AllDirectories)
            .Where(f => !ignoredDirs.Any(d => f.Contains($"{sep}{d}{sep}") || f.EndsWith($"{sep}{d}")))
            .Take(maxFiles + 1)
            .ToList();

        if (files.Count == 0)
            return PluginResult.Info("No files matched.");

        var truncated = files.Count > maxFiles;
        if (truncated) files.RemoveAt(files.Count - 1);

        var result = string.Join("\n", files);
        if (truncated)
            result += $"\n\n[TRUNCATED — only first {maxFiles} files shown. Use a more specific pattern to narrow results.]";

        return result;
    }

    [Description("Delete a file.")]
    public string DeleteFile([Description("File path.")] string path)
    {
        var denial = ResolveSafe(path, out var resolved);
        if (denial is not null) return denial;

        if (!File.Exists(resolved))
            return PluginResult.Info($"File does not exist: {resolved}");

        File.Delete(resolved);
        return PluginResult.Ok($"Deleted: {resolved}");
    }

    [Description("Get file/directory metadata (size, timestamps, permissions).")]
    public string GetFileInfo([Description("File or directory path.")] string path)
    {
        var denial = ResolveSafe(path, out var resolved);
        if (denial is not null) return denial;

        var isFile = File.Exists(resolved);
        var isDir  = Directory.Exists(resolved);
        if (!isFile && !isDir)
            return PluginResult.Error($"Path not found: {resolved}");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Path:     {resolved}");
        sb.AppendLine($"Type:     {(isDir ? "directory" : "file")}");

        if (isFile)
        {
            var fi = new FileInfo(resolved);
            sb.AppendLine($"Size:     {fi.Length:N0} bytes");
            sb.AppendLine($"Created:  {fi.CreationTimeUtc:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"Modified: {fi.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} UTC");
        }
        else
        {
            var di = new DirectoryInfo(resolved);
            sb.AppendLine($"Created:  {di.CreationTimeUtc:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"Modified: {di.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} UTC");
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var mode    = File.GetUnixFileMode(resolved);
                var octal   = Convert.ToString((int)mode & 0777, 8).PadLeft(3, '0');
                var rwx     = new char[9];
                rwx[0] = mode.HasFlag(UnixFileMode.UserRead)      ? 'r' : '-';
                rwx[1] = mode.HasFlag(UnixFileMode.UserWrite)     ? 'w' : '-';
                rwx[2] = mode.HasFlag(UnixFileMode.UserExecute)   ? 'x' : '-';
                rwx[3] = mode.HasFlag(UnixFileMode.GroupRead)     ? 'r' : '-';
                rwx[4] = mode.HasFlag(UnixFileMode.GroupWrite)    ? 'w' : '-';
                rwx[5] = mode.HasFlag(UnixFileMode.GroupExecute)  ? 'x' : '-';
                rwx[6] = mode.HasFlag(UnixFileMode.OtherRead)     ? 'r' : '-';
                rwx[7] = mode.HasFlag(UnixFileMode.OtherWrite)    ? 'w' : '-';
                rwx[8] = mode.HasFlag(UnixFileMode.OtherExecute)  ? 'x' : '-';
                sb.AppendLine($"Permissions: {new string(rwx)} ({octal})");
            }
            catch { /* best effort — some virtual filesystems don't support GetUnixFileMode */ }
        }

        return sb.ToString().TrimEnd();
    }

    [Description("Set Unix file permissions (chmod). No-op on Windows.")]
    public string SetPermissions(
        [Description("File or directory path.")] string path,
        [Description("Octal mode, e.g. '755' or '644'.")] string mode)
    {
        if (OperatingSystem.IsWindows())
            return PluginResult.Info("SetPermissions is not supported on Windows.");

        if (string.IsNullOrWhiteSpace(mode) || !System.Text.RegularExpressions.Regex.IsMatch(mode, @"^[0-7]{3,4}$"))
            return PluginResult.Error($"Invalid mode '{mode}'. Supply a 3- or 4-digit octal string such as '755' or '0644'.");

        var denial = ResolveSafe(path, out var resolved);
        if (denial is not null) return denial;

        if (!File.Exists(resolved) && !Directory.Exists(resolved))
            return PluginResult.Error($"Path not found: {resolved}");

        try
        {
            var unixMode = (UnixFileMode)Convert.ToInt32(mode, 8);
            File.SetUnixFileMode(resolved, unixMode);
            return PluginResult.Ok($"Permissions set to {mode} on '{resolved}'.");
        }
        catch (Exception ex)
        {
            return PluginResult.Error($"Failed to set permissions: {ex.Message}");
        }
    }

    [Description("Create a directory (including parents).")]
    public string CreateDirectory([Description("Directory path.")] string path)
    {
        var denial = ResolveSafe(path, out var resolved);
        if (denial is not null) return denial;

        Directory.CreateDirectory(resolved);
        return PluginResult.Ok($"Directory ready: {resolved}");
    }

    [Description("Delete a directory.")]
    public string DeleteDirectory(
        [Description("Directory path.")] string path,
        [Description("Delete non-empty directories recursively.")] bool recursive = false)
    {
        var denial = ResolveSafe(path, out var resolved);
        if (denial is not null) return denial;

        if (!Directory.Exists(resolved))
            return PluginResult.Info($"Directory does not exist: {resolved}");

        // Refuse to delete the sandbox root itself.
        if (_sandboxRoot is not null)
        {
            var comparison   = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var sandboxCheck = _sandboxRoot.TrimEnd(Path.DirectorySeparatorChar);
            var resolvedCheck = resolved.TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(sandboxCheck, resolvedCheck, comparison))
                return PluginResult.Denied("Cannot delete the sandbox root directory.");
        }

        Directory.Delete(resolved, recursive);
        return PluginResult.Ok($"Deleted directory: {resolved}");
    }

    [Description("Copy a file.")]
    public async Task<string> CopyFileAsync(
        [Description("Source path.")] string source,
        [Description("Destination path.")] string destination,
        [Description("Overwrite if destination exists.")] bool overwrite = false)
    {
        var srcDenial = ResolveSafe(source, out var resolvedSrc);
        if (srcDenial is not null) return srcDenial;

        var dstDenial = ResolveSafe(destination, out var resolvedDst);
        if (dstDenial is not null) return dstDenial;

        if (!File.Exists(resolvedSrc))
            return PluginResult.Error($"Source not found: {resolvedSrc}");

        if (!overwrite && File.Exists(resolvedDst))
            return PluginResult.Error($"Destination already exists: {resolvedDst}. Set overwrite=true to replace it.");

        var dir = Path.GetDirectoryName(resolvedDst);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await Task.Run(() => File.Copy(resolvedSrc, resolvedDst, overwrite));
        return PluginResult.Ok($"Copied '{resolvedSrc}' → '{resolvedDst}'");
    }

    [Description("Move or rename a file or directory.")]
    public Task<string> MoveFileAsync(
        [Description("Source path.")] string source,
        [Description("Destination path.")] string destination,
        [Description("Overwrite if destination file exists.")] bool overwrite = false)
    {
        var srcDenial = ResolveSafe(source, out var resolvedSrc);
        if (srcDenial is not null) return Task.FromResult(srcDenial);

        var dstDenial = ResolveSafe(destination, out var resolvedDst);
        if (dstDenial is not null) return Task.FromResult(dstDenial);

        if (Directory.Exists(resolvedSrc))
        {
            if (Directory.Exists(resolvedDst))
                return Task.FromResult(PluginResult.Error($"Destination directory already exists: {resolvedDst}"));
            var dstParent = Path.GetDirectoryName(resolvedDst);
            if (!string.IsNullOrEmpty(dstParent)) Directory.CreateDirectory(dstParent);
            Directory.Move(resolvedSrc, resolvedDst);
            return Task.FromResult(PluginResult.Ok($"Moved directory '{resolvedSrc}' → '{resolvedDst}'"));
        }

        if (File.Exists(resolvedSrc))
        {
            if (!overwrite && File.Exists(resolvedDst))
                return Task.FromResult(PluginResult.Error($"Destination already exists: {resolvedDst}. Set overwrite=true to replace it."));
            var dstParent = Path.GetDirectoryName(resolvedDst);
            if (!string.IsNullOrEmpty(dstParent)) Directory.CreateDirectory(dstParent);
            File.Move(resolvedSrc, resolvedDst, overwrite);
            return Task.FromResult(PluginResult.Ok($"Moved '{resolvedSrc}' → '{resolvedDst}'"));
        }

        return Task.FromResult(PluginResult.Error($"Source not found: {resolvedSrc}"));
    }

    [Description("Get a cached summary or auto-preview of a file. Use before read_file on large files.")]
    public async Task<string> GetFileSummaryAsync(
        [Description("File path.")] string path)
    {
        var denial = ResolveSafe(path, out var resolved);
        if (denial is not null) return denial;

        if (!File.Exists(resolved))
            return PluginResult.Error($"File not found: {resolved}");

        // Check for a cached summary.
        var summaryPath = SummaryPath(resolved);
        if (File.Exists(summaryPath))
        {
            var cached = await File.ReadAllTextAsync(summaryPath);
            return $"[Cached summary for '{resolved}']\n{cached}";
        }

        // Auto-preview: first 30 lines + stats. For large files, stream rather than
        // allocating a full string array — same protection as ReadFileAsync's cold-read gate.
        var fileInfo = new FileInfo(resolved);
        string preview;
        string trailer;
        if (fileInfo.Length > LargeFileByteThreshold)
        {
            var (previewLines, totalLines, sizeBytes) = await StreamPreviewLinesAsync(resolved, 30);
            preview = string.Join('\n', previewLines);
            trailer = totalLines > 30
                ? $"\n\n[Auto-preview: showing first 30 of {totalLines:N0} lines ({sizeBytes:N0} bytes). " +
                  $"Use grep_in_file to locate specific content, or save_file_summary to store a " +
                  $"human-written summary for future turns.]"
                : $"\n\n[Full file — {totalLines} lines, {sizeBytes:N0} bytes.]";
        }
        else
        {
            var allLines  = await File.ReadAllLinesAsync(resolved);
            int lineCount = allLines.Length;
            long byteCount = fileInfo.Length;
            preview = string.Join('\n', allLines.Take(30));
            trailer = lineCount > 30
                ? $"\n\n[Auto-preview: showing first 30 of {lineCount} lines ({byteCount:N0} bytes). " +
                  $"Use grep_in_file to locate specific content, or save_file_summary to store a " +
                  $"human-written summary for future turns.]"
                : $"\n\n[Full file — {lineCount} lines, {byteCount:N0} bytes.]";
        }

        return preview + trailer;
    }

    [Description("Save a summary for future get_file_summary calls.")]
    public async Task<string> SaveFileSummaryAsync(
        [Description("File path.")] string path,
        [Description("Summary text.")] string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return PluginResult.Error("summary must not be empty.");

        var denial = ResolveSafe(path, out var resolved);
        if (denial is not null) return denial;

        Directory.CreateDirectory(_summaryDir);
        var summaryPath = SummaryPath(resolved);
        await File.WriteAllTextAsync(summaryPath, summary.Trim());

        return PluginResult.Ok($"Summary saved for '{resolved}' → {summaryPath}");
    }

    [Description("Check if a path exists.")]
    public string PathExists([Description("Path to check.")] string path)
    {
        var denial = ResolveSafe(path, out var resolved);
        if (denial is not null) return denial;

        bool exists = File.Exists(resolved) || Directory.Exists(resolved);
        return exists 
            ? PluginResult.Ok($"Exists: {resolved}")
            : PluginResult.Info($"Does not exist: {resolved}");
    }

    [Description("List files and subdirectories (non-recursive).")]
    public string ListDirectory(
        [Description("Directory path.")] string directory,
        [Description("Glob pattern, e.g. '*.cs'.")] string pattern = "*")
    {
        var denial = ResolveSafe(directory, out var resolved);
        if (denial is not null) return denial;

        if (!Directory.Exists(resolved))
            return PluginResult.Error($"Directory not found: {resolved}");

        const int maxEntries = 500;

        var dirs = Directory.EnumerateDirectories(resolved, pattern, SearchOption.TopDirectoryOnly)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => d + Path.DirectorySeparatorChar);

        var files = Directory.EnumerateFiles(resolved, pattern, SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        var entries = dirs.Concat(files).Take(maxEntries + 1).ToList();

        if (entries.Count == 0)
            return PluginResult.Info("No entries matched.");

        var truncated = entries.Count > maxEntries;
        if (truncated) entries.RemoveAt(entries.Count - 1);

        var result = string.Join("\n", entries);
        if (truncated)
            result += $"\n\n[TRUNCATED — only first {maxEntries} entries shown. Use a more specific pattern to narrow results.]";

        return result;
    }

    // Streams the first `previewCount` lines without allocating the full file into a string
    // array. Returns the preview lines, total line count, and file size in bytes.
    private static async Task<(List<string> Lines, int TotalLines, long SizeBytes)>
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

    private string SummaryPath(string resolvedFilePath)
    {
        // Derive a stable filename from the resolved path so the same file always maps to
        // the same summary regardless of how the agent specified it (relative vs absolute).
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(resolvedFilePath));
        var hex  = Convert.ToHexString(hash)[..16].ToLowerInvariant();
        return Path.Combine(_summaryDir, $"{hex}.md");
    }

    // Resolves 'path' to its canonical absolute form and checks it against the sandbox.
    // Returns a [DENIED] error string when the path escapes the sandbox, null when safe.
    private string? ResolveSafe(string path, out string resolved)
    {
        var expandedPath = ProcessHelper.ExpandHome(path);
        resolved = _sandboxRoot is not null && !Path.IsPathRooted(expandedPath)
            ? Path.GetFullPath(expandedPath, _sandboxRoot)
            : Path.GetFullPath(expandedPath);

        if (_sandboxRoot is null)
            return null;

        // Append the OS separator so that "/sandbox" is not treated as a prefix of "/sandboxExtra".
        var sandboxPrefix = _sandboxRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolvedCheck = resolved.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!resolvedCheck.StartsWith(sandboxPrefix, comparison))
            return PluginResult.Denied($"Path '{resolved}' is outside the configured sandbox '{_sandboxRoot}'.");

        return null;
    }
}
