using System.ComponentModel;
using fuseraft.Core;
using fuseraft.Infrastructure;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Directory/file-management and read-only inspection tools for the "FileSystem" tool
/// surface — the stateless-per-turn half of what agents call alongside
/// <see cref="FileSystemPlugin"/>'s read/patch/write pipeline. Registered as a second object
/// under the "FileSystem" plugin name (see <c>PluginRegistry.RegisterAdditional</c>), so its
/// tool names stay unprefixed (<c>list_files</c>, not
/// <c>file_system_management_ops_list_files</c> — see <c>PluginRegistry.NoPrefixPlugins</c>).
///
/// Shares <see cref="FileSystemPlugin"/>'s per-turn read/write/patch <see cref="HashSet{T}"/>
/// instances by reference (constructor-injected from the owning instance) so
/// <see cref="FileSystemSandbox.InvalidatePathAsync"/> clears entries the read/write pipeline
/// added, and a single <c>FileSystemPlugin.BeginTurn()</c> resets both objects' view of
/// per-turn state together — this class does not implement <c>ITurnResettable</c> itself since
/// it owns no state, only borrowed references.
/// </summary>
internal sealed class FileSystemManagementOps
{
    private readonly string? _sandboxRoot;
    private readonly IReadOnlyList<string> _exemptedPrefixes;
    private readonly string _summaryDir;
    private readonly SessionReadCache? _sessionCache;
    private readonly FileVersionStore? _versionStore;
    private readonly HashSet<string> _readThisTurn;
    private readonly HashSet<string> _writtenThisTurn;
    private readonly HashSet<string> _patchedThisTurn;

    internal FileSystemManagementOps(
        FileSystemPlugin owner,
        string? sandboxRoot = null,
        SessionReadCache? sessionCache = null,
        FileVersionStore? versionStore = null,
        IReadOnlyList<string>? exemptedPaths = null)
    {
        _sandboxRoot      = sandboxRoot is not null ? FuseraftPaths.ExpandPath(sandboxRoot) : null;
        _exemptedPrefixes = (exemptedPaths ?? [])
            .Select(p => FuseraftPaths.ExpandPath(p).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToList();
        var baseDir       = _sandboxRoot ?? Directory.GetCurrentDirectory();
        _summaryDir       = Path.Combine(baseDir, ".fuseraft", "summaries");
        _sessionCache     = sessionCache;
        _versionStore     = versionStore;
        _readThisTurn     = owner.ReadThisTurnState;
        _writtenThisTurn  = owner.WrittenThisTurnState;
        _patchedThisTurn  = owner.PatchedThisTurnState;
    }

    [Description("Search a file (grep). Cheaper than full read_file.")]
    public async Task<string> GrepFileAsync(
        [Description("File path.")] string path,
        [Description("Text or regex pattern.")] string pattern,
        [Description("Context lines around match.")] int contextLines = 2,
        [Description("Max matches.")] int maxMatches = 30,
        CancellationToken cancellationToken = default)
    {
        var denial = FileSystemSandbox.ResolveSafe(path, _sandboxRoot, _exemptedPrefixes, out var resolved);
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

    // Absolute ceiling on maxResults regardless of what the caller requests — keeps a single
    // call from dumping an unbounded listing into context in a very large tree.
    private const int ListFilesHardCap = 500;

    [Description("List files recursively. Reports when results were truncated so you know to narrow the search — this matters most in large or multi-repo directories, where a flat result cap can silently miss files in a sibling subdirectory that wasn't reached yet.")]
    public string ListFiles(
        [Description("Directory path.")] string directory,
        [Description("Glob pattern, e.g. '*.cs'.")] string pattern = "*",
        [Description("Max results, clamped to 500. Raise it only if the default cuts off a search you know needs to see more.")] int maxResults = 100)
    {
        var denial = FileSystemSandbox.ResolveSafe(directory, _sandboxRoot, _exemptedPrefixes, out var resolved);
        if (denial is not null) return denial;

        if (!Directory.Exists(resolved))
        {
            if (File.Exists(resolved))
                return PluginResult.Error(
                    $"'{resolved}' is a file, not a directory. " +
                    $"Use read_file to read its content, or call list_files on its parent: " +
                    $"'{Path.GetDirectoryName(resolved) ?? resolved}'");
            return PluginResult.Error($"Directory not found: {resolved}");
        }

        var maxFiles = Math.Clamp(maxResults, 1, ListFilesHardCap);
        var files = Directory.EnumerateFiles(resolved, pattern, SearchOption.AllDirectories)
            .Where(f => !DirectoryFilters.IsExcluded(f))
            .Take(maxFiles + 1)
            .ToList();

        if (files.Count == 0)
            return PluginResult.Info("No files matched.");

        var truncated = files.Count > maxFiles;
        if (truncated) files.RemoveAt(files.Count - 1);

        var result = string.Join("\n", files);
        if (truncated)
            result += $"\n\n[TRUNCATED — showing first {maxFiles} matches; more exist beyond this cap. " +
                      "They may be concentrated in whichever subdirectory was walked first (e.g. one " +
                      "repo in a multi-repo working directory) — files elsewhere may not be represented " +
                      "at all. Narrow with a more specific 'directory' or 'pattern' rather than only " +
                      "raising maxResults.]";

        return result;
    }

    [Description("Delete a file.")]
    public async Task<string> DeleteFileAsync([Description("File path.")] string path)
    {
        var denial = FileSystemSandbox.ResolveSafe(path, _sandboxRoot, _exemptedPrefixes, out var resolved);
        if (denial is not null) return denial;

        if (!File.Exists(resolved))
            return PluginResult.Info($"File does not exist: {resolved}");

        File.Delete(resolved);
        await FileSystemSandbox.InvalidatePathAsync(
            resolved, _summaryDir, _readThisTurn, _writtenThisTurn, _patchedThisTurn, _sessionCache, _versionStore);
        return PluginResult.Ok($"Deleted: {resolved}");
    }

    [Description("Get file/directory metadata: size, timestamps, permissions, and (for files) the write-version counter. Cheaper than read_file when you only need to check existence or staleness. Version is NOT_TRACKED when the file exists but was never written through write_file.")]
    public async Task<string> GetFileInfoAsync([Description("File or directory path.")] string path)
    {
        var denial = FileSystemSandbox.ResolveSafe(path, _sandboxRoot, _exemptedPrefixes, out var resolved);
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

            var record = _versionStore is not null ? await _versionStore.StatAsync(resolved) : null;
            sb.AppendLine(record is not null
                ? $"Version:  {record.Version} (hash: {record.ContentHash ?? "(none)"})"
                : "Version:  NOT_TRACKED");
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

        var denial = FileSystemSandbox.ResolveSafe(path, _sandboxRoot, _exemptedPrefixes, out var resolved);
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
        var denial = FileSystemSandbox.ResolveSafe(path, _sandboxRoot, _exemptedPrefixes, out var resolved);
        if (denial is not null) return denial;

        Directory.CreateDirectory(resolved);
        return PluginResult.Ok($"Directory ready: {resolved}");
    }

    [Description("Delete a directory.")]
    public async Task<string> DeleteDirectoryAsync(
        [Description("Directory path.")] string path,
        [Description("Delete non-empty directories recursively.")] bool recursive = false)
    {
        var denial = FileSystemSandbox.ResolveSafe(path, _sandboxRoot, _exemptedPrefixes, out var resolved);
        if (denial is not null) return denial;

        if (!Directory.Exists(resolved))
            return PluginResult.Info($"Directory does not exist: {resolved}");

        // Refuse to delete the sandbox root itself.
        if (_sandboxRoot is not null)
        {
            var comparison    = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var sandboxCheck  = _sandboxRoot.TrimEnd(Path.DirectorySeparatorChar);
            var resolvedCheck = resolved.TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(sandboxCheck, resolvedCheck, comparison))
                return PluginResult.Denied("Cannot delete the sandbox root directory.");
        }

        // Enumerate all contained files before deletion so their state can be invalidated
        // after the directory tree is gone.
        var files = Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories).ToList();

        Directory.Delete(resolved, recursive);

        foreach (var file in files)
            await FileSystemSandbox.InvalidatePathAsync(
                file, _summaryDir, _readThisTurn, _writtenThisTurn, _patchedThisTurn, _sessionCache, _versionStore);

        return PluginResult.Ok($"Deleted directory: {resolved}");
    }

    [Description("Copy a file.")]
    public async Task<string> CopyFileAsync(
        [Description("Source path.")] string source,
        [Description("Destination path.")] string destination,
        [Description("Overwrite if destination exists.")] bool overwrite = false)
    {
        var srcDenial = FileSystemSandbox.ResolveSafe(source, _sandboxRoot, _exemptedPrefixes, out var resolvedSrc);
        if (srcDenial is not null) return srcDenial;

        var dstDenial = FileSystemSandbox.ResolveSafe(destination, _sandboxRoot, _exemptedPrefixes, out var resolvedDst);
        if (dstDenial is not null) return dstDenial;

        if (!File.Exists(resolvedSrc))
            return PluginResult.Error($"Source not found: {resolvedSrc}");

        if (!overwrite && File.Exists(resolvedDst))
            return PluginResult.Error($"Destination already exists: {resolvedDst}. Set overwrite=true to replace it.");

        var dir = Path.GetDirectoryName(resolvedDst);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await Task.Run(() => File.Copy(resolvedSrc, resolvedDst, overwrite));
        await FileSystemSandbox.InvalidatePathAsync(
            resolvedDst, _summaryDir, _readThisTurn, _writtenThisTurn, _patchedThisTurn, _sessionCache, _versionStore);
        _sessionCache?.RecordWrite(resolvedDst, new FileInfo(resolvedDst));
        return PluginResult.Ok($"Copied '{resolvedSrc}' → '{resolvedDst}'");
    }

    [Description("Move or rename a file or directory.")]
    public async Task<string> MoveFileAsync(
        [Description("Source path.")] string source,
        [Description("Destination path.")] string destination,
        [Description("Overwrite if destination file exists.")] bool overwrite = false)
    {
        var srcDenial = FileSystemSandbox.ResolveSafe(source, _sandboxRoot, _exemptedPrefixes, out var resolvedSrc);
        if (srcDenial is not null) return srcDenial;

        var dstDenial = FileSystemSandbox.ResolveSafe(destination, _sandboxRoot, _exemptedPrefixes, out var resolvedDst);
        if (dstDenial is not null) return dstDenial;

        if (Directory.Exists(resolvedSrc))
        {
            if (Directory.Exists(resolvedDst))
                return PluginResult.Error($"Destination directory already exists: {resolvedDst}");
            var dstParent = Path.GetDirectoryName(resolvedDst);
            if (!string.IsNullOrEmpty(dstParent)) Directory.CreateDirectory(dstParent);
            // Enumerate files before the move so we have the source paths for invalidation.
            var movedFiles = Directory.EnumerateFiles(resolvedSrc, "*", SearchOption.AllDirectories).ToList();
            Directory.Move(resolvedSrc, resolvedDst);
            foreach (var srcFile in movedFiles)
            {
                await FileSystemSandbox.InvalidatePathAsync(
                    srcFile, _summaryDir, _readThisTurn, _writtenThisTurn, _patchedThisTurn, _sessionCache, _versionStore);
                var dstFile = Path.Combine(resolvedDst, Path.GetRelativePath(resolvedSrc, srcFile));
                await FileSystemSandbox.InvalidatePathAsync(
                    dstFile, _summaryDir, _readThisTurn, _writtenThisTurn, _patchedThisTurn, _sessionCache, _versionStore);
            }
            return PluginResult.Ok($"Moved directory '{resolvedSrc}' → '{resolvedDst}'");
        }

        if (File.Exists(resolvedSrc))
        {
            if (!overwrite && File.Exists(resolvedDst))
                return PluginResult.Error($"Destination already exists: {resolvedDst}. Set overwrite=true to replace it.");
            var dstParent = Path.GetDirectoryName(resolvedDst);
            if (!string.IsNullOrEmpty(dstParent)) Directory.CreateDirectory(dstParent);
            File.Move(resolvedSrc, resolvedDst, overwrite);
            await FileSystemSandbox.InvalidatePathAsync(
                resolvedSrc, _summaryDir, _readThisTurn, _writtenThisTurn, _patchedThisTurn, _sessionCache, _versionStore);
            await FileSystemSandbox.InvalidatePathAsync(
                resolvedDst, _summaryDir, _readThisTurn, _writtenThisTurn, _patchedThisTurn, _sessionCache, _versionStore);
            return PluginResult.Ok($"Moved '{resolvedSrc}' → '{resolvedDst}'");
        }

        return PluginResult.Error($"Source not found: {resolvedSrc}");
    }

    [Description("Get a cached summary or auto-preview of a file. Use before read_file on large files.")]
    public async Task<string> GetFileSummaryAsync(
        [Description("File path.")] string path)
    {
        var denial = FileSystemSandbox.ResolveSafe(path, _sandboxRoot, _exemptedPrefixes, out var resolved);
        if (denial is not null) return denial;

        if (!File.Exists(resolved))
            return PluginResult.Error($"File not found: {resolved}");

        // Check for a cached summary.
        var summaryPath = FileSystemSandbox.SummaryPath(resolved, _summaryDir);
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
        if (fileInfo.Length > FileSystemPlugin.LargeFileByteThreshold)
        {
            var (previewLines, totalLines, sizeBytes) = await FileSystemSandbox.StreamPreviewLinesAsync(resolved, 30);
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

        var denial = FileSystemSandbox.ResolveSafe(path, _sandboxRoot, _exemptedPrefixes, out var resolved);
        if (denial is not null) return denial;

        Directory.CreateDirectory(_summaryDir);
        var summaryPath = FileSystemSandbox.SummaryPath(resolved, _summaryDir);
        await File.WriteAllTextAsync(summaryPath, summary.Trim());

        return PluginResult.Ok($"Summary saved for '{resolved}' → {summaryPath}");
    }

    [Description("List files and subdirectories (non-recursive).")]
    public string ListDirectory(
        [Description("Directory path.")] string directory,
        [Description("Glob pattern, e.g. '*.cs'.")] string pattern = "*")
    {
        var denial = FileSystemSandbox.ResolveSafe(directory, _sandboxRoot, _exemptedPrefixes, out var resolved);
        if (denial is not null) return denial;

        if (!Directory.Exists(resolved))
        {
            if (File.Exists(resolved))
                return PluginResult.Error(
                    $"'{resolved}' is a file, not a directory. " +
                    $"Use read_file to read its content, or call list_directory on its parent: " +
                    $"'{Path.GetDirectoryName(resolved) ?? resolved}'");
            return PluginResult.Error($"Directory not found: {resolved}");
        }

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
}
