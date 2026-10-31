using fuseraft.Cli.Commands.Schedule;
using fuseraft.Core;
using fuseraft.Core.Models.Config;

namespace FuseraftCli.Tests;

[Collection("FuseraftHomeEnv")]
public sealed class ScheduleToggleCommandTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");

    public ScheduleToggleCommandTests() =>
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _home);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private static string JobPath(string name) => Path.Combine(FuseraftPaths.GlobalSchedule, $"{name}.yaml");

    private static void Save(ScheduledJob job)
    {
        Directory.CreateDirectory(FuseraftPaths.GlobalSchedule);
        File.WriteAllText(JobPath(job.Name), ScheduleUtil.Serialize(job));
    }

    private static ScheduledJob Load(string name) => ScheduleUtil.Deserialize(File.ReadAllText(JobPath(name)))!;

    private static Task<int> RunAsync(ScheduleToggleCommand command, string name) =>
        command.ExecuteAsync(null!, new ScheduleToggleSettings { Name = name }, CancellationToken.None);

    [Fact]
    public async Task Disable_KeepsTheJobAndItsSchedule()
    {
        var next = DateTimeOffset.UtcNow.AddHours(3);
        Save(new ScheduledJob { Name = "nightly", Cron = "0 2 * * *", Task = "t", Enabled = true, NextRun = next });

        Assert.Equal(0, await RunAsync(new ScheduleDisableCommand(), "nightly"));

        var job = Load("nightly");
        Assert.False(job.Enabled);
        Assert.Equal("t", job.Task);
        Assert.Equal(next.ToUnixTimeSeconds(), job.NextRun!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Enable_ResumesFromNow_NotFromAMissedRun()
    {
        Save(new ScheduledJob { Name = "nightly", Cron = "0 2 * * *", Task = "t", Enabled = false, NextRun = DateTimeOffset.UtcNow.AddDays(-5) });

        Assert.Equal(0, await RunAsync(new ScheduleEnableCommand(), "nightly"));

        var job = Load("nightly");
        Assert.True(job.Enabled);
        Assert.True(job.NextRun > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task UnknownJob_Fails()
    {
        Assert.Equal(1, await RunAsync(new ScheduleEnableCommand(), "absent"));
    }
}
