using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace FuseraftCli.Tests;

public sealed class JsonSessionStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"fuseraft_tests_{Guid.NewGuid():N}");
    private readonly JsonSessionStore _store;

    public JsonSessionStoreTests()
    {
        _store = new JsonSessionStore(NullLogger<JsonSessionStore>.Instance, _tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // SaveAsync / LoadAsync round-trip

    [Fact]
    public async Task RoundTrip_PreservesAllCheckpointFields()
    {
        var original = MakeCheckpoint("abc12300");

        await _store.SaveAsync(original);
        var loaded = await _store.LoadAsync("abc12300");

        Assert.NotNull(loaded);
        Assert.Equal(original.SessionId,  loaded!.SessionId);
        Assert.Equal(original.Task,       loaded.Task);
        Assert.Equal(original.ConfigPath, loaded.ConfigPath);
        Assert.Equal(original.IsComplete, loaded.IsComplete);
        Assert.Single(loaded.Messages);
        Assert.Equal(original.Messages[0].AgentName, loaded.Messages[0].AgentName);
        Assert.Equal(original.Messages[0].Content,   loaded.Messages[0].Content);
        Assert.Equal(original.Messages[0].Role,      loaded.Messages[0].Role);
        Assert.Equal(original.Messages[0].TurnIndex, loaded.Messages[0].TurnIndex);
    }

    [Fact]
    public async Task SaveAsync_UpdatesLastUpdatedAt()
    {
        var checkpoint = MakeCheckpoint("a5001000");
        var before = DateTime.UtcNow.AddSeconds(-1);

        await _store.SaveAsync(checkpoint);

        Assert.True(checkpoint.LastUpdatedAt >= before);
    }

    [Fact]
    public async Task SaveAsync_CreatesFileOnDisk()
    {
        var checkpoint = MakeCheckpoint("f11e7e57");

        await _store.SaveAsync(checkpoint);

        var expectedPath = Path.Combine(_tempDir, "f11e7e57.json");
        Assert.True(File.Exists(expectedPath));
    }

    // LoadAsync — missing session

    [Fact]
    public async Task LoadAsync_ReturnsNull_WhenSessionDoesNotExist()
    {
        var result = await _store.LoadAsync("d0e5f0e1");

        Assert.Null(result);
    }

    // DeleteAsync

    [Fact]
    public async Task DeleteAsync_RemovesFile()
    {
        var checkpoint = MakeCheckpoint("de100100");
        await _store.SaveAsync(checkpoint);

        await _store.DeleteAsync("de100100");

        var reloaded = await _store.LoadAsync("de100100");
        Assert.Null(reloaded);
    }

    [Fact]
    public async Task DeleteAsync_DoesNotThrow_WhenSessionDoesNotExist()
    {
        var ex = await Record.ExceptionAsync(() => _store.DeleteAsync("ab057000"));

        Assert.Null(ex);
    }

    // ListAsync

    [Fact]
    public async Task ListAsync_ReturnsAllSavedCheckpoints()
    {
        await _store.SaveAsync(MakeCheckpoint("11570001"));
        await _store.SaveAsync(MakeCheckpoint("11570002"));
        await _store.SaveAsync(MakeCheckpoint("11570003"));

        var all = await _store.ListAsync();

        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task ListAsync_ReturnsMostRecentFirst()
    {
        var first  = MakeCheckpoint("0ade0001");
        var second = MakeCheckpoint("0ade0002");

        await _store.SaveAsync(first);
        await Task.Delay(10);   // ensure timestamps differ
        await _store.SaveAsync(second);

        var all = await _store.ListAsync();

        Assert.Equal("0ade0002", all[0].SessionId);
        Assert.Equal("0ade0001", all[1].SessionId);
    }

    [Fact]
    public async Task ListAsync_ReturnsEmpty_WhenNoSessionsExist()
    {
        var all = await _store.ListAsync();

        Assert.Empty(all);
    }

    // Helpers

    private static SessionCheckpoint MakeCheckpoint(string id) => new()
    {
        SessionId  = id,
        Task       = $"Test task for {id}",
        ConfigPath = "config/test.json",
        IsComplete = false,
        Messages   =
        [
            new AgentMessage
            {
                AgentName = "TestAgent",
                Content   = "Hello from the test agent.",
                Role      = "assistant",
                TurnIndex = 0
            }
        ]
    };
}
