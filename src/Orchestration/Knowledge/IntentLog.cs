using System.Text.Json;
using System.Text.Json.Serialization;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace fuseraft.Orchestration.Knowledge;

/// <summary>
/// Append-only intent log stored at <c>.fuseraft/intents.json</c>.
///
/// <para>
/// Unlike <see cref="ChangeTracker"/>, which records what actually happened after each tool
/// call returns, the IntentLog records what is <em>about</em> to happen before the call
/// executes. Each entry starts <c>PENDING</c> and is updated to <c>APPLIED</c> or
/// <c>FAILED</c> once the call completes.
/// </para>
///
/// <para>
/// This makes recovery deterministic: on resume, any <c>PENDING</c> entries represent
/// operations that were in-flight when the session was interrupted and can be safely
/// replayed or skipped based on current disk state.
/// </para>
/// </summary>
public sealed class IntentLog
{
    private string _logPath;
    private JsonFileStore<IntentStore> _store;
    private readonly ILogger<IntentLog>? _logger;
    private string? _sessionId;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public IntentLog(string logPath, ILogger<IntentLog>? logger = null)
    {
        _logPath = logPath;
        _logger  = logger;
        _store   = new JsonFileStore<IntentStore>(_logPath, JsonOpts, _logger, nameof(IntentLog));
    }

    public void SetSessionId(string sessionId)
    {
        _sessionId = sessionId;
        _logPath   = FuseraftPaths.ExpandSessionId(_logPath, sessionId);
        _store     = new JsonFileStore<IntentStore>(_logPath, JsonOpts, _logger, nameof(IntentLog));
    }

    /// <summary>
    /// Writes a <c>PENDING</c> intent entry before the tool call executes.
    /// Returns the generated intent ID so the caller can later update its status.
    /// </summary>
    public async Task<string> RecordPendingAsync(
        string agent,
        int turnIndex,
        string functionName,
        IReadOnlyDictionary<string, object?>? args,
        CancellationToken ct = default)
    {
        var intentId = Guid.NewGuid().ToString("N")[..16];

        var entry = new IntentEntry
        {
            IntentId  = intentId,
            Timestamp = DateTime.UtcNow,
            Agent     = agent,
            TurnIndex = turnIndex,
            SessionId = _sessionId,
            Status    = IntentStatus.Pending,
            Operation = new IntentOperation
            {
                FunctionName = functionName,
                TargetPath   = OrchestratorHelpers.GetArg(args, "path")
                            ?? OrchestratorHelpers.GetArg(args, "destination")
                            ?? OrchestratorHelpers.GetArg(args, "source"),
                ArgsSummary  = BuildArgsSummary(args)
            }
        };

        await AppendEntryAsync(entry, ct);
        _logger?.LogDebug(
            "IntentLog: recorded PENDING intent '{IntentId}' — {Function} (agent: {Agent}, turn: {Turn})",
            intentId, functionName, agent, turnIndex);
        return intentId;
    }

    /// <summary>
    /// Updates the status of an existing intent entry to <c>APPLIED</c> or <c>FAILED</c>.
    /// No-ops gracefully when the intent ID is not found (e.g. log was reset).
    /// </summary>
    public Task UpdateStatusAsync(
        string intentId,
        IntentStatus status,
        string? errorMessage = null,
        CancellationToken ct = default) =>
        _store.WithLockAsync(store =>
        {
            var entry = store.Entries.Find(e => e.IntentId == intentId);
            if (entry is null)
            {
                _logger?.LogWarning(
                    "IntentLog: intent '{IntentId}' not found — status update to {Status} skipped (log may have been reset).",
                    intentId, status);
                return Task.FromResult((store, false));
            }

            _logger?.LogDebug(
                "IntentLog: intent '{IntentId}' ({Function}) {OldStatus} → {NewStatus}",
                intentId, entry.Operation.FunctionName, entry.Status, status);

            entry.Status       = status;
            entry.ErrorMessage = errorMessage;
            entry.CompletedAt  = DateTime.UtcNow;

            return Task.FromResult((store, true));
        }, ct);

    /// <summary>
    /// Returns all intents whose <c>TurnIndex</c> falls within [firstTurn, lastTurn].
    /// </summary>
    public Task<IReadOnlyList<IntentEntry>> GetIntentsForRangeAsync(
        int firstTurn,
        int lastTurn,
        CancellationToken ct = default) =>
        _store.ReadAsync<IReadOnlyList<IntentEntry>>(store => store.Entries
            .Where(e => e.TurnIndex >= firstTurn && e.TurnIndex <= lastTurn)
            .OrderBy(e => e.Timestamp)
            .ToList(), ct);

    /// <summary>Returns all intents in the log, ordered by timestamp.</summary>
    public Task<IReadOnlyList<IntentEntry>> GetAllIntentsAsync(CancellationToken ct = default) =>
        _store.ReadAsync<IReadOnlyList<IntentEntry>>(store => [.. store.Entries.OrderBy(e => e.Timestamp)], ct);

    // Internals

    private Task AppendEntryAsync(IntentEntry entry, CancellationToken ct) =>
        _store.WithLockAsync(store =>
        {
            // Stamp ActiveSessionId on first write to a brand-new log — JsonFileStore's
            // reset-to-empty path can't know _sessionId, so it's set here instead.
            if (store.ActiveSessionId is null)
                store = store with { ActiveSessionId = _sessionId };
            store.Entries.Add(entry);
            return Task.FromResult((store, true));
        }, ct);

    private static Dictionary<string, string?> BuildArgsSummary(IReadOnlyDictionary<string, object?>? args)
    {
        if (args is null) return [];
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in args)
        {
            var str = v?.ToString();
            result[k] = str is { Length: > 200 } ? str[..200] + "…" : str;
        }
        return result;
    }
}
