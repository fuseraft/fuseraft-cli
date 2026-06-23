using System.Text.Json;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="PreflightPlugin"/> — the narrow, fixed-path artifact writer that lets
/// greenfield's Preflight agent be locked to FileSystem:[read] while still persisting its own
/// findings. See <see cref="ReconPluginTests"/> for the brownfield equivalent.
/// </summary>
public sealed class PreflightPluginTests : IDisposable
{
    private readonly string _root;
    private readonly string _preflightPath;

    public PreflightPluginTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuseraft_preflight_tests_" + Guid.NewGuid().ToString("N")[..8]);
        _preflightPath = Path.Combine(_root, "nested", "preflight.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private PreflightPlugin NewPlugin() => new(_preflightPath);

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task WriteFilePreflight_ParsesRuntimeVersions()
    {
        var plugin = NewPlugin();

        var result = await plugin.WriteFilePreflightAsync(
            projectTypes: ["python"],
            runtimeVersions: ["python3: 3.12.1", "malformed entry with no colon"],
            missingRuntimes: ["go"],
            gitRepo: true,
            gitClean: false,
            warnings: ["no requirements.txt found"]);

        Assert.StartsWith("[OK]", result);

        var json   = await File.ReadAllTextAsync(_preflightPath);
        var report = JsonSerializer.Deserialize<PreflightReport>(json, ReadOptions);

        Assert.NotNull(report);
        Assert.Equal(["python"], report!.ProjectTypes);
        Assert.Equal(["go"], report.MissingRuntimes);
        Assert.True(report.GitRepo);
        Assert.False(report.GitClean);
        Assert.Equal(["no requirements.txt found"], report.Warnings);

        Assert.Equal("3.12.1", report.RuntimeVersions["python3"]);
        // Malformed entry (no ": " separator) degrades gracefully: whole string becomes the
        // key with an empty version, rather than throwing.
        Assert.True(report.RuntimeVersions.ContainsKey("malformed entry with no colon"));
        Assert.Equal(string.Empty, report.RuntimeVersions["malformed entry with no colon"]);
    }

    [Fact]
    public async Task WriteFilePreflight_GitCleanNull_RoundTripsAsNull()
    {
        var plugin = NewPlugin();

        await plugin.WriteFilePreflightAsync(gitRepo: false, gitClean: null);

        var json   = await File.ReadAllTextAsync(_preflightPath);
        var report = JsonSerializer.Deserialize<PreflightReport>(json, ReadOptions);

        Assert.NotNull(report);
        Assert.Null(report!.GitClean);
    }

    [Fact]
    public async Task WriteFilePreflight_CreatesParentDirectoryIfMissing()
    {
        Assert.False(Directory.Exists(Path.GetDirectoryName(_preflightPath)));

        var plugin = NewPlugin();
        await plugin.WriteFilePreflightAsync(gitRepo: false);

        Assert.True(File.Exists(_preflightPath));
    }
}
