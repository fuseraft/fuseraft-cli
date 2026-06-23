using System.Text.Json;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Config;
using fuseraft.Orchestration.Contracts;

namespace FuseraftCli.Tests;

/// <summary>
/// Verifies the TestReport contract's <c>HasAssertions</c> check actually catches the
/// per-test fabrication pattern it exists for: a Tester agent runs one real aggregate
/// command (e.g. plain "pytest") but writes a test-report.json with several distinct,
/// more specific commands (e.g. "pytest tests/test_x.py::test_name") that were never
/// independently run. See ContractEngine.EvaluateTestReportAsync.
/// </summary>
public sealed class ContractEngineTestReportFabricationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fuseraft_ce_tr_{Guid.NewGuid():N}");
    private readonly string _reportPath;
    private readonly string _changesPath;

    public ContractEngineTestReportFabricationTests()
    {
        Directory.CreateDirectory(_dir);
        _reportPath = Path.Combine(_dir, "test-report.json");
        _changesPath = Path.Combine(_dir, "changes.json");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ContractEngine NewEngine() => new(
        [Contract],
        new ValidationConfig { TestReportPath = _reportPath, ChangeLogPath = _changesPath });

    private static ContractConfig Contract => new()
    {
        Name = "C",
        Requires =
        [
            new ContractPredicate { Type = "TestReport", NoFailures = true, HasAssertions = true }
        ]
    };

    private async Task WriteChangesAsync(params string[] succeededCommands)
    {
        var changes = new
        {
            activeSessionId = (string?)null,
            entries = new[]
            {
                new
                {
                    sessionId = (string?)null,
                    turnIndex = 0,
                    filesWritten = Array.Empty<string>(),
                    commandsRun = succeededCommands.Select(c => new { command = c, succeeded = true }).ToArray(),
                }
            }
        };
        await File.WriteAllTextAsync(_changesPath, JsonSerializer.Serialize(changes));
    }

    private async Task WriteReportAsync(params (string criterion, string command)[] results)
    {
        var report = new
        {
            results = results.Select(r => new { criterion = r.criterion, status = "PASS", command = r.command }).ToArray()
        };
        await File.WriteAllTextAsync(_reportPath, JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task Fails_When_OneRealCommand_Covers_SeveralFabricatedPerTestRows()
    {
        // Only one aggregate command actually ran.
        await WriteChangesAsync("pytest");
        // But the report claims several distinct, more specific commands were each run.
        await WriteReportAsync(
            ("criterion A", "pytest tests/test_a.py::test_alpha"),
            ("criterion B", "pytest tests/test_b.py::test_beta"));

        var (ok, error) = await NewEngine().EvaluateAsync("C");

        Assert.False(ok);
        Assert.Contains("fabrication", error, StringComparison.OrdinalIgnoreCase);
        // Both fabricated rows must be named, not just the first one found.
        Assert.Contains("criterion A", error);
        Assert.Contains("criterion B", error);
    }

    [Fact]
    public async Task Passes_When_SameRealCommand_HonestlyCitedForMultipleCriteria()
    {
        await WriteChangesAsync("pytest -v");
        // Reusing the literal command that ran, for two different criteria, is honest.
        await WriteReportAsync(
            ("criterion A", "pytest -v"),
            ("criterion B", "pytest -v"));

        var (ok, error) = await NewEngine().EvaluateAsync("C");

        Assert.True(ok, error);
    }

    [Fact]
    public async Task Passes_When_ReportCommand_IsAbbreviatedSubstringOfRealCommand()
    {
        // The real command ran with extra flags; the report cites a shorter, honest substring of it.
        await WriteChangesAsync("python3 -m pytest tests/ -v --tb=short");
        await WriteReportAsync(("criterion A", "pytest tests/"));

        var (ok, error) = await NewEngine().EvaluateAsync("C");

        Assert.True(ok, error);
    }

    [Fact]
    public async Task Fails_When_SingleFabricatedRow_HasNoMatchingCommand()
    {
        await WriteChangesAsync("pytest");
        await WriteReportAsync(("criterion A", "totally invented command"));

        var (ok, error) = await NewEngine().EvaluateAsync("C");

        Assert.False(ok);
        Assert.Contains("fabrication", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Passes_When_NoChangeLog_Exists_LenientFallback()
    {
        // No changes.json at all → nothing to verify against; check is skipped, not failed.
        await WriteReportAsync(("criterion A", "pytest tests/test_a.py::test_alpha"));

        var (ok, error) = await NewEngine().EvaluateAsync("C");

        Assert.True(ok, error);
    }
}
