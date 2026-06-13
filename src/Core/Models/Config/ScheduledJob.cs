using YamlDotNet.Serialization;

namespace fuseraft.Core.Models.Config;

/// <summary>
/// A scheduled fuseraft session stored as a YAML file in <c>~/.fuseraft/schedule/</c>.
/// Each job defines when it runs (cron expression), what it does (task + config), and
/// tracks its own run history (last_run, next_run).
/// </summary>
public sealed class ScheduledJob
{
    /// <summary>Unique identifier used as the filename slug.</summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional human-readable description shown in <c>fuseraft schedule list</c>.</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>
    /// Standard 5-field cron expression (minute hour day month weekday).
    /// Example: <c>"0 2 * * *"</c> for 2 AM UTC daily.
    /// </summary>
    [YamlMember(Alias = "cron")]
    public string Cron { get; set; } = string.Empty;

    /// <summary>Task description passed to <c>fuseraft run</c> as the positional argument.</summary>
    [YamlMember(Alias = "task")]
    public string Task { get; set; } = string.Empty;

    /// <summary>
    /// Path to the orchestration config YAML. Relative paths are resolved from the
    /// working directory at the time of execution. Optional — defaults to
    /// <c>config/orchestration.yaml</c> when absent.
    /// </summary>
    [YamlMember(Alias = "config")]
    public string? Config { get; set; }

    /// <summary>
    /// Optional working directory passed to <c>fuseraft run --work-dir</c>.
    /// Useful when the config is in a project subdirectory.
    /// </summary>
    [YamlMember(Alias = "work_dir")]
    public string? WorkDir { get; set; }

    /// <summary>
    /// Optional output transcript path template. Supports <c>{name}</c>, <c>{date}</c>,
    /// and <c>{time}</c> substitutions. Example: <c>~/.fuseraft/logs/{name}-{date}.txt</c>.
    /// When absent, output is written to stdout (captured in the job run log).
    /// </summary>
    [YamlMember(Alias = "output_path")]
    public string? OutputPath { get; set; }

    /// <summary>When <c>false</c>, the job is skipped during <c>fuseraft schedule run</c>.</summary>
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>UTC timestamp when this job was created.</summary>
    [YamlMember(Alias = "created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>UTC timestamp of the last successful execution. Null if never run.</summary>
    [YamlMember(Alias = "last_run")]
    public DateTimeOffset? LastRun { get; set; }

    /// <summary>
    /// UTC timestamp when this job is next scheduled to run.
    /// Computed from the cron expression at creation and after each execution.
    /// </summary>
    [YamlMember(Alias = "next_run")]
    public DateTimeOffset? NextRun { get; set; }
}
