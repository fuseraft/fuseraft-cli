using System.Text.Json;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="AuditPlugin"/> — the narrow, fixed-path artifact writer that lets the
/// audit template's Auditor agent be locked to FileSystem:[read] while still persisting its
/// own findings. See <see cref="ReconPluginTests"/>/<see cref="PreflightPluginTests"/> for the
/// brownfield/greenfield equivalents.
/// </summary>
public sealed class AuditPluginTests : IDisposable
{
    private readonly string _root;
    private readonly string _findingsPath;

    public AuditPluginTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuseraft_audit_tests_" + Guid.NewGuid().ToString("N")[..8]);
        _findingsPath = Path.Combine(_root, "nested", "audit-findings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private AuditPlugin NewPlugin() => new(_findingsPath);

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record FindingDoc
    {
        public string? Id { get; init; }
        public string? Severity { get; init; }
        public string? Type { get; init; }
        public string? File { get; init; }
        public int Line { get; init; }
        public string? Description { get; init; }
        public string? Recommendation { get; init; }
    }

    private sealed record ReportDoc
    {
        public List<FindingDoc> Findings { get; init; } = [];
    }

    [Fact]
    public async Task WriteFileAuditFindings_WritesParallelArrays_AsFindingObjects()
    {
        var plugin = NewPlugin();

        var result = await plugin.WriteFileAuditFindingsAsync(
            ids: ["SEC-001", "QUA-001"],
            severities: ["critical", "low"],
            types: ["security", "quality"],
            files: ["src/auth.go", "src/util.go"],
            lines: [42, 7],
            descriptions: ["SQL built via string concatenation", "dead code path"],
            recommendations: ["use parameterized queries", "remove the function"]);

        Assert.StartsWith("[OK]", result);

        var json   = await File.ReadAllTextAsync(_findingsPath);
        var report = JsonSerializer.Deserialize<ReportDoc>(json, ReadOptions);

        Assert.NotNull(report);
        Assert.Equal(2, report!.Findings.Count);
        Assert.Equal("SEC-001", report.Findings[0].Id);
        Assert.Equal("critical", report.Findings[0].Severity);
        Assert.Equal("security", report.Findings[0].Type);
        Assert.Equal("src/auth.go", report.Findings[0].File);
        Assert.Equal(42, report.Findings[0].Line);
        Assert.Equal("SQL built via string concatenation", report.Findings[0].Description);
        Assert.Equal("use parameterized queries", report.Findings[0].Recommendation);

        Assert.Equal("QUA-001", report.Findings[1].Id);
        Assert.Equal(7, report.Findings[1].Line);
    }

    [Fact]
    public async Task WriteFileAuditFindings_MismatchedArrayLengths_TruncatesToShortest()
    {
        var plugin = NewPlugin();

        await plugin.WriteFileAuditFindingsAsync(
            ids: ["SEC-001", "SEC-002", "SEC-003"],
            severities: ["high"]); // only one severity provided

        var json   = await File.ReadAllTextAsync(_findingsPath);
        var report = JsonSerializer.Deserialize<ReportDoc>(json, ReadOptions);

        Assert.NotNull(report);
        Assert.Single(report!.Findings);
        Assert.Equal("SEC-001", report.Findings[0].Id);
        Assert.Equal("high", report.Findings[0].Severity);
    }

    [Fact]
    public async Task WriteFileAuditFindings_NoArgsAtAll_WritesEmptyFindings()
    {
        var plugin = NewPlugin();

        var result = await plugin.WriteFileAuditFindingsAsync();

        Assert.StartsWith("[OK]", result);
        var json   = await File.ReadAllTextAsync(_findingsPath);
        var report = JsonSerializer.Deserialize<ReportDoc>(json, ReadOptions);

        Assert.NotNull(report);
        Assert.Empty(report!.Findings);
    }

    [Fact]
    public async Task WriteFileAuditFindings_CreatesParentDirectoryIfMissing()
    {
        Assert.False(Directory.Exists(Path.GetDirectoryName(_findingsPath)));

        var plugin = NewPlugin();
        await plugin.WriteFileAuditFindingsAsync(ids: ["SEC-001"]);

        Assert.True(File.Exists(_findingsPath));
    }
}
