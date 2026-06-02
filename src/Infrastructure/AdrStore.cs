using System.Text.Json;
using System.Text.Json.Serialization;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure;

/// <summary>
/// File-backed store for architecture decision records (ADRs).
///
/// Each entry is persisted as an indented JSON file named after its ID
/// (e.g. <c>ADR-0042.json</c>) under the configured decisions directory.
/// Writes are atomic (write-to-temp then rename) and protected by a semaphore.
/// </summary>
public sealed class AdrStore
{
    private readonly string _dir;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented                = true,
        PropertyNamingPolicy         = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive  = true,
        DefaultIgnoreCondition       = JsonIgnoreCondition.WhenWritingNull,
    };

    public AdrStore(string directory) => _dir = Path.GetFullPath(directory);

    // Read

    public async Task<List<AdrEntry>> LoadAllAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_dir)) return [];

        var results = new List<AdrEntry>();
        foreach (var file in Directory.GetFiles(_dir, "ADR-*.json").OrderBy(f => f))
        {
            var entry = await LoadFileAsync(file, ct);
            if (entry is not null) results.Add(entry);
        }
        return results;
    }

    public async Task<AdrEntry?> LoadAsync(string id, CancellationToken ct = default)
    {
        var path = FilePath(id);
        if (!File.Exists(path)) return null;
        return await LoadFileAsync(path, ct);
    }

    // Write

    public async Task SaveAsync(AdrEntry entry, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_dir);
            var json = JsonSerializer.Serialize(entry, JsonOpts);
            await WriteAtomicAsync(FilePath(entry.Id), json, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var path = FilePath(id);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        finally { _lock.Release(); }
    }

    // ID allocation

    /// <summary>Returns the next available ADR ID in the format <c>ADR-NNNN</c>.</summary>
    public string NextId()
    {
        if (!Directory.Exists(_dir)) return "ADR-0001";

        var max = Directory.GetFiles(_dir, "ADR-*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Select(n => int.TryParse(n.Length > 4 ? n[4..] : "0", out var num) ? num : 0)
            .DefaultIfEmpty(0)
            .Max();

        return $"ADR-{max + 1:D4}";
    }

    // Helpers

    private string FilePath(string id) =>
        Path.Combine(_dir, $"{id.ToUpperInvariant()}.json");

    private static async Task<AdrEntry?> LoadFileAsync(string path, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<AdrEntry>(json, JsonOpts);
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
