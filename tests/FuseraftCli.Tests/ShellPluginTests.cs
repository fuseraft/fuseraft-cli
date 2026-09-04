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

    // LooksLikeShellMismatch — Windows cmd.exe/PowerShell fallback detection

    [Theory]
    [InlineData("'Get-ChildItem' is not recognized as an internal or external command, operable program or batch file.")]
    [InlineData("'Where-Object' is not recognized as an internal or external command, operable program or batch file.")]
    [InlineData("'$env:PATH' is not recognized as an internal or external command, operable program or batch file.")]
    public void LooksLikeShellMismatch_CmdUnrecognizedCommandOnFailure_ReturnsTrue(string stderr)
    {
        var result = new ProcessResult(string.Empty, stderr, 1);
        Assert.True(ShellPlugin.LooksLikeShellMismatch(result));
    }

    [Fact]
    public void LooksLikeShellMismatch_MatchesInStdoutToo()
    {
        var result = new ProcessResult(
            "'Test-Path' is not recognized as an internal or external command, operable program or batch file.",
            string.Empty, 1);
        Assert.True(ShellPlugin.LooksLikeShellMismatch(result));
    }

    [Fact]
    public void LooksLikeShellMismatch_SuccessfulResult_ReturnsFalseEvenIfTextMatches()
    {
        // Exit code 0 means the command succeeded — never second-guess a success.
        var result = new ProcessResult(
            "'foo' is not recognized as an internal or external command, operable program or batch file.",
            string.Empty, 0);
        Assert.False(ShellPlugin.LooksLikeShellMismatch(result));
    }

    [Fact]
    public void LooksLikeShellMismatch_UnrelatedFailure_ReturnsFalse()
    {
        var result = new ProcessResult(string.Empty, "fatal: not a git repository", 128);
        Assert.False(ShellPlugin.LooksLikeShellMismatch(result));
    }

    // RunBackgroundAsync — regression coverage for the process-start refactor that added the
    // Windows cmd.exe/PowerShell mismatch retry (the retry itself only triggers on Windows).

    [Fact]
    public async Task RunBackgroundAsync_StartsJobAndReportsCompletion()
    {
        using var plugin = new ShellPlugin();

        var started = await plugin.RunBackgroundAsync("echo background-job-output");
        Assert.Contains("[OK]", started);
        Assert.Contains("Job ID:", started);

        var jobId = started.Split("Job ID: ")[1].Split('\n')[0].Trim();

        string status = "";
        for (var i = 0; i < 50 && !status.Contains("COMPLETED"); i++)
        {
            status = plugin.GetJobStatus(jobId);
            if (!status.Contains("COMPLETED")) await Task.Delay(50);
        }

        Assert.Contains("[COMPLETED]", status);
        Assert.Contains("background-job-output", plugin.GetJobOutput(jobId));
    }

    [Fact]
    public async Task RunBackgroundAsync_FailedCommand_ReportsFailureNotMismatch()
    {
        using var plugin = new ShellPlugin();

        var started = await plugin.RunBackgroundAsync("exit 7");
        var jobId = started.Split("Job ID: ")[1].Split('\n')[0].Trim();

        string status = "";
        for (var i = 0; i < 50 && !status.Contains("FAILED"); i++)
        {
            status = plugin.GetJobStatus(jobId);
            if (!status.Contains("FAILED")) await Task.Delay(50);
        }

        Assert.Contains("[FAILED]", status);
        Assert.Contains("exited 7", status);
    }
}
