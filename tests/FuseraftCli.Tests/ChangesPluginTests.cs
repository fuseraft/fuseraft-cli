using System.Text.Json;
using fuseraft.Core.Models.Session;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="ChangesPlugin"/> — the read-only view over the session change log
/// written by <c>ChangeTracker</c>.
/// </summary>
public sealed class ChangesPluginTests : IDisposable
{
    private readonly string _root;
    private readonly string _logPath;

    public ChangesPluginTests()
    {
        _root    = Path.Combine(Path.GetTempPath(), "fuseraft_changes_tests_" + Guid.NewGuid().ToString("N")[..8]);
        _logPath = Path.Combine(_root, "changes.json");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private async Task WriteLogAsync(ChangeLog log) =>
        await File.WriteAllTextAsync(_logPath, JsonSerializer.Serialize(log));

    [Fact]
    public async Task ReadAsync_NoLogFileYet_ReturnsInfoMessage()
    {
        var plugin = new ChangesPlugin(_logPath);

        var result = await plugin.ReadAsync();

        Assert.StartsWith("[INFO]", result);
        Assert.Contains("No changes have been recorded", result);
    }

    [Fact]
    public async Task ReadAsync_EmptyEntries_ReturnsInfoMessage()
    {
        await WriteLogAsync(new ChangeLog { Entries = [] });
        var plugin = new ChangesPlugin(_logPath);

        var result = await plugin.ReadAsync();

        Assert.StartsWith("[INFO]", result);
    }

    [Fact]
    public async Task ReadAsync_MalformedJson_TreatedAsNoLog()
    {
        await File.WriteAllTextAsync(_logPath, "{not valid json");
        var plugin = new ChangesPlugin(_logPath);

        var result = await plugin.ReadAsync();

        Assert.StartsWith("[INFO]", result);
    }

    [Fact]
    public async Task ReadAsync_WithEntries_FormatsFilesCommandsAndCommits()
    {
        await WriteLogAsync(new ChangeLog
        {
            Entries =
            [
                new ChangeEntry
                {
                    Agent        = "Developer",
                    TurnIndex    = 2,
                    Timestamp    = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                    FilesWritten = ["src/foo.cs"],
                    FilesDeleted = ["src/old.cs"],
                    CommandsRun  = [new CommandRecord { Command = "dotnet build", Succeeded = true }],
                    GitCommits   = ["fix: correct the thing"],
                },
            ],
        });
        var plugin = new ChangesPlugin(_logPath);

        var result = await plugin.ReadAsync();

        Assert.Contains("=== Changes Log ===", result);
        Assert.Contains("[Turn 2] Developer", result);
        Assert.Contains("src/foo.cs", result);
        Assert.Contains("src/old.cs", result);
        Assert.Contains("dotnet build", result);
        Assert.Contains("[OK]", result);
        Assert.Contains("fix: correct the thing", result);
    }

    [Fact]
    public async Task ReadAsync_FailedCommand_MarkedFailedNotOk()
    {
        await WriteLogAsync(new ChangeLog
        {
            Entries =
            [
                new ChangeEntry
                {
                    Agent       = "Developer",
                    TurnIndex   = 1,
                    Timestamp   = DateTime.UtcNow,
                    CommandsRun = [new CommandRecord { Command = "dotnet test", Succeeded = false }],
                },
            ],
        });
        var plugin = new ChangesPlugin(_logPath);

        var result = await plugin.ReadAsync();

        Assert.Contains("dotnet test  [FAILED]", result);
    }

    [Fact]
    public async Task ReadLatestAsync_DefaultCount_ReturnsOnlyMostRecentEntry()
    {
        await WriteLogAsync(new ChangeLog
        {
            Entries =
            [
                new ChangeEntry { Agent = "First",  TurnIndex = 1, Timestamp = DateTime.UtcNow },
                new ChangeEntry { Agent = "Second", TurnIndex = 2, Timestamp = DateTime.UtcNow },
            ],
        });
        var plugin = new ChangesPlugin(_logPath);

        var result = await plugin.ReadLatestAsync();

        Assert.DoesNotContain("First", result);
        Assert.Contains("Second", result);
    }

    [Fact]
    public async Task ReadLatestAsync_CountGreaterThanEntries_ReturnsAllEntries()
    {
        await WriteLogAsync(new ChangeLog
        {
            Entries =
            [
                new ChangeEntry { Agent = "First",  TurnIndex = 1, Timestamp = DateTime.UtcNow },
                new ChangeEntry { Agent = "Second", TurnIndex = 2, Timestamp = DateTime.UtcNow },
            ],
        });
        var plugin = new ChangesPlugin(_logPath);

        var result = await plugin.ReadLatestAsync(count: 10);

        Assert.Contains("First", result);
        Assert.Contains("Second", result);
    }

    [Fact]
    public async Task ReadLatestAsync_ZeroOrNegativeCount_TreatedAsAtLeastOne()
    {
        await WriteLogAsync(new ChangeLog
        {
            Entries =
            [
                new ChangeEntry { Agent = "First",  TurnIndex = 1, Timestamp = DateTime.UtcNow },
                new ChangeEntry { Agent = "Second", TurnIndex = 2, Timestamp = DateTime.UtcNow },
            ],
        });
        var plugin = new ChangesPlugin(_logPath);

        var result = await plugin.ReadLatestAsync(count: 0);

        Assert.Contains("Second", result);
        Assert.DoesNotContain("First", result);
    }

    [Fact]
    public void ArtifactPath_ReturnsConfiguredLogPath()
    {
        var plugin = new ChangesPlugin(_logPath);
        Assert.Equal(_logPath, plugin.ArtifactPath);
    }
}
