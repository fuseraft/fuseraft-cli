using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="FileSystemManagementOps"/> — the directory/file-management and
/// read-only inspection tools split off <see cref="FileSystemPlugin"/>'s tool surface.
/// <c>_ops</c> is constructed from <c>_plugin</c> (see <see cref="FileSystemManagementOps"/>'s
/// constructor) so the two share the same per-turn state, mirroring how they're paired in
/// production via <c>PluginRegistry.RegisterAdditional</c>.
/// </summary>
public sealed class FileSystemManagementOpsTests : IDisposable
{
    private readonly string _dir;
    private readonly FileSystemPlugin _plugin;
    private readonly FileSystemManagementOps _ops;

    public FileSystemManagementOpsTests()
    {
        _dir    = Path.Combine(Path.GetTempPath(), "fuseraft_fsmo_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _plugin = new FileSystemPlugin(sandboxRoot: _dir);
        _ops    = new FileSystemManagementOps(_plugin, sandboxRoot: _dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string TempPath(string filename) => Path.Combine(_dir, filename);

    // -----------------------------------------------------------------------
    // GrepFileAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GrepFile_FileNotFound_ReturnsError()
    {
        var result = await _ops.GrepFileAsync(TempPath("missing.txt"), "pattern");
        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task GrepFile_NoMatches_ReturnsInfo()
    {
        await File.WriteAllTextAsync(TempPath("grep.txt"), "line one\nline two\n");
        var result = await _ops.GrepFileAsync(TempPath("grep.txt"), "zzznomatch");
        Assert.StartsWith("[INFO]", result);
        Assert.Contains("No matches", result);
    }

    [Fact]
    public async Task GrepFile_MatchFound_ReturnsMatchWithLineNumber()
    {
        await File.WriteAllTextAsync(TempPath("grep2.txt"), "alpha\nbeta\ngamma\n");
        var result = await _ops.GrepFileAsync(TempPath("grep2.txt"), "beta", contextLines: 0);
        Assert.Contains("2", result);     // line number
        Assert.Contains("beta", result);
        Assert.DoesNotContain("alpha", result); // context=0, so no surrounding lines
    }

    [Fact]
    public async Task GrepFile_ContextLines_IncludesSurroundingLines()
    {
        await File.WriteAllTextAsync(TempPath("ctx.txt"), "before\ntarget\nafter\n");
        var result = await _ops.GrepFileAsync(TempPath("ctx.txt"), "target", contextLines: 1);
        Assert.Contains("before", result);
        Assert.Contains("target", result);
        Assert.Contains("after", result);
    }

    [Fact]
    public async Task GrepFile_InvalidRegex_ReturnsError()
    {
        await File.WriteAllTextAsync(TempPath("re.txt"), "content");
        var result = await _ops.GrepFileAsync(TempPath("re.txt"), "[unclosed");
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("Invalid pattern", result);
    }

    [Fact]
    public async Task GrepFile_MaxMatchesCap_TruncatesResults()
    {
        // 10 matching lines, cap at 3.
        var lines = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"match {i}"));
        await File.WriteAllTextAsync(TempPath("many.txt"), lines);
        var result = await _ops.GrepFileAsync(TempPath("many.txt"), "match", contextLines: 0, maxMatches: 3);
        Assert.Contains("capped", result, StringComparison.OrdinalIgnoreCase);
        // Only 3 matches shown — "match 4" through "match 10" should not appear.
        Assert.DoesNotContain("match 4", result);
    }

    // -----------------------------------------------------------------------
    // DeleteFile
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DeleteFile_FileDoesNotExist_ReturnsInfo()
    {
        var result = await _ops.DeleteFileAsync(TempPath("ghost.txt"));
        Assert.StartsWith("[INFO]", result);
        Assert.Contains("does not exist", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteFile_ExistingFile_DeletesAndReturnsOk()
    {
        await File.WriteAllTextAsync(TempPath("del.txt"), "bye");
        var result = await _ops.DeleteFileAsync(TempPath("del.txt"));
        Assert.StartsWith("[OK]", result);
        Assert.False(File.Exists(TempPath("del.txt")));
    }

    [Fact]
    public async Task DeleteFile_SandboxDenial_ReturnsDenial()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"outside_{Guid.NewGuid():N}.txt");
        var result = await _ops.DeleteFileAsync(outside);
        Assert.StartsWith("[DENIED]", result);
    }

    // -----------------------------------------------------------------------
    // ListFiles
    // -----------------------------------------------------------------------

    [Fact]
    public void ListFiles_DirectoryNotFound_ReturnsError()
    {
        var result = _ops.ListFiles(TempPath("no_such_dir"));
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListFiles_ReturnsMatchingFiles()
    {
        await File.WriteAllTextAsync(TempPath("a.kiwi"), "");
        await File.WriteAllTextAsync(TempPath("b.kiwi"), "");
        await File.WriteAllTextAsync(TempPath("c.py"),   "");
        var result = _ops.ListFiles(_dir, "*.kiwi");
        Assert.Contains("a.kiwi", result);
        Assert.Contains("b.kiwi", result);
        Assert.DoesNotContain("c.py", result);
    }

    [Fact]
    public async Task ListFiles_NoMatchingFiles_ReturnsInfo()
    {
        await File.WriteAllTextAsync(TempPath("only.py"), "");
        var result = _ops.ListFiles(_dir, "*.rb");
        Assert.StartsWith("[INFO]", result);
        Assert.Contains("No files matched", result);
    }

    [Fact]
    public async Task ListFiles_MoreMatchesThanMaxResults_TruncatesAndExplainsWhy()
    {
        for (var i = 0; i < 5; i++)
            await File.WriteAllTextAsync(TempPath($"f{i}.kiwi"), "");

        var result = _ops.ListFiles(_dir, "*.kiwi", maxResults: 3);
        Assert.Contains("TRUNCATED", result);
        Assert.Contains("first 3", result);
        // Guidance should point at narrowing scope, not just raising the cap blindly —
        // this is the multi-repo/large-tree blind spot the cap can't see past.
        Assert.Contains("Narrow with", result);
    }

    [Fact]
    public async Task ListFiles_MaxResultsAboveHardCap_IsClamped()
    {
        await File.WriteAllTextAsync(TempPath("only.kiwi"), "");
        var result = _ops.ListFiles(_dir, "*.kiwi", maxResults: 100_000);
        Assert.Contains("only.kiwi", result);
        Assert.DoesNotContain("TRUNCATED", result);
    }

    [Fact]
    public async Task ListFiles_FewerMatchesThanDefault_NotTruncated()
    {
        await File.WriteAllTextAsync(TempPath("a.kiwi"), "");
        var result = _ops.ListFiles(_dir, "*.kiwi");
        Assert.DoesNotContain("TRUNCATED", result);
    }

    // -----------------------------------------------------------------------
    // GetFileInfoAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetFileInfo_PathNotFound_ReturnsError()
    {
        // No dedicated existence-check tool remains (path_exists was folded in here) —
        // a not-found result from get_file_info is the way to check existence now.
        var result = await _ops.GetFileInfoAsync(TempPath("ghost.txt"));
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetFileInfo_File_ReportsSizeAndUntrackedVersion()
    {
        await File.WriteAllTextAsync(TempPath("info.txt"), "hello");
        var result = await _ops.GetFileInfoAsync(TempPath("info.txt"));
        Assert.Contains("Type:     file", result);
        Assert.Contains("Size:", result);
        // No version store was passed to this test fixture's plugin instance.
        Assert.Contains("Version:  NOT_TRACKED", result);
    }

    [Fact]
    public async Task GetFileInfo_Directory_HasNoVersionLine()
    {
        var result = await _ops.GetFileInfoAsync(_dir);
        Assert.Contains("Type:     directory", result);
        Assert.DoesNotContain("Version:", result);
    }

    // -----------------------------------------------------------------------
    // GetFileSummaryAsync / SaveFileSummaryAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SaveFileSummary_EmptySummary_ReturnsError()
    {
        await File.WriteAllTextAsync(TempPath("src.py"), "content");
        var result = await _ops.SaveFileSummaryAsync(TempPath("src.py"), "   ");
        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task SaveAndGetFileSummary_ReturnsCachedSummary()
    {
        await File.WriteAllTextAsync(TempPath("sum.py"), "content");
        await _ops.SaveFileSummaryAsync(TempPath("sum.py"), "This file does X.");
        var result = await _ops.GetFileSummaryAsync(TempPath("sum.py"));
        Assert.Contains("This file does X.", result);
        Assert.Contains("Cached summary", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetFileSummary_NoSavedSummary_ReturnsAutoPreview()
    {
        await File.WriteAllTextAsync(TempPath("auto.py"), "line one\nline two\nline three\n");
        var result = await _ops.GetFileSummaryAsync(TempPath("auto.py"));
        Assert.Contains("line one", result);
        Assert.Contains("Full file", result);
    }

    [Fact]
    public async Task GetFileSummary_LargeFile_AutoPreviewShowsFirst30Lines()
    {
        var lines = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"L{i}"));
        await File.WriteAllTextAsync(TempPath("large.py"), lines);
        var result = await _ops.GetFileSummaryAsync(TempPath("large.py"));
        Assert.Contains("L30", result);
        Assert.DoesNotContain("L31", result);
        Assert.Contains("Auto-preview", result);
    }

    [Fact]
    public async Task GetFileSummary_FileNotFound_ReturnsError()
    {
        var result = await _ops.GetFileSummaryAsync(TempPath("ghost.py"));
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // Cross-object shared per-turn state: an invalidation from _ops must be visible
    // to _plugin's read/write pipeline, since both share the same HashSet instances.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DeleteFile_InvalidatesPluginReadCacheForSamePath()
    {
        await File.WriteAllTextAsync(TempPath("shared.txt"), "original");
        await _plugin.ReadFileAsync(TempPath("shared.txt")); // warms _plugin's per-turn read cache

        await _ops.DeleteFileAsync(TempPath("shared.txt"));
        await File.WriteAllTextAsync(TempPath("shared.txt"), "recreated");

        // If the delete hadn't invalidated the shared _readThisTurn entry, this would
        // return a stale "already read this turn" cache-hit instead of fresh content.
        var result = await _plugin.ReadFileAsync(TempPath("shared.txt"));
        Assert.DoesNotContain("[INFO]", result);
        Assert.Contains("recreated", result);
    }
}
