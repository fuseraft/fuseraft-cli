using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Durable investigation memory: records hypotheses, rejected paths, and confirmed root causes
/// so future agents never re-run the same dead-end investigation.
///
/// <para>
/// All writes go to <c>.fuseraft/state/investigation-log.json</c>. The log survives compaction
/// and is injected into every agent's context via the <c>investigation_log</c> context source.
/// </para>
/// </summary>
public sealed class InvestigationPlugin
{
    private readonly string _logPath;
    private readonly string _sessionId;
    private readonly IEventSink? _eventSink;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented              = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition     = JsonIgnoreCondition.WhenWritingNull,
    };

    public InvestigationPlugin(string logPath, string sessionId, IEventSink? eventSink = null)
    {
        _logPath   = logPath;
        _sessionId = sessionId;
        _eventSink = eventSink;
    }

    [Description("Record a new hypothesis for investigation.")]
    public async Task<string> CreateHypothesisAsync(
        [Description("The hypothesis to investigate.")]
        string hypothesis)
    {
        if (string.IsNullOrWhiteSpace(hypothesis))
            return "[ERROR] Hypothesis text must not be empty.";

        var log = await LoadAsync();
        var id  = $"H-{(log.Hypotheses.Count + 1):D3}";
        var updated = log with
        {
            Hypotheses = [.. log.Hypotheses, new HypothesisRecord
            {
                Id         = id,
                Hypothesis = hypothesis.Trim(),
                Status     = "open",
                CreatedAt  = DateTimeOffset.UtcNow,
            }],
        };
        await SaveAsync(updated);
        return $"Recorded hypothesis [{id}]: {hypothesis.Trim()}";
    }

    [Description("Mark a hypothesis as rejected with the reason and supporting evidence.")]
    public async Task<string> RejectHypothesisAsync(
        [Description("Hypothesis ID (e.g. H-001).")]
        string id,
        [Description("Why this hypothesis was rejected.")]
        string reason,
        [Description("Evidence that disproves the hypothesis (one piece per line).")]
        string? evidence = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "[ERROR] Hypothesis ID must not be empty.";

        var log = await LoadAsync();
        var idx = log.Hypotheses.FindIndex(h =>
            string.Equals(h.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

        if (idx < 0)
            return $"[NOT FOUND] No hypothesis with ID '{id}'.";

        var evidenceList = ParseEvidence(evidence);
        var updated = log.Hypotheses[idx] with
        {
            Status       = "rejected",
            RejectReason = reason.Trim(),
            Evidence     = evidenceList,
        };

        var newHypotheses = new List<HypothesisRecord>(log.Hypotheses) { [idx] = updated };
        await SaveAsync(log with { Hypotheses = newHypotheses });

        _eventSink?.Emit(new AttemptFailedEvent(
            Description:  log.Hypotheses[idx].Hypothesis,
            ErrorSummary: reason.Trim())
        { Timestamp = DateTimeOffset.UtcNow });

        return $"Marked [{id}] as rejected: {reason.Trim()}";
    }

    [Description("Mark a hypothesis as confirmed with supporting evidence.")]
    public async Task<string> ConfirmHypothesisAsync(
        [Description("Hypothesis ID (e.g. H-001).")]
        string id,
        [Description("Evidence that confirms the hypothesis (one piece per line).")]
        string? evidence = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "[ERROR] Hypothesis ID must not be empty.";

        var log = await LoadAsync();
        var idx = log.Hypotheses.FindIndex(h =>
            string.Equals(h.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

        if (idx < 0)
            return $"[NOT FOUND] No hypothesis with ID '{id}'.";

        var evidenceList = ParseEvidence(evidence);
        var updated = log.Hypotheses[idx] with
        {
            Status   = "confirmed",
            Evidence = evidenceList,
        };

        var newHypotheses = new List<HypothesisRecord>(log.Hypotheses) { [idx] = updated };
        await SaveAsync(log with { Hypotheses = newHypotheses });
        return $"Marked [{id}] as confirmed.";
    }

    [Description("Log a completed investigation with its summary and conclusion.")]
    public async Task<string> RecordInvestigationAsync(
        [Description("What was investigated.")]
        string summary,
        [Description("What was found or concluded.")]
        string conclusion)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return "[ERROR] Summary must not be empty.";

        var log = await LoadAsync();
        var entry = new InvestigationRecord
        {
            Summary    = summary.Trim(),
            Conclusion = conclusion.Trim(),
            Timestamp  = DateTimeOffset.UtcNow,
        };
        await SaveAsync(log with { Investigations = [.. log.Investigations, entry] });
        return $"Recorded investigation: {summary.Trim()}";
    }

    [Description("Append a confirmed root cause to the investigation log.")]
    public async Task<string> IdentifyRootCauseAsync(
        [Description("The confirmed root cause.")]
        string cause)
    {
        if (string.IsNullOrWhiteSpace(cause))
            return "[ERROR] Root cause must not be empty.";

        var log = await LoadAsync();
        if (log.ConfirmedRootCauses.Any(c =>
                string.Equals(c, cause.Trim(), StringComparison.OrdinalIgnoreCase)))
            return $"Root cause already recorded: {cause.Trim()}";

        await SaveAsync(log with { ConfirmedRootCauses = [.. log.ConfirmedRootCauses, cause.Trim()] });
        return $"Identified root cause: {cause.Trim()}";
    }

    // ── I/O ─────────────────────────────────────────────────────────────────────

    private async Task<InvestigationLog> LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_logPath)) return new InvestigationLog { SessionId = _sessionId };
            var json = await File.ReadAllTextAsync(_logPath);
            return JsonSerializer.Deserialize<InvestigationLog>(json, JsonOpts)
                ?? new InvestigationLog { SessionId = _sessionId };
        }
        catch { return new InvestigationLog { SessionId = _sessionId }; }
        finally { _lock.Release(); }
    }

    private async Task SaveAsync(InvestigationLog log)
    {
        await _lock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            var json = JsonSerializer.Serialize(log with { SessionId = _sessionId }, JsonOpts);
            await File.WriteAllTextAsync(_logPath, json);
        }
        finally { _lock.Release(); }
    }

    private static List<string> ParseEvidence(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Where(s => s.Length > 0)
                  .ToList();
    }
}
