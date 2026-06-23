using System.Text.Json;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="ReconPlugin"/> — the narrow, fixed-path artifact writer that lets
/// brownfield's Archaeologist agent be locked to FileSystem:[read] while still persisting its
/// own findings. See <see cref="PreflightPluginTests"/> for the greenfield equivalent.
/// </summary>
public sealed class ReconPluginTests : IDisposable
{
    private readonly string _root;
    private readonly string _conventionsPath;
    private readonly string _briefPath;

    public ReconPluginTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuseraft_recon_tests_" + Guid.NewGuid().ToString("N")[..8]);
        _conventionsPath = Path.Combine(_root, "nested", "conventions.json");
        _briefPath       = Path.Combine(_root, "nested", "brief.brownfield.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private ReconPlugin NewPlugin() => new(_conventionsPath, _briefPath);

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    // ── WriteFileConventionsAsync ───────────────────────────────────────────

    [Fact]
    public async Task WriteFileConventions_WritesExpectedJsonShape()
    {
        var plugin = NewPlugin();

        var result = await plugin.WriteFileConventionsAsync(
            language: "go",
            namingPatterns: ["test files match *_test.go"],
            errorHandling: ["wrap errors with fmt.Errorf(\"%w\", err)"],
            forbiddenPatterns: ["no panic() outside main"],
            testPatterns: ["table-driven tests"],
            structuralNotes: ["cmd/ holds entry points"],
            buildCommand: "go build ./...",
            testCommand: "go test ./...");

        Assert.StartsWith("[OK]", result);
        Assert.True(File.Exists(_conventionsPath));

        var json    = await File.ReadAllTextAsync(_conventionsPath);
        var profile = JsonSerializer.Deserialize<ConventionProfile>(json, ReadOptions);

        Assert.NotNull(profile);
        Assert.Equal("go", profile!.Language);
        Assert.Equal(["test files match *_test.go"], profile.NamingPatterns);
        Assert.Equal(["wrap errors with fmt.Errorf(\"%w\", err)"], profile.ErrorHandling);
        Assert.Equal(["no panic() outside main"], profile.ForbiddenPatterns);
        Assert.Equal(["table-driven tests"], profile.TestPatterns);
        Assert.Equal(["cmd/ holds entry points"], profile.StructuralNotes);
        Assert.Equal("go build ./...", profile.BuildCommand);
        Assert.Equal("go test ./...", profile.TestCommand);
    }

    [Fact]
    public async Task WriteFileConventions_AllArgsOmitted_WritesEmptyDefaults()
    {
        var plugin = NewPlugin();

        await plugin.WriteFileConventionsAsync();

        var json    = await File.ReadAllTextAsync(_conventionsPath);
        var profile = JsonSerializer.Deserialize<ConventionProfile>(json, ReadOptions);

        Assert.NotNull(profile);
        Assert.Null(profile!.Language);
        Assert.Empty(profile.NamingPatterns);
    }

    // ── WriteFileDiscoveryBriefAsync ────────────────────────────────────────

    [Fact]
    public async Task WriteFileDiscoveryBrief_ParsesFragilitySignals()
    {
        var plugin = NewPlugin();

        await plugin.WriteFileDiscoveryBriefAsync(
            summary: "A small Go service.",
            inScopeFiles: ["cmd/server/main.go"],
            fragilitySignals: ["internal/legacy/queue.go — no tests, high churn", "malformed entry with no separator"],
            testCoverageGaps: ["internal/legacy/queue.go"]);

        var json  = await File.ReadAllTextAsync(_briefPath);
        var brief = JsonSerializer.Deserialize<BrownfieldDiscoveryBrief>(json, ReadOptions);

        Assert.NotNull(brief);
        Assert.Equal("A small Go service.", brief!.Summary);
        Assert.Equal(["cmd/server/main.go"], brief.InScopeFiles);
        Assert.Equal(["internal/legacy/queue.go"], brief.TestCoverageGaps);

        Assert.Equal(2, brief.FragilitySignals.Count);
        Assert.Equal("internal/legacy/queue.go", brief.FragilitySignals[0].File);
        Assert.Equal("no tests, high churn", brief.FragilitySignals[0].Reason);

        // Malformed entry (no " — " separator) degrades gracefully instead of throwing:
        // whole string kept as the reason, empty file.
        Assert.Equal(string.Empty, brief.FragilitySignals[1].File);
        Assert.Equal("malformed entry with no separator", brief.FragilitySignals[1].Reason);
    }

    // ── Shared behavior ──────────────────────────────────────────────────────

    [Fact]
    public async Task WriteFileConventions_CreatesParentDirectoryIfMissing()
    {
        Assert.False(Directory.Exists(Path.GetDirectoryName(_conventionsPath)));

        var plugin = NewPlugin();
        await plugin.WriteFileConventionsAsync(language: "rust");

        Assert.True(File.Exists(_conventionsPath));
    }
}
