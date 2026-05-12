using System.ComponentModel;
using fuseraft.Infrastructure;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Reads rich document formats (PDF, DOCX, PPTX, XLSX) as plain text.
/// All operations are read-only. Path arguments are sandbox-checked when a
/// sandbox root is configured.
/// </summary>
public sealed class DocumentPlugin(string? sandboxRoot = null)
{
    private readonly string? _sandboxRoot = sandboxRoot is not null
        ? Path.GetFullPath(ProcessHelper.ExpandHome(sandboxRoot))
        : null;

    [Description("Extract plain text from a document. Supports PDF, DOCX, PPTX, XLSX.")]
    public string ExtractText([Description("Path to the document.")] string path)
    {
        var denial = ResolveSafe(path, out var resolved);
        if (denial is not null) return denial;
        if (!File.Exists(resolved)) return PluginResult.Error($"File not found: {resolved}");
        if (!DocumentTextExtractor.IsSupported(resolved))
            return PluginResult.Error(
                $"Unsupported format '{Path.GetExtension(resolved)}'. " +
                $"Supported: {string.Join(", ", DocumentTextExtractor.SupportedExtensions)}");

        try
        {
            var (text, info) = DocumentTextExtractor.Extract(resolved);
            return string.IsNullOrWhiteSpace(text)
                ? PluginResult.Info($"{info} — no text content found.")
                : $"[{info}]\n\n{text}";
        }
        catch (Exception ex)
        {
            return PluginResult.Error($"Extraction failed: {ex.Message}");
        }
    }

    [Description("Get format and size metadata for a document. Cheaper than extract_text. Supports PDF, DOCX, PPTX, XLSX.")]
    public string GetInfo([Description("Path to the document.")] string path)
    {
        var denial = ResolveSafe(path, out var resolved);
        if (denial is not null) return denial;
        if (!File.Exists(resolved)) return PluginResult.Error($"File not found: {resolved}");
        if (!DocumentTextExtractor.IsSupported(resolved))
            return PluginResult.Error(
                $"Unsupported format '{Path.GetExtension(resolved)}'. " +
                $"Supported: {string.Join(", ", DocumentTextExtractor.SupportedExtensions)}");

        try
        {
            var fi            = new FileInfo(resolved);
            var (text, info)  = DocumentTextExtractor.Extract(resolved);
            var charCount     = text.Length;
            return $"{info}\nFile size: {FormatSize(fi.Length)}\n" +
                   $"Extracted text: ~{charCount:N0} characters (~{charCount / 4:N0} tokens)";
        }
        catch (Exception ex)
        {
            return PluginResult.Error($"Could not read document metadata: {ex.Message}");
        }
    }

    [Description("List sheet names in an Excel file (.xlsx).")]
    public string ListSheets([Description("Path to the .xlsx file.")] string path)
    {
        var denial = ResolveSafe(path, out var resolved);
        if (denial is not null) return denial;
        if (!File.Exists(resolved)) return PluginResult.Error($"File not found: {resolved}");

        var ext = Path.GetExtension(resolved).ToLowerInvariant();
        if (ext != ".xlsx")
            return PluginResult.Error($"list_sheets only works on .xlsx files, not '{ext}'.");

        try
        {
            var sheets = DocumentTextExtractor.ListSheets(resolved);
            return sheets.Count == 0
                ? PluginResult.Info("No sheets found.")
                : string.Join("\n", sheets.Select((s, i) => $"{i + 1}. {s}"));
        }
        catch (Exception ex)
        {
            return PluginResult.Error($"Could not read sheet list: {ex.Message}");
        }
    }

    [Description("Read one sheet from an Excel file (.xlsx) as a pipe-delimited text table.")]
    public string GetSheet(
        [Description("Path to the .xlsx file.")] string path,
        [Description("Sheet name.")] string sheetName,
        [Description("Maximum rows to return (0 = all).")] int maxRows = 0)
    {
        var denial = ResolveSafe(path, out var resolved);
        if (denial is not null) return denial;
        if (!File.Exists(resolved)) return PluginResult.Error($"File not found: {resolved}");

        var ext = Path.GetExtension(resolved).ToLowerInvariant();
        if (ext != ".xlsx")
            return PluginResult.Error($"get_sheet only works on .xlsx files, not '{ext}'.");

        try
        {
            var (text, rowCount) = DocumentTextExtractor.ExtractSheet(resolved, sheetName, maxRows);
            if (string.IsNullOrWhiteSpace(text))
                return PluginResult.Info($"Sheet '{sheetName}' is empty.");
            var truncNote = maxRows > 0 && rowCount >= maxRows ? $" — first {maxRows} rows" : string.Empty;
            return $"[Sheet: {sheetName} — {rowCount} row(s){truncNote}]\n\n{text}";
        }
        catch (KeyNotFoundException ex)
        {
            return PluginResult.Error(ex.Message);
        }
        catch (Exception ex)
        {
            return PluginResult.Error($"Could not read sheet '{sheetName}': {ex.Message}");
        }
    }

    private string? ResolveSafe(string path, out string resolved)
    {
        var expanded = ProcessHelper.ExpandHome(path);
        resolved = _sandboxRoot is not null && !Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded, _sandboxRoot)
            : Path.GetFullPath(expanded);

        if (_sandboxRoot is null) return null;

        var sandboxPrefix = _sandboxRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolvedCheck = resolved.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison    = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return resolvedCheck.StartsWith(sandboxPrefix, comparison)
            ? null
            : PluginResult.Denied($"Path '{resolved}' is outside the configured sandbox '{_sandboxRoot}'.");
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1_024         => $"{bytes} B",
        < 1_048_576     => $"{bytes / 1_024.0:F1} KB",
        < 1_073_741_824 => $"{bytes / 1_048_576.0:F1} MB",
        _               => $"{bytes / 1_073_741_824.0:F1} GB",
    };
}
