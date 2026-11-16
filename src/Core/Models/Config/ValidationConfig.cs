using fuseraft.Core;

namespace fuseraft.Core.Models.Config;

/// <summary>
/// Configuration for the routing validator middleware that runs before keyword-based
/// handoffs fire. Determines paths for disk-based artifacts and the assertion patterns
/// used for static fake-test detection.
/// </summary>
public record ValidationConfig
{
    /// <summary>
    /// Path to the brief written by the Planner (absolute or relative to CWD).
    /// Defaults to <c>.fuseraft/artifacts/brief.json</c>.
    /// </summary>
    public string BriefPath { get; init; } = FuseraftPaths.LocalBrief;

    /// <summary>
    /// Path to the test report written by the Tester (absolute or relative to CWD).
    /// Defaults to <c>.fuseraft/artifacts/test-report.json</c>.
    /// </summary>
    public string TestReportPath { get; init; } = FuseraftPaths.LocalTestReport;

    /// <summary>
    /// Path to the change log written by the <c>ChangeTracker</c> (absolute or relative to CWD).
    /// History-based gate checks fall back to it when the turn's history has no matching tool result.
    /// </summary>
    public string? ChangeLogPath { get; init; } = FuseraftPaths.LocalChanges;
}
