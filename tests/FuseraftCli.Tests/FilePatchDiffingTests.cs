using fuseraft.Infrastructure.Agents;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="FilePatchDiffing"/> — pure text-diffing/normalization helpers behind
/// FileSystemPlugin's write_file/patch_file pipelines. All members are internal static, visible
/// here via the project's InternalsVisibleTo. Most methods are pure functions; only
/// EnsureFileExistsAsync touches the filesystem.
/// </summary>
public sealed class FilePatchDiffingTests : IDisposable
{
    private readonly string _root;

    public FilePatchDiffingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuseraft_patchdiff_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // CountLines

    [Fact]
    public void CountLines_FirstLineFound_ReturnsLineHint()
    {
        var content = "alpha\nbeta\ngamma\ndelta";
        var hint = FilePatchDiffing.CountLines(content, "gamma\nsome trailing");

        Assert.Contains("line 3", hint);
        Assert.Contains("gamma", hint);
    }

    [Fact]
    public void CountLines_NotFound_ReturnsEmpty()
    {
        var hint = FilePatchDiffing.CountLines("alpha\nbeta", "nonexistent-text-xyz");
        Assert.Equal(string.Empty, hint);
    }

    [Fact]
    public void CountLines_EmptySearchText_ReturnsEmpty()
    {
        var hint = FilePatchDiffing.CountLines("alpha\nbeta", "   \n");
        Assert.Equal(string.Empty, hint);
    }

    // NormalizePatchText

    [Fact]
    public void NormalizePatchText_QuoteNormalizeExtension_StripsEscapedQuotes()
    {
        var input = "print(\\\"hello\\\")";
        var result = FilePatchDiffing.NormalizePatchText(input, ".py");
        Assert.Equal("print(\"hello\")", result);
    }

    [Fact]
    public void NormalizePatchText_NonQuoteNormalizeExtension_LeavesEscapedQuotesAlone()
    {
        var input = "printf(\\\"hello\\\")";
        var result = FilePatchDiffing.NormalizePatchText(input, ".cs");
        Assert.Equal(input, result);
    }

    [Fact]
    public void NormalizePatchText_LiteralNewlineSequence_ExpandedToRealNewline()
    {
        var input = "line1\\nline2";
        var result = FilePatchDiffing.NormalizePatchText(input, ".cs");
        Assert.Equal("line1\nline2", result);
    }

    [Fact]
    public void NormalizePatchText_AlreadyHasRealNewline_DoesNotExpandLiteralSequence()
    {
        var input = "real\nline with literal \\n inside";
        var result = FilePatchDiffing.NormalizePatchText(input, ".cs");
        Assert.Equal(input, result);
    }

    // ExtractExcerpt

    [Fact]
    public void ExtractExcerpt_GoodMatch_ReturnsContextWindowWithMarker()
    {
        var fileContent = "one\ntwo\nthree\nfour\nfive";
        var excerpt = FilePatchDiffing.ExtractExcerpt(fileContent, "three\nsomething", contextLines: 1);

        Assert.Contains(">>>", excerpt);
        Assert.Contains("three", excerpt);
        Assert.Contains("two", excerpt);
        Assert.Contains("four", excerpt);
    }

    [Fact]
    public void ExtractExcerpt_NoMeaningfulMatch_ReturnsEmpty()
    {
        var fileContent = "aaaa\nbbbb\ncccc";
        var excerpt = FilePatchDiffing.ExtractExcerpt(fileContent, "zzzzzzzz", contextLines: 2);

        Assert.Equal(string.Empty, excerpt);
    }

    [Fact]
    public void ExtractExcerpt_EmptySearchText_ReturnsEmpty()
    {
        var excerpt = FilePatchDiffing.ExtractExcerpt("one\ntwo", "  ", contextLines: 2);
        Assert.Equal(string.Empty, excerpt);
    }

    // FindFirstDifference

    [Fact]
    public void FindFirstDifference_EmDashVsHyphens_NamesBothCharacters()
    {
        var file   = "Run the import \u2014 then bounce the cluster.\n";
        var search = "Run the import -- then bounce the cluster.";

        var result = FilePatchDiffing.FindFirstDifference(file, search);

        Assert.Contains("line 1, column 16", result);
        Assert.Contains("U+2014 em dash", result);
        Assert.Contains("'-' (U+002D)", result);
    }

    [Fact]
    public void FindFirstDifference_CurlyVsStraightQuote_NamesCurlyQuote()
    {
        var result = FilePatchDiffing.FindFirstDifference(
            "In the utility, choose \u201CRebuild Global Settings\u201D next.", "In the utility, choose \"Rebuild Global Settings\" next.");
        Assert.Contains("curly double quote", result);
    }

    [Fact]
    public void FindFirstDifference_TrailingSpacesInFile_ReportsSpaceVsLineBreak()
    {
        var file   = "intro\nThe docs say to bounce.  \nNext line\n";
        var search = "The docs say to bounce.\nNext line";

        var result = FilePatchDiffing.FindFirstDifference(file, search);

        Assert.Contains("line 2, column 24", result);
        Assert.Contains("the file has a space where oldText has a line break", result);
    }

    [Fact]
    public void FindFirstDifference_NonBreakingSpace_IsNamed()
    {
        var result = FilePatchDiffing.FindFirstDifference(
            "Costpoint\u00A0admin server only", "Costpoint admin server only");
        // The shared prefix is only 9 chars, under the anchor, so pad it.
        Assert.Equal(string.Empty, result);

        result = FilePatchDiffing.FindFirstDifference(
            "Run on the Costpoint\u00A0admin server", "Run on the Costpoint admin server");
        Assert.Contains("non-breaking space (U+00A0)", result);
    }

    [Fact]
    public void FindFirstDifference_LaterLineDiverges_ReportsThatLine()
    {
        var file   = "line one here\nline two here\nline DIFFERENT\nline four";
        var search = "line one here\nline two here\nline three";

        var result = FilePatchDiffing.FindFirstDifference(file, search);

        Assert.Contains("line 3, column 6", result);
        Assert.Contains("line DIFFERENT", result);
        Assert.Contains("line three", result);
    }

    [Fact]
    public void FindFirstDifference_OldTextRunsPastEndOfFile_SaysSo()
    {
        var result = FilePatchDiffing.FindFirstDifference("final sentence here", "final sentence here and more");
        Assert.Contains("the end of the file", result);
    }

    [Fact]
    public void FindFirstDifference_ShortCoincidentalPrefix_ReturnsEmpty()
    {
        var result = FilePatchDiffing.FindFirstDifference("The cat sat.", "The dog ran away from home.");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FindFirstDifference_ExactMatch_ReturnsEmpty()
    {
        var result = FilePatchDiffing.FindFirstDifference("line1\nline2\nline3", "line1\nline2\nline3");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FindFirstDifference_ExcerptShowsLineBreaksEscaped()
    {
        var result = FilePatchDiffing.FindFirstDifference("alpha beta gamma\ndelta X", "alpha beta gamma\ndelta Y");
        Assert.Contains("gamma\\ndelta X", result);
        Assert.DoesNotContain("\n", result);
    }

    // Truncate

    [Fact]
    public void Truncate_ShortString_ReturnsUnchanged()
    {
        Assert.Equal("short", FilePatchDiffing.Truncate("short"));
    }

    [Fact]
    public void Truncate_LongString_TruncatesWithEllipsis()
    {
        var input = new string('x', 100);
        var result = FilePatchDiffing.Truncate(input, max: 10);

        Assert.Equal(11, result.Length); // 10 chars + ellipsis
        Assert.EndsWith("…", result);
        Assert.StartsWith(new string('x', 10), result);
    }

    // DetectElisionPlaceholder

    [Fact]
    public void DetectElisionPlaceholder_CleanContent_ReturnsNull()
    {
        var result = FilePatchDiffing.DetectElisionPlaceholder("just ordinary file content");
        Assert.Null(result);
    }

    [Fact]
    public void DetectElisionPlaceholder_ArgValuePlaceholderAtEnd_ReturnsError()
    {
        var content = "some content\n" + ElisionMarkers.ArgValueNote(1234);
        var result = FilePatchDiffing.DetectElisionPlaceholder(content);

        Assert.NotNull(result);
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("WRITE BLOCKED", result);
    }

    [Fact]
    public void DetectElisionPlaceholder_ResultElidedMarkerAtEnd_ReturnsError()
    {
        var content = "some content\n" + ElisionMarkers.ResultNote;
        var result = FilePatchDiffing.DetectElisionPlaceholder(content);

        Assert.NotNull(result);
    }

    [Fact]
    public void DetectElisionPlaceholder_MarkerNotAtEnd_ReturnsNull()
    {
        var content = ElisionMarkers.ArgValueNote(1234) + "\nmore content after it";
        var result = FilePatchDiffing.DetectElisionPlaceholder(content);

        Assert.Null(result);
    }

    // EnsureFileExistsAsync

    [Fact]
    public async Task EnsureFileExists_FileDoesNotExist_ReturnsNull()
    {
        var path = Path.Combine(_root, "missing.txt");
        var result = await FilePatchDiffing.EnsureFileExistsAsync(path, "any content");
        Assert.Null(result);
    }

    [Fact]
    public async Task EnsureFileExists_SmallExistingFile_ReturnsNullRegardlessOfNewSize()
    {
        var path = Path.Combine(_root, "small.txt");
        await File.WriteAllTextAsync(path, string.Join('\n', Enumerable.Range(0, 10).Select(i => $"line{i}")));

        var result = await FilePatchDiffing.EnsureFileExistsAsync(path, "one line only");
        Assert.Null(result);
    }

    [Fact]
    public async Task EnsureFileExists_LargeFileWithMuchSmallerReplacement_ReturnsTruncationError()
    {
        var path = Path.Combine(_root, "large.txt");
        await File.WriteAllTextAsync(path, string.Join('\n', Enumerable.Range(0, 100).Select(i => $"line{i}")));

        var newContent = string.Join('\n', Enumerable.Range(0, 10).Select(i => $"line{i}"));
        var result = await FilePatchDiffing.EnsureFileExistsAsync(path, newContent);

        Assert.NotNull(result);
        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("truncation guard", result);
    }

    [Fact]
    public async Task EnsureFileExists_LargeFileWithComparableReplacement_ReturnsNull()
    {
        var path = Path.Combine(_root, "large2.txt");
        await File.WriteAllTextAsync(path, string.Join('\n', Enumerable.Range(0, 100).Select(i => $"line{i}")));

        var newContent = string.Join('\n', Enumerable.Range(0, 90).Select(i => $"line{i}"));
        var result = await FilePatchDiffing.EnsureFileExistsAsync(path, newContent);

        Assert.Null(result);
    }

    // ComputeAndReportDiff

    [Fact]
    public void ComputeAndReportDiff_QuoteNormalizeExtension_NormalizesEvenWhenRaw()
    {
        var content = "x = \\\"value\\\"";
        var error = FilePatchDiffing.ComputeAndReportDiff("f.py", content, ".py", raw: true,
            out var normalized, out var normalised);

        Assert.Null(error);
        Assert.True(normalised);
        Assert.Equal("x = \"value\"", normalized);
    }

    [Fact]
    public void ComputeAndReportDiff_JsonEmptyContent_ReturnsError()
    {
        var error = FilePatchDiffing.ComputeAndReportDiff("f.json", "   ", ".json", raw: false,
            out _, out _);

        Assert.NotNull(error);
        Assert.StartsWith("[ERROR]", error);
        Assert.Contains("empty", error);
    }

    [Fact]
    public void ComputeAndReportDiff_JsonWithMarkdownFence_StripsFence()
    {
        var content = "```json\n{\"a\":1}\n```";
        var error = FilePatchDiffing.ComputeAndReportDiff("f.json", content, ".json", raw: false,
            out var normalized, out var normalised);

        Assert.Null(error);
        Assert.True(normalised);
        Assert.Equal("{\"a\":1}", normalized);
    }

    [Fact]
    public void ComputeAndReportDiff_JsonWithParameterWrapper_StripsWrapper()
    {
        var content = "<parameter name=\"content\">{\"a\":1}</parameter>";
        var error = FilePatchDiffing.ComputeAndReportDiff("f.json", content, ".json", raw: false,
            out var normalized, out var normalised);

        Assert.Null(error);
        Assert.True(normalised);
        Assert.Equal("{\"a\":1}", normalized);
    }

    [Fact]
    public void ComputeAndReportDiff_LiteralNewlineSequence_ExpandedWhenNotRaw()
    {
        var content = "line1\\nline2";
        var error = FilePatchDiffing.ComputeAndReportDiff("f.txt", content, ".txt", raw: false,
            out var normalized, out var normalised);

        Assert.Null(error);
        Assert.True(normalised);
        Assert.Equal("line1\nline2", normalized);
    }

    [Fact]
    public void ComputeAndReportDiff_Raw_SkipsEscapeExpansion()
    {
        var content = "line1\\nline2";
        var error = FilePatchDiffing.ComputeAndReportDiff("f.txt", content, ".txt", raw: true,
            out var normalized, out var normalised);

        Assert.Null(error);
        Assert.False(normalised);
        Assert.Equal(content, normalized);
    }

    [Fact]
    public void ComputeAndReportDiff_SourceFileWithTypographicChar_ReturnsError()
    {
        var content = "var x = 1; // em—dash here";
        var error = FilePatchDiffing.ComputeAndReportDiff("f.cs", content, ".cs", raw: false,
            out _, out _);

        Assert.NotNull(error);
        Assert.StartsWith("[ERROR]", error);
        Assert.Contains("typographic", error);
        Assert.Contains("em-dash", error);
    }

    [Fact]
    public void ComputeAndReportDiff_NonSourceFileWithTypographicChar_NoGuardApplied()
    {
        var content = "prose with an em—dash in it";
        var error = FilePatchDiffing.ComputeAndReportDiff("f.txt", content, ".txt", raw: false,
            out var normalized, out _);

        Assert.Null(error);
        Assert.Equal(content, normalized);
    }

    [Fact]
    public void ComputeAndReportDiff_Raw_SkipsTypographicGuardEvenForSourceFile()
    {
        var content = "var x = 1; // em—dash here";
        var error = FilePatchDiffing.ComputeAndReportDiff("f.cs", content, ".cs", raw: true,
            out _, out _);

        Assert.Null(error);
    }

    [Fact]
    public void ComputeAndReportDiff_CleanContent_ReturnsNullAndUnchanged()
    {
        var content = "ordinary clean content";
        var error = FilePatchDiffing.ComputeAndReportDiff("f.txt", content, ".txt", raw: false,
            out var normalized, out var normalised);

        Assert.Null(error);
        Assert.False(normalised);
        Assert.Equal(content, normalized);
    }

    // LineNumberAt / OccurrenceLines

    [Theory]
    [InlineData("a\nb\nc", 0, 1)]
    [InlineData("a\nb\nc", 1, 1)]   // the '\n' itself is still on line 1
    [InlineData("a\nb\nc", 2, 2)]
    [InlineData("a\nb\nc", 4, 3)]
    [InlineData("", 0, 1)]
    public void LineNumberAt_IsOneBased(string content, int index, int expected) =>
        Assert.Equal(expected, FilePatchDiffing.LineNumberAt(content, index));

    [Fact]
    public void LineNumberAt_IndexPastEnd_ClampsToLastLine() =>
        Assert.Equal(3, FilePatchDiffing.LineNumberAt("a\nb\nc", 999));

    [Fact]
    public void OccurrenceLines_ReturnsEachStartingLineInOrder()
    {
        var content = "foo\nbar\nfoo\nbaz\n\nfoo\n";

        Assert.Equal([1, 3, 6], FilePatchDiffing.OccurrenceLines(content, "foo", max: 10));
    }

    [Fact]
    public void OccurrenceLines_TwoMatchesOnOneLine_ListsThatLineOnce() =>
        Assert.Equal([1, 2], FilePatchDiffing.OccurrenceLines("x = foo + foo\nfoo\n", "foo", max: 10));

    [Fact]
    public void OccurrenceLines_MultiLineSearch_ReportsWhereItStarts()
    {
        var content = "a\nb\nc\na\nb\nd\n";

        Assert.Equal([1, 4], FilePatchDiffing.OccurrenceLines(content, "a\nb", max: 10));
    }

    [Fact]
    public void OccurrenceLines_StopsAtMax() =>
        Assert.Equal([1, 2, 3], FilePatchDiffing.OccurrenceLines("x\nx\nx\nx\nx\n", "x", max: 3));

    [Fact]
    public void OccurrenceLines_NoMatchOrEmptySearch_ReturnsEmpty()
    {
        Assert.Empty(FilePatchDiffing.OccurrenceLines("abc", "zzz", max: 5));
        Assert.Empty(FilePatchDiffing.OccurrenceLines("abc", "", max: 5));
    }
}
