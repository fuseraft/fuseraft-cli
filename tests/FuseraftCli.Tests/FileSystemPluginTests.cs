using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="FileSystemPlugin.WriteFileAsync"/> normalisation logic.
///
/// Key invariant under test: quote normalisation (\" → ") always runs for extensions in
/// QuoteNormalizeExtensions, even when raw=true. The raw flag only controls escape-sequence
/// expansion (\\n → newline, code-fence stripping, etc.).
/// </summary>
public sealed class FileSystemPluginTests : IDisposable
{
    private readonly string _dir;
    private readonly FileSystemPlugin _plugin;

    public FileSystemPluginTests()
    {
        _dir    = Path.Combine(Path.GetTempPath(), "fuseraft_fsp_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _plugin = new FileSystemPlugin(sandboxRoot: _dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string TempPath(string filename) => Path.Combine(_dir, filename);

    private async Task<string> ReadBack(string filename) =>
        await File.ReadAllTextAsync(TempPath(filename));

    // -----------------------------------------------------------------------
    // Path guard: newline embedded in path argument
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WriteFile_PathContainsNewline_ReturnsError()
    {
        var result = await _plugin.WriteFileAsync("foo.kiwi\nmalicious content", "content");
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("newline", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteFile_PathContainsCarriageReturn_ReturnsError()
    {
        var result = await _plugin.WriteFileAsync("foo.kiwi\rcontent", "content");
        Assert.StartsWith("[ERROR]", result);
    }

    // -----------------------------------------------------------------------
    // Sandbox denial: path outside sandbox root
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WriteFile_PathOutsideSandbox_ReturnsDenial()
    {
        var outsidePath = Path.Combine(Path.GetTempPath(), "outside_sandbox.txt");
        var result = await _plugin.WriteFileAsync(outsidePath, "content");
        Assert.StartsWith("[DENIED]", result);
    }

    [Fact]
    public async Task WriteFile_PathTraversalEscape_ReturnsDenial()
    {
        var result = await _plugin.WriteFileAsync("../../etc/passwd", "content");
        Assert.StartsWith("[DENIED]", result);
    }

    // -----------------------------------------------------------------------
    // Truncation guard: write blocked when new content is much smaller than existing
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WriteFile_TruncationGuard_BlocksWhenNewContentIsLessThan60Percent()
    {
        // Write a large file (> 50 lines) first.
        var bigContent = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"line {i}"));
        await _plugin.WriteFileAsync(TempPath("big.py"), bigContent);

        // Attempt to overwrite with only a few lines (well under 60% of 60).
        var result = await _plugin.WriteFileAsync(TempPath("big.py"), "line 1\nline 2");
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("truncation", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteFile_TruncationGuard_AllowsWriteWhenNewContentIsAbove60Percent()
    {
        var bigContent = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"line {i}"));
        await _plugin.WriteFileAsync(TempPath("medium.py"), bigContent);

        // 40 lines is 66% of 60 — above the 60% threshold.
        var newContent = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"line {i}"));
        var result = await _plugin.WriteFileAsync(TempPath("medium.py"), newContent);
        Assert.StartsWith("[OK]", result);
    }

    [Fact]
    public async Task WriteFile_TruncationGuard_DoesNotApplyToSmallExistingFiles()
    {
        // Files with ≤ 50 lines are never blocked, even if the new content is tiny.
        var smallContent = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line {i}"));
        await _plugin.WriteFileAsync(TempPath("small.py"), smallContent);

        var result = await _plugin.WriteFileAsync(TempPath("small.py"), "one line");
        Assert.StartsWith("[OK]", result);
    }

    // -----------------------------------------------------------------------
    // Quote normalisation — the regression case
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WriteFile_KiwiExtension_RawTrue_NormalizesOverEscapedQuotes()
    {
        // This is the bug that produced foo.kiwi: model passed raw=true and content with
        // \" where it meant ". Quote normalisation must run even when raw=true.
        var content = "println \\\"Hello, World!\\\"";
        await _plugin.WriteFileAsync(TempPath("foo.kiwi"), content, raw: true);

        Assert.Equal("println \"Hello, World!\"", await ReadBack("foo.kiwi"));
    }

    [Fact]
    public async Task WriteFile_KiwiExtension_RawFalse_NormalizesOverEscapedQuotes()
    {
        var content = "println \\\"Hello, World!\\\"";
        await _plugin.WriteFileAsync(TempPath("foo.kiwi"), content, raw: false);

        Assert.Equal("println \"Hello, World!\"", await ReadBack("foo.kiwi"));
    }

    [Fact]
    public async Task WriteFile_KiwiExtension_MultilineWithOverEscapedQuotes_NormalizesAllQuotes()
    {
        // Realistic case: file has real newlines (no \\n expansion needed) but \" artifacts.
        var content = "fn greet(name)\n  println \\\"Hello, \\\" + name\nend";
        await _plugin.WriteFileAsync(TempPath("greet.kiwi"), content, raw: true);

        Assert.Equal("fn greet(name)\n  println \"Hello, \" + name\nend", await ReadBack("greet.kiwi"));
    }

    // -----------------------------------------------------------------------
    // Extensions NOT in the normalise set leave \" untouched
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WriteFile_CsExtension_DoesNotNormalizeBackslashQuote()
    {
        // C# legitimately uses \" in string literals; must not be touched.
        var content = "var s = \"say \\\"hello\\\".\";";
        await _plugin.WriteFileAsync(TempPath("prog.cs"), content, raw: false);

        Assert.Equal(content, await ReadBack("prog.cs"));
    }

    // -----------------------------------------------------------------------
    // Other extensions in the set
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WriteFile_PyExtension_NormalizesOverEscapedQuotes()
    {
        var content = "print(\\\"hello\\\")";
        await _plugin.WriteFileAsync(TempPath("hello.py"), content, raw: false);

        Assert.Equal("print(\"hello\")", await ReadBack("hello.py"));
    }

    [Fact]
    public async Task WriteFile_JsExtension_RawTrue_NormalizesOverEscapedQuotes()
    {
        var content = "console.log(\\\"hi\\\")";
        await _plugin.WriteFileAsync(TempPath("hi.js"), content, raw: true);

        Assert.Equal("console.log(\"hi\")", await ReadBack("hi.js"));
    }

    // -----------------------------------------------------------------------
    // Content with no over-escaping is written verbatim
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WriteFile_KiwiExtension_NoOverEscaping_WrittenVerbatim()
    {
        var content = "println \"Hello, World!\"";
        await _plugin.WriteFileAsync(TempPath("clean.kiwi"), content, raw: false);

        Assert.Equal(content, await ReadBack("clean.kiwi"));
    }

    // -----------------------------------------------------------------------
    // raw=true still suppresses escape-sequence expansion
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WriteFile_RawTrue_PreservesLiteralBackslashN()
    {
        // raw=true must still suppress \\n → newline expansion.
        // Content has no real newlines and literal \n — should stay as-is.
        var content = "a\\nb\\nc";
        await _plugin.WriteFileAsync(TempPath("out.txt"), content, raw: true);

        Assert.Equal("a\\nb\\nc", await ReadBack("out.txt"));
    }

    // -----------------------------------------------------------------------
    // raw=false: escape-sequence expansion
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WriteFile_RawFalse_ExpandsLiteralBackslashN()
    {
        var content = "line1\\nline2\\nline3";
        await _plugin.WriteFileAsync(TempPath("out.py"), content, raw: false);

        Assert.Equal("line1\nline2\nline3", await ReadBack("out.py"));
    }

    [Fact]
    public async Task WriteFile_RawFalse_ExpandsLiteralCrLf()
    {
        var content = "line1\\r\\nline2\\r\\nline3";
        await _plugin.WriteFileAsync(TempPath("crlf.py"), content, raw: false);

        Assert.Equal("line1\r\nline2\r\nline3", await ReadBack("crlf.py"));
    }

    [Fact]
    public async Task WriteFile_RawFalse_ExpandsLiteralTabAlongsideLiteralNewline()
    {
        // \t expansion is part of the same branch as \n expansion: it only fires when
        // the content has no real newlines but does have literal \n sequences.
        var content = "col1\\tcol2\\nrow2col1\\tcol2";
        await _plugin.WriteFileAsync(TempPath("tabs.py"), content, raw: false);

        Assert.Equal("col1\tcol2\nrow2col1\tcol2", await ReadBack("tabs.py"));
    }

    [Fact]
    public async Task WriteFile_RawFalse_NoExpansionWhenContentHasRealNewlines()
    {
        // The \\n expansion only fires when there are zero real newlines in the content.
        // If a real newline is present, literal \n sequences should be left alone.
        var content = "real newline\nfollowed by literal \\n sequence";
        await _plugin.WriteFileAsync(TempPath("mixed.py"), content, raw: false);

        Assert.Equal(content, await ReadBack("mixed.py"));
    }

    // -----------------------------------------------------------------------
    // JSON: blank/whitespace content rejected
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WriteFile_JsonExtension_EmptyContent_ReturnsError()
    {
        var result = await _plugin.WriteFileAsync(TempPath("config.json"), "   ");
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("empty", result, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // JSON: code-fence stripping
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WriteFile_JsonExtension_StripsFencedJsonBlock()
    {
        var content = "```json\n{\"key\": \"value\"}\n```";
        await _plugin.WriteFileAsync(TempPath("data.json"), content);

        Assert.Equal("{\"key\": \"value\"}", await ReadBack("data.json"));
    }

    [Fact]
    public async Task WriteFile_JsonExtension_StripsUnlabelledFencedBlock()
    {
        var content = "```\n{\"key\": \"value\"}\n```";
        await _plugin.WriteFileAsync(TempPath("data2.json"), content);

        Assert.Equal("{\"key\": \"value\"}", await ReadBack("data2.json"));
    }

    [Fact]
    public async Task WriteFile_JsonExtension_PlainJson_WrittenVerbatim()
    {
        var content = "{\"key\": \"value\"}";
        await _plugin.WriteFileAsync(TempPath("plain.json"), content);

        Assert.Equal(content, await ReadBack("plain.json"));
    }

    // -----------------------------------------------------------------------
    // JSON: XML <parameter> wrapper stripping
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WriteFile_JsonExtension_StripsParameterXmlWrapper()
    {
        var content = "<parameter name=\"content\">{\"goal\": \"test\"}</parameter>";
        await _plugin.WriteFileAsync(TempPath("wrapped.json"), content);

        Assert.Equal("{\"goal\": \"test\"}", await ReadBack("wrapped.json"));
    }

    [Fact]
    public async Task WriteFile_JsonExtension_StripsParameterXmlWrapper_CaseInsensitive()
    {
        var content = "<PARAMETER name=\"content\">{\"x\": 1}</PARAMETER>";
        await _plugin.WriteFileAsync(TempPath("wrapped2.json"), content);

        Assert.Equal("{\"x\": 1}", await ReadBack("wrapped2.json"));
    }

    // -----------------------------------------------------------------------
    // PatchFileAsync — guard paths
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PatchFile_EmptyOldText_ReturnsError()
    {
        await File.WriteAllTextAsync(TempPath("p.py"), "content");
        var result = await _plugin.PatchFileAsync(TempPath("p.py"), "", "replacement");
        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task PatchFile_FileNotFound_ReturnsError()
    {
        var result = await _plugin.PatchFileAsync(TempPath("missing.py"), "old", "new");
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PatchFile_OldTextNotFound_ReturnsError()
    {
        await File.WriteAllTextAsync(TempPath("patch.py"), "line one\nline two\n");
        var result = await _plugin.PatchFileAsync(TempPath("patch.py"), "nonexistent text", "replacement");
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PatchFile_AmbiguousOldText_ReturnsError()
    {
        await File.WriteAllTextAsync(TempPath("ambig.py"), "foo\nfoo\n");
        var result = await _plugin.PatchFileAsync(TempPath("ambig.py"), "foo", "bar");
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("more than once", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PatchFile_SandboxDenial_ReturnsDenial()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"outside_{Guid.NewGuid():N}.py");
        var result = await _plugin.PatchFileAsync(outside, "old", "new");
        Assert.StartsWith("[DENIED]", result);
    }

    // -----------------------------------------------------------------------
    // PatchFileAsync — successful replacement
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PatchFile_SuccessfulReplacement_WritesCorrectContent()
    {
        await File.WriteAllTextAsync(TempPath("ok.py"), "hello world\n");
        var result = await _plugin.PatchFileAsync(TempPath("ok.py"), "hello", "goodbye");
        Assert.StartsWith("[OK]", result);
        Assert.Equal("goodbye world\n", await ReadBack("ok.py"));
    }

    [Fact]
    public async Task PatchFile_MultilineReplacement_WritesCorrectContent()
    {
        await File.WriteAllTextAsync(TempPath("multi.py"), "aaa\nbbb\nccc\n");
        await _plugin.PatchFileAsync(TempPath("multi.py"), "aaa\nbbb", "xxx\nyyy");
        Assert.Equal("xxx\nyyy\nccc\n", await ReadBack("multi.py"));
    }

    [Fact]
    public async Task PatchFile_CrlfFile_MatchesWithLfOldText_PreservesCrlf()
    {
        // Write a CRLF file directly (bypassing WriteFileAsync normalisation).
        await File.WriteAllTextAsync(TempPath("crlf.py"), "line one\r\nline two\r\n");
        var result = await _plugin.PatchFileAsync(TempPath("crlf.py"), "line one\nline two", "replaced one\nreplaced two");
        Assert.StartsWith("[OK]", result);
        var written = await ReadBack("crlf.py");
        Assert.Contains("\r\n", written);
        Assert.Contains("replaced one", written);
        Assert.Contains("replaced two", written);
    }

    [Fact]
    public async Task PatchFile_KiwiExtension_OverEscapedQuoteInOldText_StillMatches()
    {
        // The file was written with bare " (as WriteFileAsync normalises).
        await File.WriteAllTextAsync(TempPath("norm.kiwi"), "println(\"hello\")\n");

        // Agent over-escapes the quote in oldText — NormalizePatchText must fix this.
        var result = await _plugin.PatchFileAsync(TempPath("norm.kiwi"), "println(\\\"hello\\\")", "println(\\\"world\\\")");
        Assert.StartsWith("[OK]", result);
        Assert.Equal("println(\"world\")\n", await ReadBack("norm.kiwi"));
    }

    [Fact]
    public async Task PatchFile_KiwiExtension_OverEscapedQuoteInNewText_WritesNormalizedContent()
    {
        await File.WriteAllTextAsync(TempPath("new.kiwi"), "x = 1\n");
        await _plugin.PatchFileAsync(TempPath("new.kiwi"), "x = 1", "println(\\\"done\\\")");
        Assert.Equal("println(\"done\")\n", await ReadBack("new.kiwi"));
    }

    [Fact]
    public async Task PatchFile_OldTextNotFound_PartialFirstLineMatch_IncludesMismatchHint()
    {
        // First line of oldText matches a line in the file but the second line does not.
        await File.WriteAllTextAsync(TempPath("hint.py"), "def foo():\n    return 1\n");
        var result = await _plugin.PatchFileAsync(TempPath("hint.py"), "def foo():\n    return 99", "def foo():\n    return 2");
        Assert.StartsWith("[ERROR]", result);
        // The hint should call out the mismatching second line.
        Assert.Contains("Line 2 of oldText", result);
        Assert.Contains("return 99", result);
        Assert.Contains("return 1", result);
    }

    [Fact]
    public async Task PatchFile_InvalidatesReadCacheForPath()
    {
        await File.WriteAllTextAsync(TempPath("cache.py"), "original content\n");

        // Warm the read cache.
        await _plugin.ReadFileAsync(TempPath("cache.py"));

        // Patch the file — must invalidate the cache so the next read returns new content.
        await _plugin.PatchFileAsync(TempPath("cache.py"), "original", "updated");

        var result = await _plugin.ReadFileAsync(TempPath("cache.py"));
        // If cache was NOT invalidated this would return [INFO] cache-hit; new content proves it was.
        Assert.Contains("updated content", result);
    }

    // -----------------------------------------------------------------------
    // ReadFileAsync — error paths
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReadFile_FileNotFound_ReturnsError()
    {
        var result = await _plugin.ReadFileAsync(TempPath("nonexistent.txt"));
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadFile_BinaryFile_ReturnsError()
    {
        // Write a file containing a null byte to trigger the binary sniff.
        await File.WriteAllBytesAsync(TempPath("binary.bin"), [72, 101, 108, 108, 111, 0, 87, 111, 114, 108, 100]);
        var result = await _plugin.ReadFileAsync(TempPath("binary.bin"));
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("binary", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadFile_StartLineBeyondFileLength_ReturnsError()
    {
        await File.WriteAllTextAsync(TempPath("short.txt"), "one\ntwo\n");
        var result = await _plugin.ReadFileAsync(TempPath("short.txt"), startLine: 100);
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("exceeds", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadFile_ReadBudgetExhausted_ReturnsError()
    {
        var plugin = new FileSystemPlugin(sandboxRoot: _dir, readBudgetPerTurn: 10);
        await File.WriteAllTextAsync(TempPath("big.txt"), new string('x', 200));
        var result = await plugin.ReadFileAsync(TempPath("big.txt"));
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("budget", result, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // ReadFileAsync — read cache
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReadFile_SameTurnReread_ReturnsCacheHit()
    {
        await File.WriteAllTextAsync(TempPath("cache.txt"), "some content");
        var first  = await _plugin.ReadFileAsync(TempPath("cache.txt"));
        var second = await _plugin.ReadFileAsync(TempPath("cache.txt"));
        Assert.DoesNotContain("[INFO]", first);
        Assert.StartsWith("[INFO]", second);
        Assert.Contains("already read", second, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadFile_AfterBeginTurn_CacheCleared()
    {
        await File.WriteAllTextAsync(TempPath("cache2.txt"), "some content");
        await _plugin.ReadFileAsync(TempPath("cache2.txt"));           // warm cache
        ((ITurnResettable)_plugin).BeginTurn();                        // clear cache
        var result = await _plugin.ReadFileAsync(TempPath("cache2.txt"));
        Assert.DoesNotContain("[INFO]", result);
        Assert.Contains("some content", result);
    }

    [Fact]
    public async Task ReadFile_RangedReadBypassesCache()
    {
        // Reads with a non-default range must bypass the cache so a caller can page through a file.
        await File.WriteAllTextAsync(TempPath("paged.txt"), string.Join("\n", Enumerable.Range(1, 10).Select(i => $"L{i}")));
        await _plugin.ReadFileAsync(TempPath("paged.txt"));                              // full read — caches
        var result = await _plugin.ReadFileAsync(TempPath("paged.txt"), startLine: 5);  // ranged — must NOT be cache-blocked
        Assert.DoesNotContain("[INFO]", result);
        Assert.Contains("L5", result);
    }

    // -----------------------------------------------------------------------
    // ReadFileAsync — line range and navigation hints
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReadFile_WithStartLineAndMaxLines_ReturnsCorrectSlice()
    {
        await File.WriteAllTextAsync(TempPath("lines.txt"),
            string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line {i}")));

        var result = await _plugin.ReadFileAsync(TempPath("lines.txt"), startLine: 3, maxLines: 3);
        Assert.Contains("line 3", result);
        Assert.Contains("line 5", result);
        Assert.DoesNotContain("line 2\n", result);
        Assert.DoesNotContain("line 6", result);
    }

    [Fact]
    public async Task ReadFile_PartialRead_IncludesNavigationHint()
    {
        await File.WriteAllTextAsync(TempPath("hint.txt"),
            string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line {i}")));

        var result = await _plugin.ReadFileAsync(TempPath("hint.txt"), startLine: 2, maxLines: 3);
        Assert.Contains("Showing lines", result);
        Assert.Contains("startLine=", result);
    }

    [Fact]
    public async Task ReadFile_CharLimitTruncation_IncludesTruncatedHint()
    {
        var plugin = new FileSystemPlugin(sandboxRoot: _dir, readFileSizeLimit: 20);
        await File.WriteAllTextAsync(TempPath("long.txt"), new string('a', 100) + "\n" + new string('b', 100));
        var result = await plugin.ReadFileAsync(TempPath("long.txt"));
        Assert.Contains("TRUNCATED", result);
    }

    // -----------------------------------------------------------------------
    // GrepFileAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GrepFile_FileNotFound_ReturnsError()
    {
        var result = await _plugin.GrepFileAsync(TempPath("missing.txt"), "pattern");
        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task GrepFile_NoMatches_ReturnsInfo()
    {
        await File.WriteAllTextAsync(TempPath("grep.txt"), "line one\nline two\n");
        var result = await _plugin.GrepFileAsync(TempPath("grep.txt"), "zzznomatch");
        Assert.StartsWith("[INFO]", result);
        Assert.Contains("No matches", result);
    }

    [Fact]
    public async Task GrepFile_MatchFound_ReturnsMatchWithLineNumber()
    {
        await File.WriteAllTextAsync(TempPath("grep2.txt"), "alpha\nbeta\ngamma\n");
        var result = await _plugin.GrepFileAsync(TempPath("grep2.txt"), "beta", contextLines: 0);
        Assert.Contains("2", result);     // line number
        Assert.Contains("beta", result);
        Assert.DoesNotContain("alpha", result); // context=0, so no surrounding lines
    }

    [Fact]
    public async Task GrepFile_ContextLines_IncludesSurroundingLines()
    {
        await File.WriteAllTextAsync(TempPath("ctx.txt"), "before\ntarget\nafter\n");
        var result = await _plugin.GrepFileAsync(TempPath("ctx.txt"), "target", contextLines: 1);
        Assert.Contains("before", result);
        Assert.Contains("target", result);
        Assert.Contains("after", result);
    }

    [Fact]
    public async Task GrepFile_InvalidRegex_ReturnsError()
    {
        await File.WriteAllTextAsync(TempPath("re.txt"), "content");
        var result = await _plugin.GrepFileAsync(TempPath("re.txt"), "[unclosed");
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("Invalid pattern", result);
    }

    [Fact]
    public async Task GrepFile_MaxMatchesCap_TruncatesResults()
    {
        // 10 matching lines, cap at 3.
        var lines = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"match {i}"));
        await File.WriteAllTextAsync(TempPath("many.txt"), lines);
        var result = await _plugin.GrepFileAsync(TempPath("many.txt"), "match", contextLines: 0, maxMatches: 3);
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
        var result = _plugin.DeleteFile(TempPath("ghost.txt"));
        Assert.StartsWith("[INFO]", result);
        Assert.Contains("does not exist", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteFile_ExistingFile_DeletesAndReturnsOk()
    {
        await File.WriteAllTextAsync(TempPath("del.txt"), "bye");
        var result = _plugin.DeleteFile(TempPath("del.txt"));
        Assert.StartsWith("[OK]", result);
        Assert.False(File.Exists(TempPath("del.txt")));
    }

    [Fact]
    public void DeleteFile_SandboxDenial_ReturnsDenial()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"outside_{Guid.NewGuid():N}.txt");
        var result = _plugin.DeleteFile(outside);
        Assert.StartsWith("[DENIED]", result);
    }

    // -----------------------------------------------------------------------
    // ListFiles
    // -----------------------------------------------------------------------

    [Fact]
    public void ListFiles_DirectoryNotFound_ReturnsError()
    {
        var result = _plugin.ListFiles(TempPath("no_such_dir"));
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListFiles_ReturnsMatchingFiles()
    {
        await File.WriteAllTextAsync(TempPath("a.kiwi"), "");
        await File.WriteAllTextAsync(TempPath("b.kiwi"), "");
        await File.WriteAllTextAsync(TempPath("c.py"),   "");
        var result = _plugin.ListFiles(_dir, "*.kiwi");
        Assert.Contains("a.kiwi", result);
        Assert.Contains("b.kiwi", result);
        Assert.DoesNotContain("c.py", result);
    }

    [Fact]
    public async Task ListFiles_NoMatchingFiles_ReturnsInfo()
    {
        await File.WriteAllTextAsync(TempPath("only.py"), "");
        var result = _plugin.ListFiles(_dir, "*.rb");
        Assert.StartsWith("[INFO]", result);
        Assert.Contains("No files matched", result);
    }

    // -----------------------------------------------------------------------
    // GetFileSummaryAsync / SaveFileSummaryAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SaveFileSummary_EmptySummary_ReturnsError()
    {
        await File.WriteAllTextAsync(TempPath("src.py"), "content");
        var result = await _plugin.SaveFileSummaryAsync(TempPath("src.py"), "   ");
        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task SaveAndGetFileSummary_ReturnsCachedSummary()
    {
        await File.WriteAllTextAsync(TempPath("sum.py"), "content");
        await _plugin.SaveFileSummaryAsync(TempPath("sum.py"), "This file does X.");
        var result = await _plugin.GetFileSummaryAsync(TempPath("sum.py"));
        Assert.Contains("This file does X.", result);
        Assert.Contains("Cached summary", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetFileSummary_NoSavedSummary_ReturnsAutoPreview()
    {
        await File.WriteAllTextAsync(TempPath("auto.py"), "line one\nline two\nline three\n");
        var result = await _plugin.GetFileSummaryAsync(TempPath("auto.py"));
        Assert.Contains("line one", result);
        Assert.Contains("Full file", result);
    }

    [Fact]
    public async Task GetFileSummary_LargeFile_AutoPreviewShowsFirst30Lines()
    {
        var lines = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"L{i}"));
        await File.WriteAllTextAsync(TempPath("large.py"), lines);
        var result = await _plugin.GetFileSummaryAsync(TempPath("large.py"));
        Assert.Contains("L30", result);
        Assert.DoesNotContain("L31", result);
        Assert.Contains("Auto-preview", result);
    }

    [Fact]
    public async Task GetFileSummary_FileNotFound_ReturnsError()
    {
        var result = await _plugin.GetFileSummaryAsync(TempPath("ghost.py"));
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }
}
