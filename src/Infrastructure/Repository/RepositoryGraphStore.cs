using System.Text.Json;
using System.Text.Json.Serialization;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure.Repository;

/// <summary>
/// Persists and loads the <see cref="RepositoryGraph"/> to/from a single JSON file
/// at <c>.fuseraft/state/repository.graph</c>.
///
/// Writes are atomic (write-to-temp then rename) and protected by a semaphore.
/// </summary>
public sealed class RepositoryGraphStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        Converters                  = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public RepositoryGraphStore(string path) =>
        _path = Path.GetFullPath(path);

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>Loads the graph from disk. Returns an empty graph when the file does not exist.</summary>
    public async Task<RepositoryGraph> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return new RepositoryGraph();
        try
        {
            var json  = await File.ReadAllTextAsync(_path, ct);
            var graph = JsonSerializer.Deserialize<RepositoryGraph>(json, JsonOpts);
            return graph ?? new RepositoryGraph();
        }
        catch { return new RepositoryGraph(); }
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>Saves <paramref name="graph"/> to disk atomically.</summary>
    public async Task SaveAsync(RepositoryGraph graph, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            graph.LastUpdated = DateTimeOffset.UtcNow;
            var dir = Path.GetDirectoryName(_path);
            if (dir is not null) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(graph, JsonOpts);
            var tmp  = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, _path, overwrite: true);
        }
        finally { _lock.Release(); }
    }
}
