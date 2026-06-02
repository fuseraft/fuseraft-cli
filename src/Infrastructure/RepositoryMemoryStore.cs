using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure;

/// <summary>
/// Persistent store for <see cref="RepositoryMemoryEntry"/> records.
///
/// <para>
/// Each entry is written as an indented JSON file named <c>{id}.json</c> under
/// <c>.fuseraft/knowledge/repository/</c>. A human-readable <c>MEMORY.md</c> index
/// in the same directory lists every entry with its ID, status, confidence, and the
/// first line of its pattern — matching the layout used by the agent memory store.
/// </para>
///
/// <para>
/// Writes are atomic (write-to-temp then rename) and protected by a semaphore.
/// </para>
/// </summary>
public sealed class RepositoryMemoryStore
{
    private readonly string _dir;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private const string IndexFile = "MEMORY.md";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        Converters                  = { new JsonStringEnumConverter() },
    };

    public RepositoryMemoryStore(string directory) => _dir = Path.GetFullPath(directory);

    // ── Read ────────────────────────────────────────────────────────────────

    public async Task<List<RepositoryMemoryEntry>> LoadAllAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_dir)) return [];

        var results = new List<RepositoryMemoryEntry>();
        foreach (var file in Directory.GetFiles(_dir, "*.json").OrderBy(f => f))
        {
            var entry = await LoadFileAsync(file, ct);
            if (entry is not null) results.Add(entry);
        }
        return results;
    }

    /// <summary>Returns only entries with <c>Status = Approved</c>.</summary>
    public async Task<List<RepositoryMemoryEntry>> LoadApprovedAsync(CancellationToken ct = default)
    {
        var all = await LoadAllAsync(ct);
        return all.Where(e => e.Status.Equals("Approved", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Returns only entries with <c>Status = Candidate</c>.</summary>
    public async Task<List<RepositoryMemoryEntry>> LoadCandidatesAsync(CancellationToken ct = default)
    {
        var all = await LoadAllAsync(ct);
        return all.Where(e => e.Status.Equals("Candidate", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task<RepositoryMemoryEntry?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var path = FilePath(id);
        return File.Exists(path) ? await LoadFileAsync(path, ct) : null;
    }

    // ── Write ────────────────────────────────────────────────────────────────

    public async Task SaveAsync(RepositoryMemoryEntry entry, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_dir);
            var json = JsonSerializer.Serialize(entry, JsonOpts);
            await WriteAtomicAsync(FilePath(entry.Id), json, ct);
            await RebuildIndexAsync(ct);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var path = FilePath(id);
            if (File.Exists(path)) File.Delete(path);
            await RebuildIndexAsync(ct);
        }
        finally { _lock.Release(); }
    }

    // ── Index ────────────────────────────────────────────────────────────────

    private async Task RebuildIndexAsync(CancellationToken ct)
    {
        var entries = new List<RepositoryMemoryEntry>();
        foreach (var file in Directory.GetFiles(_dir, "*.json").OrderBy(f => f))
        {
            var e = await LoadFileAsync(file, ct);
            if (e is not null) entries.Add(e);
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Repository Memory Index");
        sb.AppendLine();
        sb.AppendLine("Patterns observed across sessions. Candidates require review before injection.");
        sb.AppendLine();

        foreach (var e in entries.OrderBy(e => e.Status).ThenByDescending(e => e.ReinforcementCount))
        {
            var preview = e.Pattern.Length > 80 ? e.Pattern[..80] + "…" : e.Pattern;
            sb.AppendLine($"- [{e.Status}] [{e.Confidence}] (reinforced {e.ReinforcementCount}×) {preview}");
        }

        var indexPath = Path.Combine(_dir, IndexFile);
        await WriteAtomicAsync(indexPath, sb.ToString(), ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string FilePath(string id) => Path.Combine(_dir, $"{id}.json");

    private static async Task<RepositoryMemoryEntry?> LoadFileAsync(string path, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<RepositoryMemoryEntry>(json, JsonOpts);
        }
        catch { return null; }
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, path, overwrite: true);
    }
}
