using System.Text.Json;
using System.Text.Json.Serialization;
using fuseraft.Core.Models;
using Microsoft.Extensions.Logging;

namespace fuseraft.Orchestration;

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
    private readonly string _logPath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
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
    }

    public void SetSessionId(string sessionId) => _sessionId = sessionId;

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
                TargetPath   = GetArg(args, "path")
                            ?? GetArg(args, "destination")
                            ?? GetArg(args, "source"),
                ArgsSummary  = BuildArgsSummary(args)
            }
        };

        await AppendEntryAsync(entry, ct);
        return intentId;
    }

    /// <summary>
    /// Updates the status of an existing intent entry to <c>APPLIED</c> or <c>FAILED</c>.
    /// No-ops gracefully when the intent ID is not found (e.g. log was reset).
    /// </summary>
    public async Task UpdateStatusAsync(
        string intentId,
        IntentStatus status,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var store = await LoadAsync(ct);
            var entry = store.Entries.Find(e => e.IntentId == intentId);
            if (entry is null) return;

            entry.Status       = status;
            entry.ErrorMessage = errorMessage;
            entry.CompletedAt  = DateTime.UtcNow;

            await SaveAsync(store, ct);
        }
        finally { _fileLock.Release(); }
    }

    /// <summary>
    /// Returns all intents whose <c>TurnIndex</c> falls within [firstTurn, lastTurn].
    /// </summary>
    public async Task<IReadOnlyList<IntentEntry>> GetIntentsForRangeAsync(
        int firstTurn,
        int lastTurn,
        CancellationToken ct = default)
    {
        var store = await LoadReadOnlyAsync(ct);
        return store.Entries
            .Where(e => e.TurnIndex >= firstTurn && e.TurnIndex <= lastTurn)
            .OrderBy(e => e.Timestamp)
            .ToList();
    }

    /// <summary>Returns all intents in the log, ordered by timestamp.</summary>
    public async Task<IReadOnlyList<IntentEntry>> GetAllIntentsAsync(CancellationToken ct = default)
    {
        var store = await LoadReadOnlyAsync(ct);
        return [.. store.Entries.OrderBy(e => e.Timestamp)];
    }

    // Internals

    private async Task AppendEntryAsync(IntentEntry entry, CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var store = await LoadAsync(ct);
            store.Entries.Add(entry);
            await SaveAsync(store, ct);
        }
        finally { _fileLock.Release(); }
    }

    private async Task<IntentStore> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_logPath)) return new IntentStore { ActiveSessionId = _sessionId };
        try
        {
            var raw = await File.ReadAllTextAsync(_logPath, ct);
            return JsonSerializer.Deserialize<IntentStore>(raw, JsonOpts) ?? new IntentStore();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "IntentLog: failed to load '{Path}' — intent history reset.", _logPath);
            return new IntentStore();
        }
    }

    private async Task<IntentStore> LoadReadOnlyAsync(CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try { return await LoadAsync(ct); }
        finally { _fileLock.Release(); }
    }

    private async Task SaveAsync(IntentStore store, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_logPath));
        if (dir is not null) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(_logPath, JsonSerializer.Serialize(store, JsonOpts), ct);
    }

    private static string? GetArg(IReadOnlyDictionary<string, object?>? args, string key)
    {
        if (args is null || !args.TryGetValue(key, out var val)) return null;
        return val?.ToString();
    }

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
