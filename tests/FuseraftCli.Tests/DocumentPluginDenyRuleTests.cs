using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// <see cref="DocumentPlugin"/> had its own private sandbox check and never learned about FileSystem
/// deny rules, so a configured <c>Deny: ["secrets/**"]</c> stopped <c>read_file</c> but not
/// <c>get_sheet</c> on <c>secrets/passwords.xlsx</c> — and spreadsheets and PDFs are exactly where
/// people keep credential lists. Real .xlsx files, so extraction is genuinely exercised.
/// </summary>
public sealed class DocumentPluginDenyRuleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fuseraft_docdeny_").FullName;

    private const string Secret = "hunter2-spreadsheet-password";

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Xlsx(string relative, string cellValue)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = doc.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        var row = new Row();
        row.Append(new Cell { CellValue = new CellValue(cellValue), DataType = CellValues.String });
        sheetData.Append(row);
        worksheetPart.Worksheet = new Worksheet(sheetData);
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = "Sheet1" });
        workbookPart.Workbook.Save();
        return path;
    }

    private DocumentPlugin Plugin(params string[] denyGlobs) =>
        new(_root, DefaultSecurityPolicy.MergeFileSystemDeny(new FileSystemPermissions { Deny = [.. denyGlobs] }));

    private static void AssertDenied(string result)
    {
        Assert.StartsWith("[DENIED]", result);
        Assert.Contains("deny rule", result);
        Assert.DoesNotContain(Secret, result);
    }

    [Fact]
    public void EveryDocumentTool_RefusesAFileACustomDenyGlobProtects()
    {
        var path = Xlsx("secrets/passwords.xlsx", Secret);
        var plugin = Plugin("secrets/**");

        AssertDenied(plugin.ExtractText(path));
        AssertDenied(plugin.GetInfo(path));
        AssertDenied(plugin.ListSheets(path));
        AssertDenied(plugin.GetSheet(path, "Sheet1"));
    }

    [Fact]
    public void ARelativePath_IsCheckedAgainstTheDenyRuleToo()
    {
        Xlsx("secrets/passwords.xlsx", Secret);

        AssertDenied(Plugin("secrets/**").GetSheet("secrets/passwords.xlsx", "Sheet1"));
    }

    [Fact]
    public void ADenyGlobAtAnyDepth_Applies()
    {
        var path = Xlsx("team/finance/secrets/keys.xlsx", Secret);

        AssertDenied(Plugin("**/secrets/**").GetSheet(path, "Sheet1"));
    }

    [Fact]
    public void WithoutTheDenyRule_TheSameFileIsReadable_SoTheRuleIsWhatStopsIt()
    {
        var path = Xlsx("secrets/passwords.xlsx", Secret);

        var result = new DocumentPlugin(_root).GetSheet(path, "Sheet1");

        Assert.Contains(Secret, result);
    }

    [Fact]
    public void OrdinaryDocuments_AreUnaffected()
    {
        var path = Xlsx("reports/q3.xlsx", "quarterly-figures");

        var plugin = Plugin("secrets/**");

        Assert.Contains("quarterly-figures", plugin.GetSheet(path, "Sheet1"));
        Assert.DoesNotContain("[DENIED]", plugin.GetInfo(path));
        Assert.DoesNotContain("[DENIED]", plugin.ListSheets(path));
    }

    [Fact]
    public void TheDefaultProtectedNames_ApplyToDocumentsToo()
    {
        // A spreadsheet somebody named like a credentials file — the default globs match by NAME.
        var path = Xlsx("ops/.netrc", Secret);

        AssertDenied(new DocumentPlugin(_root, DefaultSecurityPolicy.MergeFileSystemDeny(null)).ExtractText(path));
    }

    [Fact]
    public void ADenyRuleWinsEvenForAPathOutsideTheSandbox_WithTheDenyMessage()
    {
        // Same order as FileSystemSandbox.ResolveSafe: the rule is checked first.
        var outside = Directory.CreateTempSubdirectory("fuseraft_docdeny_out_").FullName;
        try
        {
            var path = Path.Combine(outside, ".netrc");
            File.WriteAllText(path, "irrelevant");

            var result = new DocumentPlugin(_root, DefaultSecurityPolicy.MergeFileSystemDeny(null)).GetInfo(path);

            AssertDenied(result);
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void ThePlainSandboxCheck_StillWorksForAnOrdinaryOutsideFile()
    {
        var outside = Directory.CreateTempSubdirectory("fuseraft_docdeny_out2_").FullName;
        try
        {
            var result = Plugin().GetInfo(Path.Combine(outside, "report.xlsx"));

            Assert.StartsWith("[DENIED]", result);
            Assert.Contains("outside the configured sandbox", result);
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void Configure_WiresTheDenyRulesIntoTheRegisteredDocumentPlugin()
    {
        var path = Xlsx("secrets/passwords.xlsx", Secret);

        var registry = new PluginRegistry().RegisterDefaults().Configure(new SecurityConfig
        {
            FileSystemSandboxPath = _root,
            FileSystemPermissions = new FileSystemPermissions { Deny = ["secrets/**"] },
        });
        Assert.True(registry.TryGet("Document", out var obj));
        var document = Assert.IsType<DocumentPlugin>(obj);

        AssertDenied(document.GetSheet(path, "Sheet1"));
    }
}
