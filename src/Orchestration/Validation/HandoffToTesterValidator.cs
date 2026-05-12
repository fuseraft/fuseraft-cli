using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Validation;

/// <summary>
/// Blocks a handoff unless the source agent completed real work during the current turn.
/// "Real work" means at least one of:
/// <list type="bullet">
///   <item>A <c>write_file</c> or <c>patch_file</c> tool call completed (the normal path), OR</item>
///   <item>
///     When <paramref name="shellFallbackPattern"/> is supplied: a successful
///     <c>shell_run</c> whose command matches at least one of the pipe-separated
///     substrings in the pattern completed. This allows commands that write files
///     through the shell (e.g. <c>go mod tidy</c>, <c>npm install</c>) to satisfy the
///     validator without also calling <c>write_file</c>.
///   </item>
/// </list>
///
/// Detection uses <see cref="FunctionResultContent"/> in <c>Role=Tool</c> messages (written
/// by the agent infrastructure only after the plugin function returned) and
/// <see cref="FunctionCallContent"/> in <c>Role=Assistant</c> messages (to recover the
/// original command arguments via CallId). Both are scoped to the current turn — the search
/// stops at the first <c>Role=User</c> message boundary.
///
/// When <paramref name="testReportPath"/> is supplied, the error message includes the FAIL
/// entries from the test report so the Developer knows exactly what needs fixing.
/// </summary>
public sealed class HandoffToTesterValidator(
    string? shellFallbackPattern = null,
    string? testReportPath = null,
    string? changeLogPath = null) : IRoutingValidator
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };
    public async Task<RoutingValidationResult> ValidateAsync(
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        bool hasWriteFile = false;
        bool hasDepShell  = false;
        bool hasGitCommit = false;

        for (int i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i];

            // User messages mark the boundary between turns — stop here.
            if (msg.Role == ChatRole.User) break;

            if (msg.Role == ChatRole.Tool)
            {
                foreach (var item in msg.Contents)
                {
                    if (item is not FunctionResultContent frc) continue;

                    var funcName = HistoryHelpers.FindFunctionName(history, frc.CallId, i) ?? string.Empty;

                    if (funcName.Contains("write_file", StringComparison.OrdinalIgnoreCase) ||
                        funcName.Contains("patch_file", StringComparison.OrdinalIgnoreCase))
                    {
                        hasWriteFile = true;
                        break;
                    }

                    // A successful git_commit in the current turn is accepted as evidence of
                    // real work: the developer built, verified, and committed their changes.
                    // This covers the common workflow where files were written in a prior turn
                    // and the commit turn is the handoff turn.
                    if (funcName.Contains("git_commit", StringComparison.OrdinalIgnoreCase))
                    {
                        var output = frc.Result?.ToString() ?? string.Empty;
                        if (!output.StartsWith("[EXIT",     StringComparison.Ordinal) &&
                            !output.StartsWith("[ERROR]",   StringComparison.Ordinal) &&
                            !output.StartsWith("[TIMEOUT]", StringComparison.Ordinal) &&
                            !output.StartsWith("[DENIED]",  StringComparison.Ordinal))
                            hasGitCommit = true;
                    }

                    // When a shell fallback pattern is configured, a successful shell_run
                    // whose command matches is accepted in lieu of write_file.
                    if (shellFallbackPattern is not null &&
                        funcName.Contains("shell_run", StringComparison.OrdinalIgnoreCase))
                    {
                        var output = frc.Result?.ToString() ?? string.Empty;
                        if (!output.StartsWith("[EXIT",     StringComparison.Ordinal) &&
                            !output.StartsWith("[ERROR]",   StringComparison.Ordinal) &&
                            !output.StartsWith("[TIMEOUT]", StringComparison.Ordinal) &&
                            !output.StartsWith("[DENIED]",  StringComparison.Ordinal))
                        {
                            var cmd = HistoryHelpers.FindCommand(history, frc.CallId, i);
                            if (cmd is not null && HistoryHelpers.MatchesPattern(cmd, shellFallbackPattern))
                                hasDepShell = true;
                        }
                    }
                }
            }

            if (hasWriteFile || hasDepShell || hasGitCommit) break;
        }

        // If the current turn has no write evidence, fall back to the session-scoped change log.
        // A successful git_commit in any prior turn of this session is accepted — the Tester
        // is responsible for verifying the work; the Developer shouldn't be blocked just because
        // the commit happened in a turn before the handoff turn.
        if (!hasWriteFile && !hasDepShell && !hasGitCommit && changeLogPath is not null)
            hasGitCommit = await CheckChangeLogForCommitAsync(changeLogPath, cancellationToken);

        if (!hasWriteFile && !hasDepShell && !hasGitCommit)
        {
            var failDetail = BuildFailDetail();
            return RoutingValidationResult.Fail(
                "Handoff blocked: no evidence of real work this turn\n" +
                "(no write_file, no patch_file, no git_commit, no shell fallback matched).\n\n" +
                "You must write at least one file before handing off. Use write_file for new files\n" +
                "or patch_file for surgical edits to existing files. Code blocks in your response\n" +
                "are NOT saved to disk — you must call the tool.\n\n" +
                failDetail);
        }

        return RoutingValidationResult.Pass();
    }

    // Checks the session-scoped change log for any successful git_commit. Used as a fallback
    // when the current turn has no write evidence — allows handoff after a build-then-commit
    // workflow that spans multiple turns.
    private static async Task<bool> CheckChangeLogForCommitAsync(string logPath, CancellationToken ct)
    {
        if (!File.Exists(logPath)) return false;
        try
        {
            var json = await File.ReadAllTextAsync(logPath, ct);
            var log  = JsonSerializer.Deserialize<ChangeLog>(json, JsonOptions);
            if (log is null) return false;

            var sessionId = log.ActiveSessionId;
            return log.Entries
                .Where(e => sessionId is null || e.SessionId == sessionId)
                .Any(e => e.GitCommits.Count > 0);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the test report (if configured and present) and returns a formatted block
    /// describing any FAIL results so the Developer knows exactly what to fix.
    /// Returns an empty string when no actionable information is available.
    /// </summary>
    private string BuildFailDetail()
    {
        if (testReportPath is null || !File.Exists(testReportPath))
            return string.Empty;

        try
        {
            var json = File.ReadAllText(testReportPath);
            var report = JsonSerializer.Deserialize<TesterReport>(json, JsonOptions);
            var fails = report?.Results?
                .Where(r => string.Equals(r.Status, "FAIL", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (fails is not { Count: > 0 })
                return string.Empty;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"The Tester found {fails.Count} failing criterion/criteria that you must fix:");
            sb.AppendLine();
            foreach (var f in fails)
            {
                sb.AppendLine($"  FAIL: {f.Criterion}");
                if (!string.IsNullOrWhiteSpace(f.Notes))
                    sb.AppendLine($"        Notes: {f.Notes}");
                if (!string.IsNullOrWhiteSpace(f.Stderr))
                    sb.AppendLine($"        stderr: {f.Stderr.Trim()}");
            }
            sb.AppendLine();
            sb.AppendLine("Fix each failing criterion, then call write_file for every changed file.");
            sb.AppendLine();
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

}

// Internal DTOs — minimal subset of the Tester's test-report.json used only for error enrichment.

internal sealed record TesterReport
{
    [JsonPropertyName("results")]
    public List<TesterResult>? Results { get; init; }
}

internal sealed record TesterResult
{
    [JsonPropertyName("criterion")]
    public string? Criterion { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    [JsonPropertyName("stderr")]
    public string? Stderr { get; init; }
}
