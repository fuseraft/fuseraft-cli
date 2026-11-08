using fuseraft.Cli.Commands.Schedule;
using fuseraft.Core.Models.Config;

namespace FuseraftCli.Tests;

public sealed class ScheduleRunCommandTests
{
    [Fact]
    public void BuildArgs_RunsTheTaskWithFuseraftRun()
    {
        var args = ScheduleRunCommand.BuildArgs(new ScheduledJob { Name = "j", Task = "Audit", Config = "c.yaml", WorkDir = "/w" });

        Assert.Equal(["run", "Audit", "--no-banner", "--config", "c.yaml", "--work-dir", "/w"], args);
    }
}
