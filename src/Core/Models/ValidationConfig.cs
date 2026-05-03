namespace fuseraft.Core.Models;

/// <summary>
/// Configuration for the routing validator middleware that runs before keyword-based
/// handoffs fire. Determines paths for disk-based artifacts and the assertion patterns
/// used for static fake-test detection.
/// </summary>
public record ValidationConfig
{
    /// <summary>
    /// Path to the brief written by the Planner (absolute or relative to CWD).
    /// Defaults to <c>.fuseraft/brief.json</c>.
    /// </summary>
    public string BriefPath { get; init; } = ".fuseraft/brief.json";

    /// <summary>
    /// Path to the test report written by the Tester (absolute or relative to CWD).
    /// Defaults to <c>.fuseraft/test-report.json</c>.
    /// </summary>
    public string TestReportPath { get; init; } = ".fuseraft/test-report.json";

    /// <summary>
    /// Regex patterns that identify real assertion calls in test files.
    /// A test file with zero matches across all patterns is considered a fake test
    /// and blocks the HANDOFF TO REVIEWER route.
    /// </summary>
    public List<string> TestAssertionPatterns { get; init; } =
    [
        @"tester::assert",      // Kiwi-style / fuseraft built-in
        @"if .+ throw",         // C#/Java guard pattern
        @"\bassert",            // bare 'assert' statement (Python, Go, C) AND camelCase prefixes
                                // (assertEqual, assertTrue, assertRaises, assertThat, …)
        @"\bexpect\(",          // JS/TS: expect(x).toBe(…) — require the opening paren to avoid
                                // matching prose like "we expect the result to be…"
        @"\bself\.assert",      // Python unittest: self.assertEqual, self.assertRaises, …
        @"\bt\.(Error|Fatal|Fail|Log)\b", // Go testing.T methods
    ];

    /// <summary>
    /// Path to the change log written by the <c>ChangeTracker</c> (absolute or relative to CWD).
    /// When this file exists, <c>TestReportValid</c> cross-references the commands listed in
    /// <c>test-report.json</c> against the commands that were actually run, closing the loophole
    /// where an agent writes a plausible-looking report without executing anything.
    /// Defaults to <c>.fuseraft/changes.json</c>. Set to null or omit to disable the check.
    /// </summary>
    public string? ChangeLogPath { get; init; } = ".fuseraft/changes.json";
}
