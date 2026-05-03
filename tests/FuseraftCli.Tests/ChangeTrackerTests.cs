using System.Text.Json;
using fuseraft.Core.Models;
using fuseraft.Orchestration;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="ChangeTracker"/> and the <see cref="ChangeLog"/> model.
///
/// The middleware capture path (CapturingMiddleware) requires a live MAF agent
/// invocation and is covered by integration tests. These tests cover the parts
/// that are independently testable:
/// <list type="bullet">
///   <item><see cref="ChangeTracker.SetSessionIdAsync"/> — file I/O and session stamping.</item>
///   <item>CommandRecord Output serialisation / deserialisation — backward-compat contract.</item>
///   <item>FlushTurnAsync with an empty queue — must be a no-op.</item>
/// </list>
/// </summary>
public sealed class ChangeTrackerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fuseraft_ct_{Guid.NewGuid():N}");
    private string LogPath => Path.Combine(_dir, "changes.json");

    public ChangeTrackerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ChangeTracker Tracker() => new(LogPath);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private async Task<ChangeLog> ReadLog()
    {
        var json = await File.ReadAllTextAsync(LogPath);
        return JsonSerializer.Deserialize<ChangeLog>(json, JsonOpts) ?? new ChangeLog();
    }

    // -------------------------------------------------------------------------
    // SetSessionIdAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SetSessionId_CreatesFile_WhenMissing()
    {
        await Tracker().SetSessionIdAsync("abc12345");

        Assert.True(File.Exists(LogPath));
        var log = await ReadLog();
        Assert.Equal("abc12345", log.ActiveSessionId);
    }

    [Fact]
    public async Task SetSessionId_OverwritesExistingSessionId()
    {
        var tracker = Tracker();
        await tracker.SetSessionIdAsync("session-1");
        await tracker.SetSessionIdAsync("session-2");

        var log = await ReadLog();
        Assert.Equal("session-2", log.ActiveSessionId);
    }

    [Fact]
    public async Task SetSessionId_PreservesExistingEntries()
    {
        // Simulate a log that already has entries from a prior session.
        var existing = new ChangeLog
        {
            ActiveSessionId = "old",
            Entries =
            [
                new ChangeEntry
                {
                    Agent     = "Developer",
                    TurnIndex = 0,
                    Timestamp = DateTime.UtcNow,
                    SessionId = "old",
                    FilesWritten = ["main.go"]
                }
            ]
        };
        await File.WriteAllTextAsync(LogPath, JsonSerializer.Serialize(existing));

        await Tracker().SetSessionIdAsync("new-session");

        var log = await ReadLog();
        Assert.Equal("new-session", log.ActiveSessionId);
        Assert.Single(log.Entries);  // existing entry must not be lost
        Assert.Equal("old", log.Entries[0].SessionId);
    }

    // -------------------------------------------------------------------------
    // FlushTurnAsync — empty queue
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FlushTurnAsync_EmptyQueue_DoesNotWriteFile()
    {
        var tracker = Tracker();
        await tracker.FlushTurnAsync("Developer", 0);

        Assert.False(File.Exists(LogPath));
    }

    // -------------------------------------------------------------------------
    // CommandRecord Output — serialisation contract
    // -------------------------------------------------------------------------

    [Fact]
    public void CommandRecord_Output_SerializesToJson()
    {
        var record = new CommandRecord { Command = "go test ./...", Succeeded = true, Output = "ok  \twebapp\t0.017s" };

        var json = JsonSerializer.Serialize(record);

        Assert.Contains("\"Output\"", json);
        Assert.Contains("ok", json);
    }

    [Fact]
    public void CommandRecord_NullOutput_OmittedFromJson()
    {
        // Output=null must be omitted by WhenWritingNull so old records stay compact
        // and backward-compatible parsers don't see an unexpected key.
        var log = new ChangeLog
        {
            Entries = [
                new ChangeEntry
                {
                    Agent       = "Developer",
                    TurnIndex   = 0,
                    Timestamp   = DateTime.UtcNow,
                    CommandsRun = [new CommandRecord { Command = "go build ./...", Succeeded = true }]
                }
            ]
        };
        var opts = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        var json = JsonSerializer.Serialize(log, opts);

        Assert.DoesNotContain("\"Output\"", json);
    }

    [Fact]
    public void CommandRecord_NullOutput_DeserializesFromLegacyJson()
    {
        // Records written before output capture was introduced have no Output field.
        // They must deserialise without error and Output must be null.
        const string legacy = """{"Command":"go test ./...","succeeded":true}""";

        var record = JsonSerializer.Deserialize<CommandRecord>(legacy, JsonOpts);

        Assert.NotNull(record);
        Assert.Equal("go test ./...", record!.Command);
        Assert.True(record.Succeeded);
        Assert.Null(record.Output);
    }

    [Fact]
    public void CommandRecord_WithOutput_RoundTrips()
    {
        const string output = "=== RUN TestFoo\n--- PASS: TestFoo (0.00s)\nok  webapp  0.013s";
        var original = new CommandRecord { Command = "go test ./...", Succeeded = true, Output = output };

        var json   = JsonSerializer.Serialize(original);
        var parsed = JsonSerializer.Deserialize<CommandRecord>(json, JsonOpts);

        Assert.Equal(original.Command,   parsed!.Command);
        Assert.Equal(original.Succeeded, parsed.Succeeded);
        Assert.Equal(original.Output,    parsed.Output);
    }

    // -------------------------------------------------------------------------
    // ChangeLog ActiveSessionId — cross-session contamination guard
    // -------------------------------------------------------------------------

    [Fact]
    public void ChangeLog_ActiveSessionId_NullByDefault()
    {
        var log = new ChangeLog();
        Assert.Null(log.ActiveSessionId);
    }

    [Fact]
    public async Task SetSessionId_TwoDifferentTrackers_ShareTheSameFile_SecondWins()
    {
        // Simulates a restart where a new ChangeTracker instance opens an existing file.
        await Tracker().SetSessionIdAsync("first");
        await Tracker().SetSessionIdAsync("second");

        var log = await ReadLog();
        Assert.Equal("second", log.ActiveSessionId);
    }
}
