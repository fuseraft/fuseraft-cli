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
    public void Sweep_ChangedFile_IsReportedOnce_WithBothVersions()
    {
        var path = Seen("a.txt", "one\n");
        File.WriteAllText(path, "one\ntwo\n");

        var change = Assert.Single(_ledger.Sweep());

        Assert.Equal(path, change.Path);
        Assert.Equal("one\n", change.Before);
        Assert.Equal("one\ntwo\n", change.After);
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
    public void Sweep_NamesASecretTheChangeAdded()
    {
        const string name = "FUSERAFT_TEST_LEDGER_TOKEN";
        Environment.SetEnvironmentVariable(name, "ledger-test-0123456789abcdef");
        try
        {
            var path = Seen("a.txt", "one\n");
            File.WriteAllText(path, "one\ntoken=ledger-test-0123456789abcdef\n");

            Assert.Contains(name, Assert.Single(_ledger.Sweep()).NewSecrets);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public void ToolChanges_AreKeptOnlyWhileCollecting()
    {
        var change = new FileChange("/x", Deleted: false, "a", "b", new SortedSet<string>());

        _ledger.NoteToolChanges([change]);
        Assert.Empty(_ledger.TakeToolChanges());

        _ledger.CollectToolChanges = true;
        _ledger.NoteToolChanges([change]);
        Assert.Single(_ledger.TakeToolChanges());
        Assert.Empty(_ledger.TakeToolChanges());
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
