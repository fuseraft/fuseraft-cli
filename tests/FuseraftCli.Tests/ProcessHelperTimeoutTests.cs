using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>A command that times out still shows what it printed, so the model can see where it was stuck.</summary>
public sealed class ProcessHelperTimeoutTests
{
    [Fact]
    public async Task ATimedOutCommand_KeepsItsOutput()
    {
        if (OperatingSystem.IsWindows()) return; // uses POSIX sh

        // Like a test run that hangs on its third test: a live run saw only "[TIMEOUT]" and had to guess which one.
        var result = await ProcessHelper.RunAsync(
            "sh", ["-c", "echo 'test_one PASSED'; echo 'test_two PASSED'; echo 'test_slow ...'; echo warming up >&2; sleep 30"],
            timeoutSeconds: 2);

        var output = result.ToPluginOutput();
        Assert.StartsWith("[EXIT -1]", output);
        Assert.Contains("test_slow ...", output);
        Assert.Contains("warming up", output);
        Assert.Contains("[TIMEOUT] Process exceeded 2s limit", output);
        Assert.Contains("what it printed before it was stopped", output);
    }

    [Fact]
    public async Task ASilentTimeout_SaysOnlyThatItTimedOut()
    {
        if (OperatingSystem.IsWindows()) return;

        var result = await ProcessHelper.RunAsync("sh", ["-c", "sleep 30"], timeoutSeconds: 1);

        Assert.Equal("[TIMEOUT] Process exceeded 1s limit.", result.Stderr);
        Assert.Equal(string.Empty, result.Stdout);
    }

    [Fact]
    public async Task ACommandThatFinishes_ReturnsAllItsOutput()
    {
        if (OperatingSystem.IsWindows()) return;

        var result = await ProcessHelper.RunAsync("sh", ["-c", "seq 1 20000; echo done >&2"], timeoutSeconds: 30);

        Assert.Equal(0, result.ExitCode);
        Assert.EndsWith("19999\n20000\n", result.Stdout);
        Assert.Equal("done\n", result.Stderr);
    }
}
