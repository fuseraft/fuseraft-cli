using fuseraft.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace fuseraft.Infrastructure.Objectives;

/// <summary>
/// File-backed store for <see cref="Objective"/> records persisted as YAML under
/// <c>.fuseraft/knowledge/objectives/</c>. Each objective is one file named
/// <c>OBJ-NNNN.yaml</c>. Writes are atomic (write-to-temp then rename).
/// </summary>
public sealed class ObjectiveStore
{
    private readonly string _dir;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(PascalCaseNamingConvention.Instance)
        .DisableAliases()
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(PascalCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public ObjectiveStore(string directory) => _dir = Path.GetFullPath(directory);

    // ── Read ────────────────────────────────────────────────────────────────

    public async Task<List<Objective>> LoadAllAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_dir)) return [];

        var results = new List<Objective>();
        foreach (var file in Directory.GetFiles(_dir, "OBJ-*.yaml").OrderBy(f => f))
        {
            ct.ThrowIfCancellationRequested();
            var obj = await LoadFileAsync(file, ct);
            if (obj is not null) results.Add(obj);
        }
        return results;
    }

    public async Task<Objective?> GetAsync(string id, CancellationToken ct = default)
    {
        var path = FilePath(id);
        return File.Exists(path) ? await LoadFileAsync(path, ct) : null;
    }

    public async Task<List<Objective>> LoadActiveAsync(CancellationToken ct = default)
    {
        var all = await LoadAllAsync(ct);
        return all.Where(o => o.Status.Equals("Active", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // ── Write ────────────────────────────────────────────────────────────────

    public async Task SaveAsync(Objective obj, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_dir);
            var yaml = Serializer.Serialize(obj);
            await WriteAtomicAsync(FilePath(obj.Id), yaml, ct);
        }
        finally { _lock.Release(); }
    }

    // ── ID allocation ────────────────────────────────────────────────────────

    public string NextId()
    {
        if (!Directory.Exists(_dir)) return "OBJ-0001";

        var max = Directory.GetFiles(_dir, "OBJ-*.yaml")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Select(n => int.TryParse(n.Length > 4 ? n[4..] : "0", out var num) ? num : 0)
            .DefaultIfEmpty(0)
            .Max();

        return $"OBJ-{max + 1:D4}";
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string FilePath(string id) => Path.Combine(_dir, $"{id.ToUpperInvariant()}.yaml");

    private async Task<Objective?> LoadFileAsync(string path, CancellationToken ct)
    {
        try
        {
            var yaml = await File.ReadAllTextAsync(path, ct);
            return Deserializer.Deserialize<Objective>(yaml);
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
