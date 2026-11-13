using fuseraft.Infrastructure.Context;

namespace FuseraftCli.Tests;

/// <summary>The ledger reports each change to a file the agent has seen once, and says the agent's copy is stale until it looks again.</summary>
public sealed class FileLedgerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fuseraft_ledger_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FileLedger _ledger = new();

    public FileLedgerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Seen(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        _ledger.RecordSeen(path);
        return path;
    }

    [Fact]
    public void Sweep_UnchangedFile_ReportsNothing()
    {
        Seen("a.txt", "one\n");

        Assert.Empty(_ledger.Sweep());
    }

    [Fact]
    public void Sweep_ChangedFile_IsReportedOnce()
    {
        var path = Seen("a.txt", "one\n");
        File.WriteAllText(path, "one\ntwo\n");

        var change = Assert.Single(_ledger.Sweep());

        Assert.Equal(path, change.Path);
        Assert.False(change.Deleted);
        Assert.Empty(_ledger.Sweep());
    }

    [Fact]
    public void Sweep_ASameSizeEditInTheSameClockTick_IsStillReported()
    {
        var path = Seen("a.txt", "one\n");
        var time = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "ONE\n");
        // A coarse clock (about 15 ms on Windows) gives a write right after the read the same time.
        File.SetLastWriteTimeUtc(path, time);

        Assert.Single(_ledger.Sweep());
        Assert.True(_ledger.IsStale(path));
        Assert.Empty(_ledger.Sweep());
    }

    [Fact]
    public void Sweep_DeletedFile_IsReportedAndForgotten()
    {
        var path = Seen("a.txt", "one\n");
        File.Delete(path);

        Assert.True(Assert.Single(_ledger.Sweep()).Deleted);
        Assert.Empty(_ledger.Sweep());
        Assert.False(_ledger.IsStale(path));
    }

    [Fact]
    public void Sweep_TouchedButSameBytes_ReportsNothing()
    {
        var path = Seen("a.txt", "one\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        Assert.Empty(_ledger.Sweep());
        Assert.False(_ledger.IsStale(path));
    }

    [Fact]
    public void IsStale_FromTheChangeUntilTheFileIsSeenAgain()
    {
        var path = Seen("a.txt", "one\n");
        Assert.False(_ledger.IsStale(path));

        File.WriteAllText(path, "one\ntwo\n");
        Assert.True(_ledger.IsStale(path));
        _ledger.Sweep();
        Assert.True(_ledger.IsStale(path));

        _ledger.RecordSeen(path);
        Assert.False(_ledger.IsStale(path));
    }

    [Fact]
    public void IsStale_FileNeverSeen_IsFalse() =>
        Assert.False(_ledger.IsStale(Path.Combine(_dir, "unknown.txt")));

    [Fact]
    public void ToolChanges_AreKeptOnlyWhileCollecting()
    {
        var change = new FileChange("/x", Deleted: false);

        _ledger.NoteToolChanges([change]);
        Assert.Empty(_ledger.TakeToolChanges());

        _ledger.CollectToolChanges = true;
        _ledger.NoteToolChanges([change]);
        Assert.Single(_ledger.TakeToolChanges());
        Assert.Empty(_ledger.TakeToolChanges());
    }

    [Fact]
    public void TrackedFiles_KeepsOnlyTheFilesSeenMostRecently()
    {
        _ledger.TrackedFiles = 2;
        var a = Seen("a.txt", "a\n");
        var b = Seen("b.txt", "b\n");
        var c = Seen("c.txt", "c\n");
        foreach (var path in new[] { a, b, c }) File.WriteAllText(path, "changed\n");

        Assert.Equal([b, c], _ledger.Sweep().Select(ch => ch.Path).Order());
        Assert.False(_ledger.IsStale(a));
    }

    [Fact]
    public void TrackedFiles_Zero_TurnsTrackingOff()
    {
        _ledger.TrackedFiles = 0;
        var a = Seen("a.txt", "a\n");
        File.WriteAllText(a, "changed\n");

        Assert.Empty(_ledger.Sweep());
        Assert.False(_ledger.IsStale(a));
    }

    [Fact]
    public void Clear_ForgetsEverySeenFile()
    {
        var path = Seen("a.txt", "one\n");
        _ledger.Clear();
        File.WriteAllText(path, "one\ntwo\n");

        Assert.Empty(_ledger.Sweep());
        Assert.False(_ledger.IsStale(path));
    }
}
