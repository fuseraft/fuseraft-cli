using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Runs on its own, after every other collection: <see cref="ProcessHelper.KillRunning"/> kills
/// every command in flight, which would include other test classes' processes.
/// </summary>
[CollectionDefinition("KillRunning", DisableParallelization = true)]
public sealed class KillRunningCollection;

/// <summary>
/// The second Ctrl+C on a stuck turn: a command a tool waits on with no timeout and no
/// cancellation must still end when the REPL kills running commands.
/// </summary>
[Collection("KillRunning")]
public sealed class ProcessHelperKillRunningTests
{
    [Fact]
    public async Task KillRunning_EndsAWaitWithNoTimeoutOrCancellation()
    {
        if (OperatingSystem.IsWindows()) return; // uses POSIX sh

        var run = ProcessHelper.RunAsync("sh", ["-c", "sleep 30"], timeoutSeconds: 0);

        // Wait for the command to be registered before killing it.
        var killed = 0;
        for (var i = 0; i < 50 && killed == 0; i++)
        {
            await Task.Delay(100);
            killed = ProcessHelper.KillRunning();
        }

        Assert.Equal(1, killed);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(run, finished);
        Assert.False((await run).Succeeded);
        Assert.Equal(0, ProcessHelper.KillRunning());
    }
}
