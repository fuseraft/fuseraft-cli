using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// shell_run must never block a turn forever: a timeout of 0 used to mean "no limit", so a model
/// that started a server with timeoutSeconds=0 hung the REPL, and Ctrl+C left the child running.
/// </summary>
public sealed class ShellPluginTimeoutTests
{
    [Theory]
    [InlineData(0,   60, 60)]
    [InlineData(-5,  60, 60)]
    [InlineData(10,  60, 10)]
    [InlineData(0,  120, 120)]
    public void EffectiveTimeout_NonPositiveFallsBackToDefault(int requested, int fallback, int expected)
    {
        Assert.Equal(expected, ShellPlugin.EffectiveTimeout(requested, fallback));
    }

    [Fact]
    public async Task RunAsync_Cancelled_KillsProcessAndThrows()
    {
        if (OperatingSystem.IsWindows()) return; // uses POSIX sh and kill -0

        var pidFile = Path.Combine(Path.GetTempPath(), $"fuseraft-cancel-{Guid.NewGuid():N}.pid");
        try
        {
            using var shell = new ShellPlugin();
            using var cts   = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var started     = DateTime.UtcNow;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                shell.RunAsync($"echo $$ > '{pidFile}'; exec sleep 30", timeoutSeconds: 0, cancellationToken: cts.Token));

            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "cancellation did not end the wait");

            var pid   = File.ReadAllText(pidFile).Trim();
            var alive = await ProcessHelper.RunAsync("sh", ["-c", $"kill -0 {pid}"], timeoutSeconds: 5);
            Assert.False(alive.Succeeded, $"process {pid} survived cancellation");
        }
        finally
        {
            try { File.Delete(pidFile); } catch { }
        }
    }
}
