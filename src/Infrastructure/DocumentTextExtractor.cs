using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;

namespace fuseraft.Infrastructure;

/// <summary>
/// Extracts plain text from rich document formats (PDF, DOCX, PPTX, XLSX).
/// Used by <see cref="ContextStore"/> at import time and by
/// <see cref="Plugins.DocumentPlugin"/> at agent runtime.
/// </summary>
public static class DocumentTextExtractor
{
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>([".pdf", ".docx", ".pptx", ".xlsx"], StringComparer.OrdinalIgnoreCase);

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Extracts plain text from <paramref name="path"/>.
    /// Returns the extracted text and a short info line (e.g. "PDF — 12 page(s)").
    /// Throws <see cref="NotSupportedException"/> for unsupported extensions.
    /// </summary>
    public static (string Text, string Info) Extract(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".pdf"  => ExtractPdf(path),
            ".docx" => ExtractDocx(path),
            ".pptx" => ExtractPptx(path),
            ".xlsx" => ExtractXlsx(path),
            _ => throw new NotSupportedException($"Unsupported document format: {ext}")
        };
    }

    /// <summary>Returns the sheet names in an Excel file.</summary>
    public static IReadOnlyList<string> ListSheets(string path)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        return doc.WorkbookPart?.Workbook?.Sheets?.Elements<Sheet>()
            .Select(s => s.Name?.Value ?? string.Empty)
            .ToList() ?? [];
    }

    /// <summary>
    /// Extracts a single sheet from an Excel file as pipe-delimited rows.
    /// </summary>
    public static (string Text, int RowCount) ExtractSheet(string path, string sheetName, int maxRows = 0)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var workbookPart = doc.WorkbookPart
            ?? throw new InvalidOperationException("Workbook has no parts.");

        var sharedStrings = BuildSharedStrings(workbookPart);

        var sheet = workbookPart.Workbook?.Sheets?.Elements<Sheet>()
            .FirstOrDefault(s => string.Equals(s.Name?.Value, sheetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Sheet '{sheetName}' not found.");

        if (sheet.Id?.Value is null)
            throw new InvalidOperationException($"Sheet '{sheetName}' has no part ID.");

        var wsPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id.Value);
        var data = wsPart.Worksheet?.GetFirstChild<SheetData>();
        if (data is null) return (string.Empty, 0);

        var sb = new StringBuilder();
        int rowCount = 0;
        foreach (var row in data.Elements<Row>())
        {
            if (maxRows > 0 && rowCount >= maxRows) break;
            var cells = row.Elements<Cell>().Select(c => GetCellValue(c, sharedStrings));
            sb.AppendLine(string.Join(" | ", cells));
            rowCount++;
        }
        return (sb.ToString().Trim(), rowCount);
    }

    // PDF

    private static (string Text, string Info) ExtractPdf(string path)
    {
        using var pdf = PdfDocument.Open(path);
        var pages = pdf.GetPages().ToList();
        var sb    = new StringBuilder();
        foreach (var page in pages)
        {
            var text = page.Text;
            if (!string.IsNullOrWhiteSpace(text))
                sb.AppendLine(text);
        }
        return (sb.ToString().Trim(), $"PDF — {pages.Count} page(s)");
    }

    // DOCX

    private static (string Text, string Info) ExtractDocx(string path)
    {
        using var doc  = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return (string.Empty, "DOCX — empty document");

        var sb = new StringBuilder();
        foreach (var elem in body.ChildElements)
        {
            if (elem is Paragraph para)
            {
                var text = para.InnerText;
                if (!string.IsNullOrWhiteSpace(text))
                    sb.AppendLine(text);
            }
            else if (elem is DocumentFormat.OpenXml.Wordprocessing.Table table)
            {
                foreach (var row in table.Elements<TableRow>())
                {
                    var cells = row.Elements<TableCell>()
                        .Select(c => c.InnerText.Trim())
                        .Where(t => !string.IsNullOrEmpty(t));
                    sb.AppendLine(string.Join(" | ", cells));
                }
            }
        }

        var extracted = sb.ToString().Trim();
        var wordCount = extracted.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return (extracted, $"DOCX — ~{wordCount:N0} word(s)");
    }

    // PPTX

    private static (string Text, string Info) ExtractPptx(string path)
    {
        using var pres      = PresentationDocument.Open(path, false);
        var slideParts = pres.PresentationPart?.SlideParts?.ToList() ?? [];
        var sb         = new StringBuilder();
        int slideNum   = 0;

        foreach (var slidePart in slideParts)
        {
            slideNum++;
            sb.AppendLine($"=== Slide {slideNum} ===");
            foreach (var text in slidePart.Slide?.Descendants<DocumentFormat.OpenXml.Drawing.Text>() ?? [])
            {
                if (!string.IsNullOrWhiteSpace(text.Text))
                    sb.AppendLine(text.Text);
            }
            sb.AppendLine();
        }

        return (sb.ToString().Trim(), $"PPTX — {slideParts.Count} slide(s)");
    }

    // XLSX

    private static (string Text, string Info) ExtractXlsx(string path)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var workbookPart = doc.WorkbookPart;
        if (workbookPart is null) return (string.Empty, "XLSX — empty workbook");

        var sharedStrings = BuildSharedStrings(workbookPart);
        var sheets = workbookPart.Workbook?.Sheets?.Elements<Sheet>().ToList() ?? [];
        var sb = new StringBuilder();
        int totalRows = 0;

        foreach (var sheet in sheets)
        {
            sb.AppendLine($"=== Sheet: {sheet.Name} ===");
            if (sheet.Id?.Value is null) continue;
            var wsPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id.Value);
            var data   = wsPart.Worksheet?.GetFirstChild<SheetData>();
            if (data is null) continue;

            foreach (var row in data.Elements<Row>())
            {
                var cells = row.Elements<Cell>().Select(c => GetCellValue(c, sharedStrings));
                sb.AppendLine(string.Join(" | ", cells));
                totalRows++;
            }
            sb.AppendLine();
        }

        return (sb.ToString().Trim(), $"XLSX — {sheets.Count} sheet(s), {totalRows:N0} row(s)");
    }

    // Helpers

    private static List<string> BuildSharedStrings(WorkbookPart workbookPart) =>
        workbookPart.SharedStringTablePart?.SharedStringTable
            ?.Elements<SharedStringItem>()
            .Select(s => s.InnerText)
            .ToList() ?? [];

    private static string GetCellValue(Cell cell, List<string> sharedStrings)
    {
        var value = cell.CellValue?.Text ?? string.Empty;
        if (cell.DataType?.Value == CellValues.SharedString
            && int.TryParse(value, out var idx)
            && (uint)idx < (uint)sharedStrings.Count)
            return sharedStrings[idx];
        return value;
    }
}
