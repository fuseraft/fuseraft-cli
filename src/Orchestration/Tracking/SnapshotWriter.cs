using System.Text.Json;
using System.Text.Json.Serialization;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Tracking;

/// <summary>
/// Writes per-turn context snapshots and a final manifest to a session-scoped
/// directory for postmortem analysis. All writes are best-effort — errors are
/// swallowed so recording never disrupts the orchestration session.
/// </summary>
public sealed class SnapshotWriter : IDisposable
{
    private readonly string _dir;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _sessionId;

    private static readonly JsonSerializerOptions LineOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ManifestOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = true,
    };

    public string SnapshotDir => _dir;

    public SnapshotWriter(string dir) => _dir = dir;

    public void SetSessionId(string sessionId) => _sessionId = sessionId;

    /// <summary>
    /// Appends one record to <c>turns.jsonl</c> for the given agent message.
    /// No-op for orchestrator-internal routing messages (role="user", agent="orchestrator").
    /// </summary>
    public async Task RecordTurnAsync(AgentMessage msg)
    {
        var record = new TurnRecord(
            Ts:                   msg.Timestamp.ToString("O"),
            Session:              _sessionId,
            Turn:                 msg.TurnIndex,
            Agent:                msg.AgentName,
            Role:                 msg.Role,
            Content:              msg.Content,
            ToolCalls:            msg.ToolCalls?.Select(tc => new ToolCallEntry(tc.Name, tc.ArgsSummary, tc.Succeeded, EstOutputTokens(tc))).ToArray(),
            InputTokens:          msg.Usage?.InputTokens,
            OutputTokens:         msg.Usage?.OutputTokens,
            IsCompactionSummary:  msg.IsCompactionSummary ? true : null);

        var line = JsonSerializer.Serialize(record, LineOpts) + "\n";
        await AppendLineAsync(Path.Combine(_dir, "turns.jsonl"), line);
    }

    /// <summary>
    /// Writes <c>manifest.json</c> summarising the completed session.
    /// Safe to call even if the session failed or was cancelled.
    /// </summary>
    public async Task WriteManifestAsync(bool succeeded, string? errorMessage, string task, TimeSpan elapsed)
    {
        var manifest = new ManifestRecord(
            Ts:             DateTimeOffset.UtcNow.ToString("O"),
            Session:        _sessionId,
            Succeeded:      succeeded,
            ErrorMessage:   errorMessage,
            Task:           task,
            ElapsedSeconds: Math.Round(elapsed.TotalSeconds, 3));

        try
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, "manifest.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, ManifestOpts));
        }
        catch { /* best-effort */ }
    }

    private async Task AppendLineAsync(string path, string line)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_dir);
            await File.AppendAllTextAsync(path, line).ConfigureAwait(false);
        }
        catch { /* best-effort — never disrupt the session */ }
        finally { _lock.Release(); }
    }

    public void Dispose() => _lock.Dispose();

    private sealed record TurnRecord(
        string   Ts,
        string?  Session,
        int      Turn,
        string   Agent,
        string   Role,
        string   Content,
        ToolCallEntry[]? ToolCalls,
        int?     InputTokens,
        int?     OutputTokens,
        bool?    IsCompactionSummary);

    private sealed record ToolCallEntry(string Name, string? ArgsSummary, bool Succeeded, int? EstOutputTokens);

    // Estimates the output tokens consumed by one tool_use block:
    // name chars + args JSON chars + ~12 chars of block overhead, divided by 4 (chars per token).
    private static int EstOutputTokens(ToolCallRecord tc) =>
        Math.Max(1, (tc.Name.Length + tc.ArgsCharCount + 12) / 4);

    private sealed record ManifestRecord(
        string  Ts,
        string? Session,
        bool    Succeeded,
        string? ErrorMessage,
        string  Task,
        double  ElapsedSeconds);
}
