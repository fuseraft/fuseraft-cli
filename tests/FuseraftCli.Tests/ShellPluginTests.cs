using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

public sealed class ShellPluginTests
{
    // RunAsync — quiet parameter (folded in from the removed shell_run_quiet tool)

    [Fact]
    public async Task RunAsync_QuietOnSuccess_ReturnsOk()
    {
        using var plugin = new ShellPlugin();
        var result = await plugin.RunAsync("echo hello", quiet: true);
        Assert.Equal("OK", result);
    }

    [Fact]
    public async Task RunAsync_QuietOnFailure_ReturnsFullOutputAndExitCode()
    {
        using var plugin = new ShellPlugin();
        var result = await plugin.RunAsync("exit 3", quiet: true);
        Assert.NotEqual("OK", result);
        Assert.Contains("[EXIT 3]", result);
    }

    [Fact]
    public async Task RunAsync_NotQuiet_ReturnsFullOutputOnSuccess()
    {
        using var plugin = new ShellPlugin();
        var result = await plugin.RunAsync("echo hello-not-quiet");
        Assert.Contains("hello-not-quiet", result);
    }

    // GetSessionTempDir

    [Fact]
    public void GetSessionTempDir_CreatesDirectory()
    {
        using var plugin = new ShellPlugin();
        var path = plugin.GetSessionTempDir();
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void GetSessionTempDir_IsIdempotent()
    {
        using var plugin = new ShellPlugin();
        var first  = plugin.GetSessionTempDir();
        var second = plugin.GetSessionTempDir();
        Assert.Equal(first, second);
    }

    [Fact]
    public void GetSessionTempDir_ReturnsDistinctPathsForDifferentInstances()
    {
        using var a = new ShellPlugin();
        using var b = new ShellPlugin();
        Assert.NotEqual(a.GetSessionTempDir(), b.GetSessionTempDir());
    }

    [Fact]
    public void Dispose_DeletesSessionTempDir()
    {
        string path;
        using (var plugin = new ShellPlugin())
        {
            path = plugin.GetSessionTempDir();
            Assert.True(Directory.Exists(path));
        }
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void Dispose_WhenTempDirNeverRequested_DoesNotThrow()
    {
        var plugin = new ShellPlugin();
        var ex = Record.Exception(() => plugin.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_WhenCalledTwice_DoesNotThrow()
    {
        var plugin = new ShellPlugin();
        plugin.GetSessionTempDir();
        plugin.Dispose();
        var ex = Record.Exception(() => plugin.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void GetSessionTempDir_ConcurrentCalls_ReturnSamePath()
    {
        using var plugin = new ShellPlugin();

        var paths = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 32, _ => paths.Add(plugin.GetSessionTempDir()));

        Assert.Single(paths.Distinct());
    }
}
