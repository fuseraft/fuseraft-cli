using fuseraft.Infrastructure.Context;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>write_file is refused on a file that changed since the agent last read or wrote it, until it reads it again.</summary>
public sealed class FileSystemPluginStaleWriteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fuseraft_stale_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FileLedger _ledger = new();
    private readonly FileSystemPlugin _plugin;

    public FileSystemPluginStaleWriteTests()
    {
        Directory.CreateDirectory(_dir);
        _plugin = new FileSystemPlugin(sandboxRoot: _dir, ledger: _ledger);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathOf(string file) => Path.Combine(_dir, file);

    [Fact]
    public async Task WriteFile_AfterAnOutsideChange_IsRefused_UntilReRead()
    {
        File.WriteAllText(PathOf("a.txt"), "one\n");
        await _plugin.ReadFileAsync(PathOf("a.txt"));
        File.WriteAllText(PathOf("a.txt"), "one\nadded elsewhere\n");

        var refused = await _plugin.WriteFileAsync(PathOf("a.txt"), "one\nmine\n");
        Assert.Contains("WRITE BLOCKED", refused);
        Assert.Contains("added elsewhere", File.ReadAllText(PathOf("a.txt")));

        await _plugin.ReadFileAsync(PathOf("a.txt"), startLine: 1, maxLines: 5);
        Assert.DoesNotContain("[ERROR]", await _plugin.WriteFileAsync(PathOf("a.txt"), "one\nadded elsewhere\nmine\n"));
    }

    [Fact]
    public async Task WriteFile_FileNeverSeen_IsAllowed()
    {
        File.WriteAllText(PathOf("a.txt"), "one\n");

        Assert.DoesNotContain("[ERROR]", await _plugin.WriteFileAsync(PathOf("a.txt"), "two\n"));
    }

    [Fact]
    public async Task WriteFile_AfterItsOwnWrite_IsAllowed()
    {
        File.WriteAllText(PathOf("a.txt"), "zero\n");
        await _plugin.ReadFileAsync(PathOf("a.txt"));
        await _plugin.WriteFileAsync(PathOf("a.txt"), "one\n");

        Assert.DoesNotContain("[ERROR]", await _plugin.WriteFileAsync(PathOf("a.txt"), "one\ntwo\n"));
    }

    [Fact]
    public async Task PatchFile_OnAChangedFile_StillWorks()
    {
        File.WriteAllText(PathOf("a.txt"), "one\n");
        await _plugin.ReadFileAsync(PathOf("a.txt"));
        File.WriteAllText(PathOf("a.txt"), "one\nadded elsewhere\n");

        Assert.DoesNotContain("[ERROR]", await _plugin.PatchFileAsync(PathOf("a.txt"), "one", "ONE"));
        Assert.Equal("ONE\nadded elsewhere\n", File.ReadAllText(PathOf("a.txt")));
    }
}
