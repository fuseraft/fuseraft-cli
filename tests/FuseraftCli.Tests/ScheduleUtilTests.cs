using fuseraft.Cli.Commands.Schedule;
using fuseraft.Core.Models.Config;

namespace FuseraftCli.Tests;

public sealed class ScheduleUtilTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 31, 13, 49, 6, TimeSpan.Zero);
    private static readonly DateTimeOffset Next    = new(2026, 11, 1, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RoundTrip_PreservesTimestamps()
    {
        var job = new ScheduledJob
        {
            Name = "nightly", Cron = "0 2 * * *", Task = "Audit",
            CreatedAt = Created, LastRun = Created.AddHours(-1), NextRun = Next,
        };

        var back = ScheduleUtil.Deserialize(ScheduleUtil.Serialize(job))!;

        Assert.Equal(Created, back.CreatedAt);
        Assert.Equal(Created.AddHours(-1), back.LastRun);
        Assert.Equal(Next, back.NextRun);
    }

    [Fact]
    public void Serialize_WritesOneScalarPerTimestamp()
    {
        var yaml = ScheduleUtil.Serialize(new ScheduledJob { Name = "j", CreatedAt = Created, NextRun = Next });

        Assert.Contains("next_run: 2026-11-01T02:00:00.0000000+00:00", yaml);
        Assert.DoesNotContain("utc_ticks", yaml);
    }

    [Fact]
    public void Serialize_ConvertsToUtc()
    {
        var local = new DateTimeOffset(2026, 10, 31, 21, 0, 0, TimeSpan.FromHours(-5));
        var yaml  = ScheduleUtil.Serialize(new ScheduledJob { Name = "j", NextRun = local });

        Assert.Contains("next_run: 2026-11-01T02:00:00.0000000+00:00", yaml);
    }

    [Fact]
    public void NullLastRun_StaysNull()
    {
        var back = ScheduleUtil.Deserialize(ScheduleUtil.Serialize(new ScheduledJob { Name = "j", NextRun = Next }))!;
        Assert.Null(back.LastRun);
    }

    // The exact shape `fuseraft schedule add` wrote before timestamps had a converter.
    private const string LegacyYaml = """
        name: nightly
        cron: 0 2 * * *
        task: Audit
        enabled: true
        created_at:
          date_time: 2026-10-31T13:49:06.5856818
          utc_date_time: 2026-10-31T13:49:06.5856818Z
          local_date_time: 2026-10-31T08:49:06.5856818-05:00
          date: 2026-10-31T00:00:00.0000000
          day: 31
          day_of_week: Saturday
          offset: 00:00:00
          ticks: 639290513465856818
          utc_ticks: 639290513465856818
          time_of_day: 13:49:06.5856818
          year: 2026
        next_run:
          date_time: 2026-11-01T02:00:00.0000000
          utc_date_time: 2026-11-01T02:00:00.0000000Z
          local_date_time: 2026-10-31T21:00:00.0000000-05:00
          offset: 00:00:00
          ticks: 639290952000000000
          utc_ticks: 639290952000000000
          year: 2026
        """;

    [Fact]
    public void Deserialize_ReadsLegacyMappingFormat()
    {
        var job = ScheduleUtil.Deserialize(LegacyYaml)!;

        Assert.Equal(Next, job.NextRun);
        Assert.Equal(new DateTimeOffset(639290513465856818, TimeSpan.Zero), job.CreatedAt);
        Assert.Null(job.LastRun);
        Assert.Equal("0 2 * * *", job.Cron);
    }

    [Fact]
    public void Deserialize_LegacyWithoutTicks_FallsBackToUtcDateTime()
    {
        var job = ScheduleUtil.Deserialize("""
            name: j
            next_run:
              utc_date_time: 2026-11-01T02:00:00.0000000Z
              offset: 00:00:00
            """)!;

        Assert.Equal(Next, job.NextRun);
    }
}
