using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="DocumentPlugin"/> — the read-only rich-document text extractor.
/// Uses a real, minimal .xlsx built with DocumentFormat.OpenXml (the same library
/// <c>DocumentTextExtractor</c> reads with) rather than a stub, so extraction is exercised
/// against actual file bytes, not just the plugin's own routing/error-handling logic.
/// </summary>
public sealed class DocumentPluginTests : IDisposable
{
    private readonly string _root;

    public DocumentPluginTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuseraft_document_plugin_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string CreateMinimalXlsx(string fileName, string sheetName, string[][] rows)
    {
        var path = Path.Combine(_root, fileName);
        using (var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            foreach (var rowValues in rows)
            {
                var row = new Row();
                foreach (var value in rowValues)
                    row.Append(new Cell { CellValue = new CellValue(value), DataType = CellValues.String });
                sheetData.Append(row);
            }
            worksheetPart.Worksheet = new Worksheet(sheetData);

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id      = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name    = sheetName,
            });
            workbookPart.Workbook.Save();
        }
        return path;
    }

    [Fact]
    public void ExtractText_ValidXlsx_ReturnsCellContentJoinedByPipe()
    {
        var path   = CreateMinimalXlsx("data.xlsx", "Sheet1", [["a", "b"], ["c", "d"]]);
        var plugin = new DocumentPlugin();

        var result = plugin.ExtractText(path);

        Assert.Contains("a | b", result);
        Assert.Contains("c | d", result);
        Assert.Contains("XLSX", result);
    }

    [Fact]
    public void ExtractText_FileDoesNotExist_ReturnsError()
    {
        var plugin = new DocumentPlugin();

        var result = plugin.ExtractText(Path.Combine(_root, "missing.xlsx"));

        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractText_UnsupportedExtension_ReturnsErrorListingSupportedFormats()
    {
        var path = Path.Combine(_root, "notes.txt");
        File.WriteAllText(path, "plain text");
        var plugin = new DocumentPlugin();

        var result = plugin.ExtractText(path);

        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("Unsupported format", result);
    }

    [Fact]
    public void ExtractText_PathOutsideSandbox_ReturnsDenied()
    {
        var sandboxRoot = Path.Combine(_root, "sandbox");
        Directory.CreateDirectory(sandboxRoot);
        var outsidePath = CreateMinimalXlsx("outside.xlsx", "Sheet1", [["x"]]);
        var plugin      = new DocumentPlugin(sandboxRoot);

        var result = plugin.ExtractText(outsidePath);

        Assert.StartsWith("[DENIED]", result);
    }

    [Fact]
    public void ExtractText_PathInsideSandbox_Allowed()
    {
        var sandboxRoot = Path.Combine(_root, "sandbox");
        Directory.CreateDirectory(sandboxRoot);
        var path = CreateMinimalXlsx(Path.Combine("sandbox", "inside.xlsx"), "Sheet1", [["ok"]]);
        var plugin = new DocumentPlugin(sandboxRoot);

        var result = plugin.ExtractText(path);

        Assert.Contains("ok", result);
    }

    [Fact]
    public void GetInfo_ValidXlsx_ReportsSizeAndCharCount()
    {
        var path   = CreateMinimalXlsx("info.xlsx", "Sheet1", [["hello", "world"]]);
        var plugin = new DocumentPlugin();

        var result = plugin.GetInfo(path);

        Assert.Contains("File size:", result);
        Assert.Contains("Extracted text:", result);
        Assert.Contains("tokens", result);
    }

    [Fact]
    public void GetInfo_FileDoesNotExist_ReturnsError()
    {
        var plugin = new DocumentPlugin();

        var result = plugin.GetInfo(Path.Combine(_root, "missing.xlsx"));

        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public void ListSheets_ValidXlsx_ReturnsSheetNames()
    {
        var path   = CreateMinimalXlsx("sheets.xlsx", "MySheet", [["a"]]);
        var plugin = new DocumentPlugin();

        var result = plugin.ListSheets(path);

        Assert.Contains("MySheet", result);
    }

    [Fact]
    public void ListSheets_NonXlsxFile_ReturnsError()
    {
        var path = Path.Combine(_root, "doc.pdf");
        File.WriteAllBytes(path, [0x25, 0x50, 0x44, 0x46]); // "%PDF" magic bytes, not a real PDF
        var plugin = new DocumentPlugin();

        var result = plugin.ListSheets(path);

        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("only works on .xlsx", result);
    }

    [Fact]
    public void GetSheet_ValidSheet_ReturnsRowsAsPipeDelimitedText()
    {
        var path   = CreateMinimalXlsx("get.xlsx", "Data", [["col1", "col2"], ["1", "2"]]);
        var plugin = new DocumentPlugin();

        var result = plugin.GetSheet(path, "Data");

        Assert.Contains("col1 | col2", result);
        Assert.Contains("1 | 2", result);
        Assert.Contains("2 row(s)", result);
    }

    [Fact]
    public void GetSheet_UnknownSheetName_ReturnsError()
    {
        var path   = CreateMinimalXlsx("get.xlsx", "Data", [["a"]]);
        var plugin = new DocumentPlugin();

        var result = plugin.GetSheet(path, "DoesNotExist");

        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public void GetSheet_MaxRowsLimitsOutputAndNotesTruncation()
    {
        var path   = CreateMinimalXlsx("get.xlsx", "Data", [["r1"], ["r2"], ["r3"]]);
        var plugin = new DocumentPlugin();

        var result = plugin.GetSheet(path, "Data", maxRows: 2);

        Assert.Contains("first 2 rows", result);
    }

    [Fact]
    public void GetSheet_NonXlsxFile_ReturnsError()
    {
        var path = Path.Combine(_root, "doc.docx");
        File.WriteAllText(path, "not a real docx");
        var plugin = new DocumentPlugin();

        var result = plugin.GetSheet(path, "Sheet1");

        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("only works on .xlsx", result);
    }
}
