using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// End-to-end tests for the REPL <c>/undo</c> mechanism, exercised through the real
/// <see cref="FileSystemPlugin"/>/<see cref="FileSystemManagementOps"/> wiring (not just
/// <see cref="UndoSnapshotStore"/> in isolation) so a mistake in the integration — e.g.
/// forgetting to call <see cref="UndoSnapshotStore.BeginTurn"/> from
/// <c>ITurnResettable.BeginTurn()</c> — would be caught.
/// </summary>
public sealed class UndoSnapshotStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _undoDir;
    private readonly FileSystemPlugin _plugin;
    private readonly FileSystemManagementOps _ops;

    public UndoSnapshotStoreTests()
    {
        _dir     = Path.Combine(Path.GetTempPath(), "fuseraft_undo_tests_" + Guid.NewGuid().ToString("N")[..8]);
        _undoDir = Path.Combine(_dir, ".undo");
        Directory.CreateDirectory(_dir);
        _plugin = new FileSystemPlugin(sandboxRoot: _dir);
        _plugin.EnableUndoSnapshots(_undoDir);
        _ops = new FileSystemManagementOps(_plugin, sandboxRoot: _dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string TempPath(string filename) => Path.Combine(_dir, filename);
    private void BeginTurn() => ((ITurnResettable)_plugin).BeginTurn();

    [Fact]
    public async Task Disabled_NoOp()
    {
        var plugin = new FileSystemPlugin(sandboxRoot: _dir); // EnableUndoSnapshots never called
        var result = await plugin.UndoStore.UndoLastTurnAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task Undo_NothingRecorded_ReturnsNull()
    {
        var result = await _plugin.UndoStore.UndoLastTurnAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task Undo_RevertsPatchedFile()
    {
        await File.WriteAllTextAsync(TempPath("a.txt"), "original");
        BeginTurn();

        await _plugin.PatchFileAsync(TempPath("a.txt"), "original", "changed");
        Assert.Equal("changed", await File.ReadAllTextAsync(TempPath("a.txt")));

        var result = await _plugin.UndoStore.UndoLastTurnAsync();
        Assert.NotNull(result);
        Assert.Equal("original", await File.ReadAllTextAsync(TempPath("a.txt")));
    }

    [Fact]
    public async Task Undo_DeletesNewlyWrittenFile()
    {
        BeginTurn();

        await _plugin.WriteFileAsync(TempPath("new.txt"), "brand new");
        Assert.True(File.Exists(TempPath("new.txt")));

        var result = await _plugin.UndoStore.UndoLastTurnAsync();
        Assert.NotNull(result);
        Assert.False(File.Exists(TempPath("new.txt")));
        Assert.Contains("did not exist", result!.Actions[0].Description);
    }

    [Fact]
    public async Task Undo_RestoresDeletedFile()
    {
        await File.WriteAllTextAsync(TempPath("gone.txt"), "keep me");
        BeginTurn();

        await _ops.DeleteFileAsync(TempPath("gone.txt"));
        Assert.False(File.Exists(TempPath("gone.txt")));

        var result = await _plugin.UndoStore.UndoLastTurnAsync();
        Assert.NotNull(result);
        Assert.Equal("keep me", await File.ReadAllTextAsync(TempPath("gone.txt")));
    }

    [Fact]
    public async Task Undo_RestoresAllFilesTouchedInSameTurn()
    {
        await File.WriteAllTextAsync(TempPath("first.txt"), "one");
        BeginTurn();

        await _plugin.PatchFileAsync(TempPath("first.txt"), "one", "ONE");
        await _plugin.WriteFileAsync(TempPath("second.txt"), "two");

        var result = await _plugin.UndoStore.UndoLastTurnAsync();
        Assert.NotNull(result);
        Assert.Equal(2, result!.Actions.Count);
        Assert.Equal("one", await File.ReadAllTextAsync(TempPath("first.txt")));
        Assert.False(File.Exists(TempPath("second.txt")));
    }

    [Fact]
    public async Task Undo_OnlySnapshotsFirstMutationPerTurn()
    {
        await File.WriteAllTextAsync(TempPath("a.txt"), "v1");
        BeginTurn();

        await _plugin.PatchFileAsync(TempPath("a.txt"), "v1", "v2");
        await _plugin.PatchFileAsync(TempPath("a.txt"), "v2", "v3");
        Assert.Equal("v3", await File.ReadAllTextAsync(TempPath("a.txt")));

        // Undo should restore to "v1" (state before the turn started), not "v2"
        // (the intermediate state between the two patches).
        var result = await _plugin.UndoStore.UndoLastTurnAsync();
        Assert.NotNull(result);
        Assert.Single(result!.Actions);
        Assert.Equal("v1", await File.ReadAllTextAsync(TempPath("a.txt")));
    }

    [Fact]
    public async Task Undo_RevertsCopyToNewDestination()
    {
        await File.WriteAllTextAsync(TempPath("src.txt"), "source content");
        BeginTurn();

        await _ops.CopyFileAsync(TempPath("src.txt"), TempPath("dst.txt"));
        Assert.True(File.Exists(TempPath("dst.txt")));

        var result = await _plugin.UndoStore.UndoLastTurnAsync();
        Assert.NotNull(result);
        // Source is read-only for a copy — only the destination should be reverted.
        Assert.Single(result!.Actions);
        Assert.False(File.Exists(TempPath("dst.txt")));
        Assert.Equal("source content", await File.ReadAllTextAsync(TempPath("src.txt")));
    }

    [Fact]
    public async Task Undo_RevertsCopyThatOverwroteExistingDestination()
    {
        await File.WriteAllTextAsync(TempPath("src.txt"), "new content");
        await File.WriteAllTextAsync(TempPath("dst.txt"), "old destination content");
        BeginTurn();

        await _ops.CopyFileAsync(TempPath("src.txt"), TempPath("dst.txt"), overwrite: true);
        Assert.Equal("new content", await File.ReadAllTextAsync(TempPath("dst.txt")));

        var result = await _plugin.UndoStore.UndoLastTurnAsync();
        Assert.NotNull(result);
        Assert.Equal("old destination content", await File.ReadAllTextAsync(TempPath("dst.txt")));
    }

    [Fact]
    public async Task Undo_RevertsMoveToNewDestination()
    {
        await File.WriteAllTextAsync(TempPath("src.txt"), "moved content");
        BeginTurn();

        await _ops.MoveFileAsync(TempPath("src.txt"), TempPath("dst.txt"));
        Assert.False(File.Exists(TempPath("src.txt")));
        Assert.True(File.Exists(TempPath("dst.txt")));

        var result = await _plugin.UndoStore.UndoLastTurnAsync();
        Assert.NotNull(result);
        Assert.Equal(2, result!.Actions.Count); // source recreated, destination removed
        Assert.Equal("moved content", await File.ReadAllTextAsync(TempPath("src.txt")));
        Assert.False(File.Exists(TempPath("dst.txt")));
    }

    [Fact]
    public async Task Undo_RevertsMoveThatOverwroteExistingDestination()
    {
        await File.WriteAllTextAsync(TempPath("src.txt"), "moved content");
        await File.WriteAllTextAsync(TempPath("dst.txt"), "old destination content");
        BeginTurn();

        await _ops.MoveFileAsync(TempPath("src.txt"), TempPath("dst.txt"), overwrite: true);

        var result = await _plugin.UndoStore.UndoLastTurnAsync();
        Assert.NotNull(result);
        Assert.Equal("moved content", await File.ReadAllTextAsync(TempPath("src.txt")));
        Assert.Equal("old destination content", await File.ReadAllTextAsync(TempPath("dst.txt")));
    }

    [Fact]
    public async Task Undo_RevertsMovedDirectory()
    {
        Directory.CreateDirectory(TempPath("srcdir"));
        await File.WriteAllTextAsync(TempPath("srcdir/a.txt"), "a");
        await File.WriteAllTextAsync(TempPath("srcdir/b.txt"), "b");
        BeginTurn();

        await _ops.MoveFileAsync(TempPath("srcdir"), TempPath("dstdir"));
        Assert.False(Directory.Exists(TempPath("srcdir")));
        Assert.True(File.Exists(TempPath("dstdir/a.txt")));

        var result = await _plugin.UndoStore.UndoLastTurnAsync();
        Assert.NotNull(result);
        Assert.Equal(4, result!.Actions.Count); // 2 files recreated at src, 2 removed from dst
        Assert.Equal("a", await File.ReadAllTextAsync(TempPath("srcdir/a.txt")));
        Assert.Equal("b", await File.ReadAllTextAsync(TempPath("srcdir/b.txt")));
        Assert.False(File.Exists(TempPath("dstdir/a.txt")));
        Assert.False(File.Exists(TempPath("dstdir/b.txt")));
    }

    [Fact]
    public async Task Undo_WalksBackOneTurnAtATime()
    {
        await File.WriteAllTextAsync(TempPath("a.txt"), "v1");

        BeginTurn();
        await _plugin.PatchFileAsync(TempPath("a.txt"), "v1", "v2");

        BeginTurn();
        await _plugin.PatchFileAsync(TempPath("a.txt"), "v2", "v3");

        // First /undo reverts the most recent turn (v3 -> v2).
        var first = await _plugin.UndoStore.UndoLastTurnAsync();
        Assert.NotNull(first);
        Assert.Equal("v2", await File.ReadAllTextAsync(TempPath("a.txt")));

        // Second /undo reverts the turn before that (v2 -> v1).
        var second = await _plugin.UndoStore.UndoLastTurnAsync();
        Assert.NotNull(second);
        Assert.Equal("v1", await File.ReadAllTextAsync(TempPath("a.txt")));

        // Nothing left to undo.
        Assert.Null(await _plugin.UndoStore.UndoLastTurnAsync());
    }
}
