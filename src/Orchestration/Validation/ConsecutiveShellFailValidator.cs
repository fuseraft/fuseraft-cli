using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Validation;

/// <summary>
/// Blocks a handoff route when a build or verify command has failed in every one of
/// the last <paramref name="threshold"/> turns that ran it, with no intervening success.
///
/// <para>
/// Intent: when a Developer has attempted the same build/verify command
/// <paramref name="threshold"/> times in a row without a single success, continuing to
/// retry and re-handoff wastes tokens and burns the session budget.  This validator
/// intercepts the forward handoff keyword and tells the agent to escalate via
/// <c>REPLAN REQUIRED</c> instead, returning control to the Planner for a fresh
/// approach.
/// </para>
///
/// <para>
/// Uses the <c>changes.json</c> change log (written by ChangeTracker middleware) as the
/// authoritative source.  Only entries from the current session are considered.  Falls
/// back to passing (non-blocking) when the log cannot be read or when fewer than
/// <paramref name="threshold"/> matching turns have been recorded.
/// </para>
/// </summary>
public sealed class ConsecutiveShellFailValidator(
    string? commandPattern = null,
    string? changeLogPath = null,
    int threshold = 3) : IRoutingValidator
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
        if (changeLogPath is null)
            return RoutingValidationResult.Pass();

        bool hasRecentSuccess = await CheckRecentSuccessAsync(changeLogPath, cancellationToken);
        if (hasRecentSuccess)
            return RoutingValidationResult.Pass();

        var patternDesc = commandPattern is not null
            ? $" matching '{commandPattern}'"
            : string.Empty;

        return RoutingValidationResult.Fail(
            $"Handoff blocked: {threshold} consecutive turns with no successful shell command{patternDesc}.\n\n" +
            $"The same build or verify command has failed in every recent turn with no recovery.\n" +
            $"Retrying the same approach will continue to fail and waste session budget.\n\n" +
            $"Required action — escalate instead of re-attempting:\n" +
            $"  1. Do NOT emit the implementation-complete handoff keyword.\n" +
            $"  2. Emit 'REPLAN REQUIRED' to return control to the Planner.\n" +
            $"  3. Include a brief summary of what failed so the Planner can write\n" +
            $"     a corrected brief before the next Developer turn.");
    }

    // Returns true when at least one of the last `threshold` turns that ran the
    // matching command had a success — meaning the agent is making progress and
    // the handoff should be allowed through.
    // Returns true (non-blocking) on any read error or when not enough history exists.
    private async Task<bool> CheckRecentSuccessAsync(string logPath, CancellationToken ct)
    {
        if (!File.Exists(logPath)) return true;

        try
        {
            var json = await File.ReadAllTextAsync(logPath, ct);
            var log  = JsonSerializer.Deserialize<ChangeLog>(json, JsonOpts);
            if (log is null) return true;

            var sessionId = log.ActiveSessionId;
            var sessionEntries = log.Entries
                .Where(e => sessionId is null ||
                            string.Equals(e.SessionId, sessionId, StringComparison.Ordinal))
                .OrderByDescending(e => e.TurnIndex)
                .ToList();

            // Collect the last `threshold` turns that actually ran a matching command.
            var matchingTurns = sessionEntries
                .Where(e => e.CommandsRun.Any(c =>
                    commandPattern is null ||
                    HistoryHelpers.MatchesPattern(c.Command, commandPattern)))
                .Take(threshold)
                .ToList();

            // Not enough history yet — insufficient data to block.
            if (matchingTurns.Count < threshold)
                return true;

            // If any of those turns had at least one successful run, allow through.
            return matchingTurns.Any(e => e.CommandsRun.Any(c =>
                c.Succeeded &&
                (commandPattern is null ||
                 HistoryHelpers.MatchesPattern(c.Command, commandPattern))));
        }
        catch
        {
            return true; // On read/parse error, don't block.
        }
    }
}
