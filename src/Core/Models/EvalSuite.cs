namespace fuseraft.Core.Models;

/// <summary>
/// Top-level descriptor loaded from an eval suite YAML or JSON file.
/// </summary>
public sealed class EvalSuite
{
    public string Name    { get; set; } = string.Empty;
    /// <summary>Suite-level default config path. Overridden per-case by <see cref="EvalCase.Config"/>.</summary>
    public string? Config { get; set; }
    public List<EvalCase> Cases { get; set; } = [];
}

/// <summary>
/// A single eval scenario — a task prompt plus the scoring criteria that determine pass/fail.
/// </summary>
public sealed class EvalCase
{
    /// <summary>Unique identifier used in reports and <c>--filter</c>.</summary>
    public string Id         { get; set; } = string.Empty;
    /// <summary>Inline task string. Mutually exclusive with <see cref="TaskFile"/>.</summary>
    public string? Task      { get; set; }
    /// <summary>Path to a file whose contents become the task. Mutually exclusive with <see cref="Task"/>.</summary>
    public string? TaskFile  { get; set; }
    /// <summary>Per-case config override. Falls back to suite-level Config, then the CLI flag.</summary>
    public string? Config    { get; set; }

    /// <summary>Fail the case when the session does not report <c>Succeeded = true</c>.</summary>
    public bool MustSucceed  { get; set; } = true;

    /// <summary>All strings must appear (case-insensitive) in the final assistant message.</summary>
    public List<string> ExpectKeywords    { get; set; } = [];
    /// <summary>All patterns must match (case-insensitive) against the final assistant message.</summary>
    public List<string> ExpectRegex       { get; set; } = [];
    /// <summary>None of these strings may appear (case-insensitive) in the final assistant message.</summary>
    public List<string> ForbiddenKeywords { get; set; } = [];

    /// <summary>Fail if the session exceeds this many agent turns. 0 = unlimited.</summary>
    public int MaxTurns      { get; set; }
    /// <summary>Free-form labels used with <c>--filter</c>.</summary>
    public List<string> Tags { get; set; } = [];
}

/// <summary>
/// Scoring outcome for a single eval case.
/// </summary>
public sealed record EvalCaseResult
{
    public required string CaseId          { get; init; }
    public required string SessionId       { get; init; }
    public bool Passed                     { get; init; }
    public List<string> FailureReasons     { get; init; } = [];
    public int TotalTurns                  { get; init; }
    public long DurationMs                 { get; init; }
    public long TotalInputTokens           { get; init; }
    public long TotalOutputTokens          { get; init; }
    public string? ErrorMessage            { get; init; }
}
