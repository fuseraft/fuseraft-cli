using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Validation;


/// <summary>
/// Blocks <c>HANDOFF TO REVIEWER</c> unless <c>test-report.json</c> passes all structural
/// checks and (when <c>brief.json</c> is present) every acceptance criterion is covered.
///
/// Checks performed (in order):
/// <list type="number">
///   <item>test-report.json exists at the configured path.</item>
///   <item>test-report.json is valid JSON and has at least one result entry.</item>
///   <item>No result has <c>status: FAIL</c>.</item>
///   <item>No PASS result has an empty <c>command</c> field (fabrication guard).</item>
///   <item>No PASS result has a plugin tool-call string in the <c>command</c> field.</item>
///   <item>No file is listed in <c>fake_test_files</c>.</item>
///   <item>Result count >= acceptance-criteria count from brief.json (coverage guard).</item>
///   <item>Test files listed in brief.json's <c>files_to_change</c> contain at least one
///       configured assertion pattern (static fake-test detection).</item>
///   <item>PASS result commands cross-referenced against changes.json via token/substring
///       matching (current session only). The <c>stdout</c> field is optional and not used
///       for this check — ground-truth output lives in changes.json.</item>
/// </list>
/// </summary>
public sealed class HandoffToReviewerValidator(ValidationConfig config) : IRoutingValidator
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    public async Task<RoutingValidationResult> ValidateAsync(
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        // 1. File existence
        if (!File.Exists(config.TestReportPath))
            return RoutingValidationResult.Fail(
                $"HANDOFF TO REVIEWER blocked: test-report.json not found at '{config.TestReportPath}'.\n\n" +
                $"Write it with write_file:\n" +
                $"  {{ \"results\": [{{ \"criterion\": \"...\", \"status\": \"PASS\", \"command\": \"<shell>\", \"exit_code\": 0 }}], \"fake_test_files\": [] }}");

        // 2. JSON validity
        TestReport report;
        try
        {
            var raw = await File.ReadAllTextAsync(config.TestReportPath, cancellationToken);
            // Go test output (and other shell output) frequently contains literal tab and
            // newline bytes inside stdout/stderr strings. System.Text.Json rejects these per
            // spec (control chars must be escaped in JSON strings). Sanitize before parsing
            // so the Tester does not get stuck on output it cannot control.
            var json = EscapeControlCharsInJsonStrings(raw);
            report = JsonSerializer.Deserialize<TestReport>(json, JsonOptions)
                     ?? throw new InvalidOperationException("Deserialized to null.");
        }
        catch (Exception ex)
        {
            return RoutingValidationResult.Fail(
                $"HANDOFF TO REVIEWER blocked: test-report.json could not be parsed: {ex.Message}\n\n" +
                $"Rebuild from scratch (do NOT patch the existing file):\n" +
                $"  1. Re-run all shell_run commands.\n" +
                $"  2. Limit 'stdout' to 200 chars; replace literal '\"' with single quotes.\n" +
                $"  3. write_file the corrected report, then re-emit HANDOFF TO REVIEWER.");
        }

        if (report.Results is null or { Count: 0 })
            return RoutingValidationResult.Fail(
                $"HANDOFF TO REVIEWER blocked: test-report.json has no results. Every acceptance criterion needs an entry.");

        // 3. No FAILs
        var fails = report.Results
            .Where(r => string.Equals(r.Status, "FAIL", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (fails.Count > 0)
            return RoutingValidationResult.Fail(
                $"HANDOFF TO REVIEWER blocked: {fails.Count} FAIL(s) in test-report.json:\n" +
                string.Join("\n", fails.Select(f => $"  ✗ {f.Criterion}")) + "\n\n" +
                $"Send to Developer: emit BUGS FOUND");

        // 4. No fabricated results (PASS with empty command)
        var fabricated = report.Results
            .Where(r => string.Equals(r.Status, "PASS", StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrWhiteSpace(r.Command))
            .ToList();
        if (fabricated.Count > 0)
            return RoutingValidationResult.Fail(
                $"HANDOFF TO REVIEWER blocked: {fabricated.Count} PASS result(s) with empty 'command' — fabricated results:\n" +
                string.Join("\n", fabricated.Select(r => $"  ✗ {r.Criterion}")));

        // 4b. No tool-call strings used as commands (e.g. "FileSystem-read_file path=…")
        // A PASS backed by a file read or search is not evidence that a test passed.
        var toolCallBacked = report.Results
            .Where(r => string.Equals(r.Status, "PASS", StringComparison.OrdinalIgnoreCase)
                        && IsToolCallCommand(r.Command))
            .ToList();
        if (toolCallBacked.Count > 0)
            return RoutingValidationResult.Fail(
                $"HANDOFF TO REVIEWER blocked: {toolCallBacked.Count} PASS result(s) with a tool call in 'command' instead of a real shell command:\n" +
                string.Join("\n", toolCallBacked.Select(r => $"  ✗ {r.Criterion}: {r.Command}")));

        // 5. No declared fake test files
        if (report.FakeTestFiles is { Count: > 0 })
            return RoutingValidationResult.Fail(
                $"HANDOFF TO REVIEWER blocked: {report.FakeTestFiles.Count} declared fake test file(s). Rewrite with real assertions:\n" +
                string.Join("\n", report.FakeTestFiles.Select(f => $"  ✗ {f}")));

        // 6 & 7. Brief-dependent checks — read brief.json once for both.
        if (File.Exists(config.BriefPath))
        {
            Brief? brief = null;
            try
            {
                var briefJson = await File.ReadAllTextAsync(config.BriefPath, cancellationToken);
                brief = JsonSerializer.Deserialize<Brief>(briefJson, JsonOptions);
            }
            catch
            {
                // brief.json unreadable — skip brief-dependent checks rather than block
            }

            if (brief is not null)
            {
                var coverage = CheckCriterionCoverage(report, brief);
                if (!coverage.IsValid) return coverage;

                if (config.TestAssertionPatterns.Count > 0)
                {
                    var fakeCheck = await CheckForFakeTestsAsync(brief, cancellationToken);
                    if (!fakeCheck.IsValid) return fakeCheck;
                }
            }
        }

        // 8. Cross-reference test-report commands against changes.json
        // Only fires when ChangeLogPath is configured and the file exists.
        if (config.ChangeLogPath is not null)
        {
            var commandCheck = await CheckCommandsWereRunAsync(report, cancellationToken);
            if (!commandCheck.IsValid) return commandCheck;
        }

        return RoutingValidationResult.Pass();
    }

    // Helpers

    private static RoutingValidationResult CheckCriterionCoverage(TestReport report, Brief brief)
    {
        // A missing acceptance_criteria field is a structural error in the brief — fail
        // explicitly so the Tester knows to have the Planner fix the brief before proceeding.
        if (brief.AcceptanceCriteria is null)
            return RoutingValidationResult.Fail(
                "HANDOFF TO REVIEWER blocked: brief.json has no 'acceptance_criteria'. " +
                "The Planner must add a non-empty list before the Tester can produce a valid test report.");

        if (brief.AcceptanceCriteria.Count == 0)
            return RoutingValidationResult.Pass();

        // Require at least as many results as there are acceptance criteria.
        if (report.Results!.Count < brief.AcceptanceCriteria.Count)
            return RoutingValidationResult.Fail(
                $"HANDOFF TO REVIEWER blocked: brief has {brief.AcceptanceCriteria.Count} criteria but report has only {report.Results.Count}. Missing coverage:\n" +
                string.Join("\n", brief.AcceptanceCriteria
                    .Skip(report.Results.Count)
                    .Select(c => $"  ✗ {c}")));

        return RoutingValidationResult.Pass();
    }

    private async Task<RoutingValidationResult> CheckForFakeTestsAsync(
        Brief brief,
        CancellationToken cancellationToken)
    {
        // Find test files from brief.json's files_to_change (paths containing "test").
        var testFilePaths = brief.FilesToChange?
            .Select(f => f.Path)
            .Where(p => !string.IsNullOrEmpty(p)
                        && p.Contains("test", StringComparison.OrdinalIgnoreCase))
            .Select(p => p!)
            .ToList() ?? [];

        if (testFilePaths.Count == 0)
            return RoutingValidationResult.Pass();

        // Compile assertion patterns; skip malformed ones rather than crashing.
        var compiled = config.TestAssertionPatterns
            .Select(p =>
            {
                try { return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled); }
                catch { return null; }
            })
            .Where(r => r is not null)
            .ToList();

        if (compiled.Count == 0)
            return RoutingValidationResult.Pass();

        var fakeFiles = new List<string>();
        foreach (var path in testFilePaths)
        {
            if (!File.Exists(path)) continue;

            try
            {
                var content = await File.ReadAllTextAsync(path, cancellationToken);
                bool hasAssertions = compiled.Any(rx => rx!.IsMatch(content));
                if (!hasAssertions) fakeFiles.Add(path);
            }
            catch
            {
                // Skip files we can't read.
            }
        }

        if (fakeFiles.Count > 0)
            return RoutingValidationResult.Fail(
                $"HANDOFF TO REVIEWER blocked: test files with no assertions (always exit 0):\n" +
                string.Join("\n", fakeFiles.Select(f => $"  ✗ {f}")) +
                "\n\nRewrite using real assertion calls appropriate for the test framework in use.");

        return RoutingValidationResult.Pass();
    }

    /// <summary>
    /// Check 8: Verify that PASS results in the test report are backed by real shell_run
    /// calls recorded in <c>changes.json</c> by the ChangeTracker middleware.
    ///
    /// <para>
    /// Matching is done by token/substring overlap between the reported command string and
    /// the commands captured in the session change log. The <c>stdout</c> field in the
    /// report is intentionally ignored — it is optional metadata for the Reviewer and is
    /// not used for verification. Ground-truth output lives in changes.json.
    /// </para>
    ///
    /// <para>
    /// This approach is robust to minor command-string differences (quoting, flags, aliases)
    /// and eliminates the primary failure mode of the previous fingerprint-based check,
    /// which required the Tester to faithfully copy potentially large shell output into JSON
    /// strings — a common source of encoding errors.
    /// </para>
    ///
    /// <para>
    /// If <c>changes.json</c> does not exist the check is skipped (graceful degradation
    /// for configs that do not use ChangeTracking).
    /// </para>
    /// </summary>
    private async Task<RoutingValidationResult> CheckCommandsWereRunAsync(
        TestReport report,
        CancellationToken cancellationToken)
    {
        var logPath = config.ChangeLogPath!;
        if (!File.Exists(logPath)) return RoutingValidationResult.Pass();

        ChangeLog changeLog;
        try
        {
            var json = await File.ReadAllTextAsync(logPath, cancellationToken);
            changeLog = JsonSerializer.Deserialize<ChangeLog>(json, JsonOptions) ?? new ChangeLog();
        }
        catch (Exception) { return RoutingValidationResult.Pass(); } // unreadable change log — skip command check

        // Filter to the current session.
        var activeSession = changeLog.ActiveSessionId;
        var sessionEntries = activeSession is not null
            ? changeLog.Entries.Where(e => string.Equals(e.SessionId, activeSession, StringComparison.Ordinal)).ToList()
            : changeLog.Entries;

        // Collect all command strings run this session (succeeded or not — criteria that
        // test expected-failure exit codes are legitimate and must not be excluded).
        var ranCommands = sessionEntries
            .SelectMany(e => e.CommandsRun)
            .Select(c => c.Command)
            .ToList();

        if (ranCommands.Count == 0)
        {
            var passCount = report.Results?.Count(r =>
                string.Equals(r.Status, "PASS", StringComparison.OrdinalIgnoreCase)) ?? 0;
            if (passCount > 0)
                return RoutingValidationResult.Fail(
                    $"HANDOFF TO REVIEWER blocked: {passCount} PASS result(s) but no shell commands recorded. Run tests with shell_run before handing off.");

            return RoutingValidationResult.Pass();
        }

        // For each PASS result, verify its command appears in the session log via
        // substring or significant-token overlap. stdout is not used for matching.
        const int MinCommandLen = 8;
        var unverified = new List<string>();

        foreach (var result in report.Results ?? [])
        {
            if (!string.Equals(result.Status, "PASS", StringComparison.OrdinalIgnoreCase)) continue;
            var cmd = result.Command?.Trim() ?? string.Empty;
            if (cmd.Length < MinCommandLen) continue; // too short to be meaningful

            bool matched = ranCommands.Any(r =>
                r.Contains(cmd, StringComparison.OrdinalIgnoreCase) ||
                cmd.Contains(r, StringComparison.OrdinalIgnoreCase) ||
                ShareSignificantToken(cmd, r));

            if (!matched) unverified.Add(cmd);
        }

        if (unverified.Count > 0)
        {
            var available = ranCommands.Count > 0
                ? "\n\nCommands recorded in changes.json for this session:\n" +
                  string.Join("\n", ranCommands.Distinct().Select(r => $"  - {r}"))
                : "\n\nNo shell commands have been executed this session. " +
                  "Call shell_run for each acceptance criterion before writing the report.";

            return RoutingValidationResult.Fail(
                $"HANDOFF TO REVIEWER blocked: {unverified.Count} PASS result(s) not found in the change log.\n\n" +
                "Each PASS must be backed by a real shell_run this session:\n" +
                "  1. shell_run the command.\n" +
                "  2. Update 'command' in test-report.json.\n" +
                "  3. write_file the report.\n\n" +
                "Unverified:\n" +
                string.Join("\n", unverified.Select(c => $"  ✗ {c}")) +
                available);
        }

        return RoutingValidationResult.Pass();
    }

    /// <summary>
    /// Returns true when <paramref name="command"/> looks like a plugin tool-call string
    /// rather than a real shell command.  Tool-call strings start with a known plugin
    /// prefix such as <c>FileSystem-</c> or <c>Search-</c>.
    /// </summary>
    private static bool IsToolCallCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        ReadOnlySpan<string> toolPrefixes = ["FileSystem-", "Search-", "Git-",
                                             "Scratchpad-", "Changes-", "MCP-"];
        foreach (var prefix in toolPrefixes)
        {
            if (command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Escapes unescaped ASCII control characters (U+0000–U+001F) that appear inside
    /// JSON string values. These are valid in JSON only as escape sequences (e.g. <c>\t</c>,
    /// <c>\n</c>); a literal byte is a spec violation that <c>System.Text.Json</c> correctly
    /// rejects. Shell output (e.g. <c>go test</c>) frequently embeds literal tabs and
    /// newlines, so we fix them here rather than making the Tester post-process every result.
    ///
    /// The method implements a minimal JSON string-state machine: it tracks whether the
    /// current position is inside a string and whether the previous character was a backslash,
    /// so it never double-escapes an already-escaped sequence.
    /// </summary>
    private static string EscapeControlCharsInJsonStrings(string json)
    {
        var sb = new System.Text.StringBuilder(json.Length);
        bool inString = false;
        bool escaped  = false;

        foreach (char c in json)
        {
            if (escaped)
            {
                sb.Append(c);
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                sb.Append(c);
                if (inString) escaped = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                sb.Append(c);
                continue;
            }

            if (inString && c < '\x20')
            {
                sb.Append(c switch
                {
                    '\b' => "\\b",
                    '\f' => "\\f",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _    => $"\\u{(int)c:x4}"
                });
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns true when the two command strings share at least one significant token —
    /// a word that is longer than 4 characters and is not a common shell verb.
    /// </summary>
    private static bool ShareSignificantToken(string a, string b)
    {
        static IEnumerable<string> Tokens(string s) =>
            s.Split([' ', '\t', '/', '\\', '-', '_', '.', ';', '|', '&', '<', '>', '(',
                     ')', '{', '}', '!', '?', '=', '"', '\'', '[', ']', '*', '#', '`', ','],
                    StringSplitOptions.RemoveEmptyEntries)
             .Where(t => t.Length > 4)
             .Select(t => t.ToLowerInvariant())
             .Where(t => t is not ("build" or "clean" or "install" or "cargo"
                                or "dotnet" or "python" or "node" or "npm" or "npx"));

        var tokensA = Tokens(a).ToHashSet();
        return Tokens(b).Any(tokensA.Contains);
    }
}

// Internal DTOs for JSON deserialization

internal sealed record TestReport
{
    [JsonPropertyName("results")]
    public List<TestResult>? Results { get; init; }

    [JsonPropertyName("fake_test_files")]
    public List<string>? FakeTestFiles { get; init; }
}

internal sealed record TestResult
{
    [JsonPropertyName("criterion")]
    public string? Criterion { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("stdout")]
    public string? Stdout { get; init; }

    [JsonPropertyName("stderr")]
    public string? Stderr { get; init; }

    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; init; }
}

internal sealed record Brief
{
    [JsonPropertyName("goal")]
    public string? Goal { get; init; }

    [JsonPropertyName("files_to_change")]
    public List<BriefFile>? FilesToChange { get; init; }

    [JsonPropertyName("files_for_context")]
    public List<BriefFile>? FilesForContext { get; init; }

    [JsonPropertyName("acceptance_criteria")]
    public List<string>? AcceptanceCriteria { get; init; }

    [JsonPropertyName("constraints")]
    public List<string>? Constraints { get; init; }
}

internal sealed record BriefFile
{
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
