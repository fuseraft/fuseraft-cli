using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Validation;

/// <summary>
/// Blocks a handoff route unless the source agent completed at least one shell command
/// that exited successfully (exit code 0) during the current turn.
///
/// <para>
/// Uses the ChangeTracker log as the primary source when <paramref name="changeLogPath"/>
/// is supplied — the log is written by middleware and is reliable regardless of how the
/// underlying LLM API represents tool call/result message pairs. Falls back to scanning
/// the raw chat history for deployments that do not use ChangeTracker.
/// </para>
///
/// <para>
/// When <paramref name="requireCurrentTurn"/> is <c>true</c> (recommended for termination
/// validators), the change log is only consulted when the history scan finds no user-message
/// boundary — i.e. we are at the very start of the conversation where no prior turn context
/// exists. If a user boundary <em>is</em> found before any shell pass, the current turn
/// definitively had no shell run and the change log is not consulted, preventing a stale
/// entry from an earlier turn from satisfying the check.
/// </para>
/// </summary>
public sealed class RequireShellPassValidator(
    string? requiredCommandPattern = null,
    string? changeLogPath = null,
    bool requireCurrentTurn = false,
    ILogger<RequireShellPassValidator>? logger = null) : IRoutingValidator
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<RoutingValidationResult> ValidateAsync(
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        // Scan the raw chat history first. This is the definitive source for whether
        // the CURRENT turn had a successful shell run:
        //   - shellPass = true  → a passing shell_run was found before any user boundary.
        //   - hitBoundary = true → a user message was reached before finding a shell pass,
        //                          meaning the current turn definitely had no shell run.
        var (shellPass, hitBoundary) = ScanHistory(history);
        if (shellPass) return RoutingValidationResult.Pass();

        // When requireCurrentTurn is true (typically used for termination validators) and
        // a user boundary was found, the current turn had no shell run — do not consult
        // the change log, which might contain a stale entry from an earlier turn.
        bool hasShellPass = false;
        bool skipChangeLog = requireCurrentTurn && hitBoundary;
        if (!skipChangeLog && changeLogPath is not null)
            hasShellPass = await CheckChangeLogAsync(changeLogPath, cancellationToken);

        if (!hasShellPass)
        {
            return RoutingValidationResult.Fail(
                "Handoff blocked: no" +
                (requiredCommandPattern is not null ? $" matching ({requiredCommandPattern})" : "") +
                " shell_run passed this turn.\n\n" +
                "The validator checks THIS TURN ONLY — prior-turn runs do not carry forward.\n\n" +
                "  1. Call shell_run to run the required command (exit 0).\n" +
                "  2. Emit the handoff keyword in the same response.");
        }

        return RoutingValidationResult.Pass();
    }

    // Change-log check — reads the most recent entry for the active session and checks
    // whether any successful command matches the required pattern.
    private async Task<bool> CheckChangeLogAsync(string logPath, CancellationToken ct)
    {
        if (!File.Exists(logPath)) return false;

        try
        {
            var json = await File.ReadAllTextAsync(logPath, ct);
            var log  = JsonSerializer.Deserialize<ChangeLog>(json, JsonOpts);
            if (log is null) return false;

            // Restrict to the current session so stale data from prior runs is ignored.
            var sessionId = log.ActiveSessionId;

            var recentEntry = log.Entries
                .Where(e => sessionId is null || e.SessionId == sessionId)
                .OrderByDescending(e => e.TurnIndex)
                .FirstOrDefault();

            if (recentEntry is null) return false;

            return recentEntry.CommandsRun.Any(c =>
                c.Succeeded &&
                (requiredCommandPattern is null ||
                 HistoryHelpers.MatchesPattern(c.Command, requiredCommandPattern)));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "RequireShellPassValidator: failed to read change log at '{Path}' — treating as no shell pass.", logPath);
            return false;
        }
    }

    // History scan — returns (shellPass, hitBoundary).
    // hitBoundary=true means we encountered a user message before finding a shell pass,
    // which definitively indicates the current agent turn had no successful shell run.
    private (bool shellPass, bool hitBoundary) ScanHistory(IList<ChatMessage> history)
    {
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i];

            // User messages mark the turn boundary — stop here.
            if (msg.Role == ChatRole.User) return (false, true);

            if (msg.Role == ChatRole.Tool)
            {
                foreach (var item in msg.Contents)
                {
                    if (item is not FunctionResultContent frc) continue;
                    var funcName = HistoryHelpers.FindFunctionName(history, frc.CallId, i) ?? string.Empty;
                    if (!funcName.Contains("shell_run", StringComparison.OrdinalIgnoreCase)) continue;

                    // A successful shell run does not produce [EXIT N] or error prefixes.
                    var result = frc.Result?.ToString() ?? string.Empty;
                    if (result.StartsWith("[EXIT",      StringComparison.Ordinal) ||
                        result.StartsWith("[ERROR]",    StringComparison.Ordinal) ||
                        result.StartsWith("[TIMEOUT]",  StringComparison.Ordinal) ||
                        result.StartsWith("[DENIED]",   StringComparison.Ordinal))
                        continue;

                    // If no pattern is required, any passing command satisfies the validator.
                    if (requiredCommandPattern is null)
                        return (true, false);

                    // Pattern required — extract the original command from the preceding
                    // FunctionCallContent and check it against the pattern.
                    var command = HistoryHelpers.FindCommand(history, frc.CallId, i);
                    if (command is not null && HistoryHelpers.MatchesPattern(command, requiredCommandPattern))
                        return (true, false);
                }
            }
        }

        return (false, false);
    }

}
