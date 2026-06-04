using System.Text.Json;
using System.Text.Json.Serialization;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure;

/// <summary>
/// Durable store for <see cref="RepositoryKnowledgeFinding"/> records.
///
/// <para>
/// All findings are serialized as a JSON array to a single file
/// (<c>.fuseraft/state/knowledge_findings.json</c>). Entity-driven lookups
/// are used by <see cref="fuseraft.Orchestration.KnowledgeRetriever"/> to surface
/// findings from prior sessions without embedding search.
/// </para>
///
/// <para>
/// Writes are atomic (write-to-temp then rename) and serialized through a
/// <see cref="SemaphoreSlim"/>. Deduplication is by (Entity, Finding) case-insensitive
/// equality; identical findings are silently skipped.
/// </para>
/// </summary>
public sealed class RepositoryKnowledgeStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        Converters                  = { new JsonStringEnumConverter() },
    };

    public RepositoryKnowledgeStore(string filePath) =>
        _filePath = Path.GetFullPath(filePath);

    // ── Read ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<RepositoryKnowledgeFinding>> LoadAllAsync(
        CancellationToken ct = default)
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            var json = await File.ReadAllTextAsync(_filePath, ct);
            return JsonSerializer.Deserialize<List<RepositoryKnowledgeFinding>>(json, JsonOpts) ?? [];
        }
        catch { return []; }
    }

    /// <summary>
    /// Returns findings whose <see cref="RepositoryKnowledgeFinding.Entity"/> contains
    /// <paramref name="entityQuery"/> (case-insensitive), ordered by descending confidence
    /// then descending recency.
    /// </summary>
    public async Task<IReadOnlyList<RepositoryKnowledgeFinding>> SearchByEntityAsync(
        string            entityQuery,
        int               topN = 20,
        CancellationToken ct   = default)
    {
        if (string.IsNullOrWhiteSpace(entityQuery)) return [];
        var all = await LoadAllAsync(ct);
        return all
            .Where(f => f.Entity.Contains(entityQuery, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Confidence)
            .ThenByDescending(f => f.RecordedAt)
            .Take(topN)
            .ToList();
    }

    // ── Write ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Persists a new finding. No-ops silently when an identical (entity + finding) record
    /// already exists so repeated observations do not bloat the store.
    /// </summary>
    public async Task AddAsync(
        RepositoryKnowledgeFinding finding,
        CancellationToken          ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = (await LoadAllAsync(ct)).ToList();

            bool isDuplicate = all.Any(f =>
                f.Entity .Equals(finding.Entity,  StringComparison.OrdinalIgnoreCase) &&
                f.Finding.Equals(finding.Finding, StringComparison.OrdinalIgnoreCase));
            if (isDuplicate) return;

            all.Add(finding);

            var dir = Path.GetDirectoryName(_filePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(all, JsonOpts);
            var tmp  = _filePath + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, _filePath, overwrite: true);
        }
        finally { _lock.Release(); }
    }
}
