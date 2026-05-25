using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration;

/// <summary>
/// An <see cref="IOrchestrationHook"/> that injects diagnostic context into the shared
/// conversation history when a routing validator fails more than once consecutively.
///
/// <para>
/// On a <c>validation_fail</c> event with <c>consecutive &gt;= 2</c>, this hook reads
/// the most recent entries from the change log and appends a concise summary to the
/// history. This gives the re-invoked agent ground-truth data about what was actually
/// done on disk — independent of what it claimed in its last response — so it can make
/// an informed correction rather than repeating the same failing action.
/// </para>
///
/// <para>
/// History injection uses the <c>appendMessage</c> delegate provided at construction so
/// the hook does not hold a direct reference to the shared <c>List&lt;ChatMessage&gt;</c>.
/// </para>
/// </summary>
public sealed class ValidationDiagnosticHook : IOrchestrationHook
{
    private readonly string _changeLogPath;
    private readonly Action<ChatMessage> _appendMessage;
    private readonly int _consecutiveThreshold;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Creates a new diagnostic hook.
    /// </summary>
    /// <param name="changeLogPath">Path to the session change log written by <see cref="ChangeTracker"/>.</param>
    /// <param name="appendMessage">Delegate that appends a message to the shared conversation history.</param>
    /// <param name="consecutiveThreshold">
    /// Minimum consecutive failure count that triggers injection. Defaults to 2 so the
    /// first retry uses only the validator's own error message; ground-truth context is
    /// injected starting from the second failure.
    /// </param>
    public ValidationDiagnosticHook(
        string changeLogPath,
        Action<ChatMessage> appendMessage,
        int consecutiveThreshold = 2)
    {
        _changeLogPath        = changeLogPath;
        _appendMessage        = appendMessage;
        _consecutiveThreshold = consecutiveThreshold;
    }

    public async Task OnEventAsync(OrchestrationEvent evt, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(evt.EventType, "validation_fail", StringComparison.OrdinalIgnoreCase))
            return;

        // Extract the consecutive count from the anonymous payload.
        int consecutive = ExtractConsecutive(evt.Payload);
        if (consecutive < _consecutiveThreshold)
            return;

        // Read the most recent change log entry.
        var summary = await ReadLatestChangeSummaryAsync(cancellationToken).ConfigureAwait(false);

        // Extract the specific contract failure reason from the payload so the agent knows
        // exactly what predicate failed, not just what is on disk.
        var contractError = ExtractError(evt.Payload);

        if (summary is null && contractError is null)
            return;

        var parts = new System.Text.StringBuilder();
        parts.AppendLine("DIAGNOSTIC CONTEXT (injected by validation monitor):");
        parts.AppendLine("The following ground-truth data was recorded on disk during this session.");
        parts.AppendLine("Use it to identify the gap between what you attempted and what the validator requires.");

        if (contractError is not null)
        {
            parts.AppendLine();
            parts.AppendLine("CONTRACT FAILURE REASON:");
            parts.AppendLine(contractError);
        }

        if (summary is not null)
        {
            parts.AppendLine();
            parts.AppendLine("WHAT WAS ACTUALLY RECORDED:");
            parts.Append(summary);
        }

        _appendMessage(new ChatMessage(ChatRole.User, parts.ToString().TrimEnd()));
    }

    // Reads the most recent ChangeEntry from the change log and formats it as plain text.
    private async Task<string?> ReadLatestChangeSummaryAsync(CancellationToken ct)
    {
        if (!File.Exists(_changeLogPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(_changeLogPath, ct).ConfigureAwait(false);
            var log  = JsonSerializer.Deserialize<ChangeLogSnapshot>(json, JsonOpts);

            if (log?.Entries is not { Count: > 0 })
                return null;

            var entry = log.Entries[^1]; // most recent entry
            var parts = new List<string>();

            if (entry.FilesWritten?.Count > 0)
                parts.Add($"Files written: {string.Join(", ", entry.FilesWritten)}");

            if (entry.FilesDeleted?.Count > 0)
                parts.Add($"Files deleted: {string.Join(", ", entry.FilesDeleted)}");

            if (entry.CommandsRun?.Count > 0)
            {
                var cmds = entry.CommandsRun.Select(c => $"{c.Command} [{(c.Succeeded ? "OK" : "FAILED")}]");
                parts.Add($"Commands run: {string.Join("; ", cmds)}");
            }

            if (entry.GitCommits?.Count > 0)
                parts.Add($"Git commits: {string.Join(", ", entry.GitCommits)}");

            if (parts.Count == 0)
                return null;

            return $"Last recorded activity (turn {entry.TurnIndex}, agent '{entry.Agent}'):\n" +
                   string.Join("\n", parts.Select(p => $"  - {p}"));
        }
        catch
        {
            return null;
        }
    }

    // Extracts the 'consecutive' field from the validation_fail payload using reflection over
    // the anonymous type. Falls back to 0 on any error.
    private static int ExtractConsecutive(object? payload)
    {
        if (payload is null) return 0;
        try
        {
            var prop = payload.GetType().GetProperty("consecutive",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.IgnoreCase);
            return prop?.GetValue(payload) is int v ? v : 0;
        }
        catch { return 0; }
    }

    // Extracts the 'error' field from the validation_fail payload — the human-readable
    // contract failure message injected by StateMachineSelectionStrategy. Returns null
    // when the field is absent (e.g. legacy payload shapes or non-state-machine sessions).
    private static string? ExtractError(object? payload)
    {
        if (payload is null) return null;
        try
        {
            var prop = payload.GetType().GetProperty("error",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.IgnoreCase);
            return prop?.GetValue(payload)?.ToString();
        }
        catch { return null; }
    }

}
