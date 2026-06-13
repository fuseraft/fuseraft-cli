using System.Text.Json;
using System.Text.Json.Serialization;

namespace fuseraft.Orchestration.Context;

/// <summary>
/// Appends per-turn context window snapshots to a JSONL file so that a post-run
/// visualization can show how each agent's cumulative input token count grew over time.
///
/// Thread-safe via <see cref="SemaphoreSlim"/>. All writes are best-effort — errors are
/// swallowed so recording never disrupts the orchestration session.
/// </summary>
public sealed class ContextWindowRecorder : IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _sessionId;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ContextWindowRecorder(string path) => _path = path;

    public void SetSessionId(string sessionId) => _sessionId = sessionId;

    /// <summary>Records one snapshot for an agent turn.</summary>
    public async Task RecordAsync(
        string agentName,
        int    turn,
        int    turnInputTokens,
        int    turnOutputTokens,
        int    cumulativeInputTokens,
        int?   warnAt    = null,
        int?   cutoverAt = null)
    {
        await WriteAsync(new CtxSnapshot(
            Ts:                    DateTimeOffset.UtcNow.ToString("O"),
            Session:               _sessionId,
            Agent:                 agentName,
            Turn:                  turn,
            TurnInputTokens:       turnInputTokens,
            TurnOutputTokens:      turnOutputTokens,
            CumulativeInputTokens: cumulativeInputTokens,
            WarnAt:                warnAt is > 0 ? warnAt : null,
            CutoverAt:             cutoverAt is > 0 ? cutoverAt : null,
            CompactionOccurred:    null));
    }

    /// <summary>Records a compaction event marker at the given assistant turn count.</summary>
    public async Task RecordCompactionAsync(int atTurn)
    {
        await WriteAsync(new CtxSnapshot(
            Ts:                    DateTimeOffset.UtcNow.ToString("O"),
            Session:               _sessionId,
            Agent:                 "system",
            Turn:                  atTurn,
            TurnInputTokens:       0,
            TurnOutputTokens:      0,
            CumulativeInputTokens: 0,
            WarnAt:                null,
            CutoverAt:             null,
            CompactionOccurred:    true));
    }

    private async Task WriteAsync(CtxSnapshot snapshot)
    {
        var line = JsonSerializer.Serialize(snapshot, JsonOpts) + "\n";

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.AppendAllTextAsync(_path, line).ConfigureAwait(false);
        }
        catch { /* best-effort — never disrupt the session */ }
        finally { _lock.Release(); }
    }

    public void Dispose() => _lock.Dispose();

    private sealed record CtxSnapshot(
        string  Ts,
        string? Session,
        string  Agent,
        int     Turn,
        int     TurnInputTokens,
        int     TurnOutputTokens,
        int     CumulativeInputTokens,
        int?    WarnAt,
        int?    CutoverAt,
        bool?   CompactionOccurred);
}
