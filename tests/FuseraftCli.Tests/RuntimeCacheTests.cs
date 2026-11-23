using fuseraft.Cli.Commands;

namespace FuseraftCli.Tests;

public sealed class RuntimeCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fuseraft-runtime-cache-test-{Guid.NewGuid():N}");

    public RuntimeCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void RootFor_FollowsTheDotnetHostsRules()
    {
        var home = Path.Combine(_dir, "home");
        var temp = Path.Combine(_dir, "temp");

        Assert.Equal(Path.Combine(home, ".net", "fuseraft"), RuntimeCache.RootFor(null, windows: false, home, temp));
        Assert.Equal(Path.Combine(temp, ".net", "fuseraft"), RuntimeCache.RootFor("", windows: true, home, temp));
        // The override is used as is, without a .net folder.
        Assert.Equal(Path.Combine(_dir, "x", "fuseraft"), RuntimeCache.RootFor(Path.Combine(_dir, "x"), windows: false, home, temp));
    }

    [Fact]
    public void CurrentFrom_IsTheSearchDirectoryDirectlyUnderTheRoot()
    {
        var root  = Path.Combine(_dir, "fuseraft");
        var build = Path.Combine(root, "vQdFZGVltuBB");
        var dirs  = string.Join(Path.PathSeparator, Path.Combine(_dir, "app") + Path.DirectorySeparatorChar, build + Path.DirectorySeparatorChar);

        Assert.Equal(build, RuntimeCache.CurrentFrom(dirs, root));
        Assert.Null(RuntimeCache.CurrentFrom(Path.Combine(_dir, "app"), root));
        Assert.Null(RuntimeCache.CurrentFrom(Path.Combine(build, "nested"), root));
        Assert.Null(RuntimeCache.CurrentFrom(null, root));
    }

    [Fact]
    public void PruneExcept_DeletesEveryOtherBuild_AndCountsWhatItFreed()
    {
        var root = Path.Combine(_dir, "fuseraft");
        foreach (var build in new[] { "old1", "old2", "current" })
        {
            Directory.CreateDirectory(Path.Combine(root, build));
            File.WriteAllText(Path.Combine(root, build, "libe_sqlite3.so"), "12345");
        }

        var (count, bytes) = RuntimeCache.PruneExcept(root, Path.Combine(root, "current"));

        Assert.Equal(2, count);
        Assert.Equal(10, bytes);
        Assert.Equal([Path.Combine(root, "current")], Directory.GetDirectories(root));
    }

    [Fact]
    public void PruneExcept_WithNoCache_DoesNothing() =>
        Assert.Equal((0, 0L), RuntimeCache.PruneExcept(Path.Combine(_dir, "missing"), "x"));

    [Theory]
    [InlineData(0, "empty")]
    [InlineData(10, "1 KB")]
    [InlineData(3 * 1024 * 1024, "3 MB")]
    public void FormatBytes_PicksAUnit(long bytes, string expected) =>
        Assert.Equal(expected, RuntimeCache.FormatBytes(bytes));
}
