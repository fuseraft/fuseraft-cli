using System.Text.Json;
using System.Text.Json.Serialization;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure.Knowledge;

/// <summary>
/// Stores <see cref="ClaimRecord"/> entries keyed by artifact or evidence-graph node ID,
/// persisted to <c>.fuseraft/state/provenance.json</c>.
///
/// <para>
/// Records are appended and never mutated in place — each call to <see cref="RecordAsync"/>
/// adds or replaces the claim for a given <see cref="ClaimRecord.Id"/>. Validators call
/// <see cref="RecordAsync"/> when they produce a passing result; downstream agents and the
/// Context Broker (Gap 8) query the registry to determine whether ground-truth evidence
/// supports a given artifact.
/// </para>
///
/// <para>
/// Expiry is checked by <see cref="IsValidAsync"/>: a claim is invalid when its
/// <see cref="ClaimRecord.ExpiresAt"/> is set and is in the past.
/// </para>
/// </summary>
public sealed class ProvenanceRegistry
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        Converters                  = { new JsonStringEnumConverter() },
    };

    public ProvenanceRegistry(string path) => _path = path;

    // ── Write ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Persists a <see cref="ClaimRecord"/>, replacing any existing record with the same
    /// <see cref="ClaimRecord.Id"/>. The computed <see cref="ClaimRecord.Status"/> is set
    /// from the support composition before saving.
    /// </summary>
    public async Task<ClaimRecord> RecordAsync(ClaimRecord record, CancellationToken ct = default)
    {
        var computed = record with
        {
            Status     = ConfidenceComputer.Compute(record.Support),
            VerifiedAt = record.Support.Count > 0 ? DateTimeOffset.UtcNow : record.VerifiedAt,
        };

        await _lock.WaitAsync(ct);
        try
        {
            var all = await LoadAllInternalAsync(ct);
            all.RemoveAll(r => string.Equals(r.Id, computed.Id, StringComparison.Ordinal));
            all.Add(computed);
            await SaveAsync(all, ct);
        }
        finally { _lock.Release(); }

        return computed;
    }

    // ── Read ────────────────────────────────────────────────────────────────

    public async Task<ClaimRecord?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var all = await LoadAllAsync(ct);
        return all.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
    }

    /// <summary>Returns the most recent claim recorded for the given artifact ID.</summary>
    public async Task<ClaimRecord?> GetByArtifactAsync(string artifactId, CancellationToken ct = default)
    {
        var all = await LoadAllAsync(ct);
        return all
            .Where(r => string.Equals(r.ArtifactId, artifactId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.ObservedAt)
            .FirstOrDefault();
    }

    public Task<List<ClaimRecord>> GetAllAsync(CancellationToken ct = default) =>
        LoadAllAsync(ct);

    // ── Expiry ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>false</c> when the record does not exist or its <see cref="ClaimRecord.ExpiresAt"/>
    /// is set and is in the past. Callers must re-verify stale claims before acting on them.
    /// </summary>
    public async Task<bool> IsValidAsync(string id, CancellationToken ct = default)
    {
        var record = await GetByIdAsync(id, ct);
        if (record is null) return false;
        if (record.ExpiresAt.HasValue && record.ExpiresAt.Value < DateTimeOffset.UtcNow)
            return false;
        return true;
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────

    /// <summary>
    /// Archives records matching <paramref name="shouldArchive"/> to <paramref name="archivePath"/>
    /// (appended, never overwritten) and, when <paramref name="apply"/> is <c>true</c>,
    /// removes them from the active store. Returns the records that would be or were archived.
    /// </summary>
    public async Task<IReadOnlyList<ClaimRecord>> CompactAsync(
        Func<ClaimRecord, bool> shouldArchive,
        string                  archivePath,
        bool                    apply,
        CancellationToken       ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all       = await LoadAllInternalAsync(ct);
            var toArchive = all.Where(shouldArchive).ToList();
            if (toArchive.Count == 0) return [];

            if (apply)
            {
                // Append to archive (dedup by ID, newest wins).
                var existing = await LoadFromFileAsync(archivePath, ct);
                var archiveMap = existing
                    .Concat(toArchive)
                    .GroupBy(r => r.Id)
                    .ToDictionary(g => g.Key, g => g.Last());
                await SaveToPathAsync(archivePath, [.. archiveMap.Values], ct);

                // Remove archived records from the active store.
                var archiveIds = new HashSet<string>(toArchive.Select(r => r.Id), StringComparer.Ordinal);
                await SaveAsync(all.Where(r => !archiveIds.Contains(r.Id)).ToList(), ct);
            }

            return toArchive;
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Applies time-based confidence decay to all active records.
    /// When <paramref name="apply"/> is <c>true</c>, saves records whose status changed.
    /// Returns the IDs of records that were or would be downgraded.
    /// </summary>
    public async Task<IReadOnlyList<string>> DecayAsync(
        int               decayDays,
        bool              apply,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all     = await LoadAllInternalAsync(ct);
            var updated = new List<ClaimRecord>();
            var changed = new List<string>();

            foreach (var r in all)
            {
                var newStatus = ConfidenceComputer.Decay(r.Status, r.VerifiedAt, r.ExpiresAt, decayDays);
                if (string.Equals(newStatus, r.Status, StringComparison.Ordinal))
                {
                    updated.Add(r);
                }
                else
                {
                    updated.Add(r with { Status = newStatus });
                    changed.Add(r.Id);
                }
            }

            if (apply && changed.Count > 0)
                await SaveAsync(updated, ct);

            return changed;
        }
        finally { _lock.Release(); }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<List<ClaimRecord>> LoadAllAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try { return await LoadAllInternalAsync(ct); }
        finally { _lock.Release(); }
    }

    private async Task<List<ClaimRecord>> LoadAllInternalAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var json = await File.ReadAllTextAsync(_path, ct);
            return JsonSerializer.Deserialize<List<ClaimRecord>>(json, JsonOpts) ?? [];
        }
        catch { return []; }
    }

    private static async Task<List<ClaimRecord>> LoadFromFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<List<ClaimRecord>>(json, JsonOpts) ?? [];
        }
        catch { return []; }
    }

    private async Task SaveAsync(List<ClaimRecord> records, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(records, JsonOpts);
        // A GUID-suffixed temp name, not a fixed "<path>.tmp" — CompactAsync's archive path is
        // derived from FuseraftPaths + the current working directory, both process-global, so
        // two callers can legitimately compute the identical destination path (e.g. concurrent
        // fuseraft processes against the same project, or — as observed — unrelated tests that
        // happen to overlap). A shared, predictable temp name lets one caller's File.Move
        // consume the other's in-flight write, so the second Move throws FileNotFoundException
        // on a temp file it itself just wrote.
        var tmp = $"{_path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, _path, overwrite: true);
    }

    private static async Task SaveToPathAsync(string path, List<ClaimRecord> records, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(records, JsonOpts);
        // See the comment in SaveAsync above — same reasoning, same fix.
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, path, overwrite: true);
    }
}
