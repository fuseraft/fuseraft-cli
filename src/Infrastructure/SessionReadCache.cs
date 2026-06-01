using System.Text.Json;
using System.Text.Json.Serialization;

namespace fuseraft.Infrastructure;

/// <summary>
/// Per-session file-read cache that tracks whether a file has changed since it was last
/// read. When a cold read is attempted on a file that is already in the cache and has not
/// been modified on disk (same mtime + size), the cache returns a hit so
/// <see cref="Plugins.FileSystemPlugin"/> can short-circuit the read and return a
/// "unchanged since last read" hint instead of dumping the full content into context again.
///
/// <para>
/// This is the session-level complement to the per-turn <c>_readThisTurn</c> HashSet in
/// <c>FileSystemPlugin</c>. The per-turn cache only prevents re-reads within a single
/// agent turn. This cache prevents re-reads across turns for files that have not changed —
/// the primary driver of the redundant read patterns observed in long sessions.
/// </para>
///
/// <para>
/// Cache entries are invalidated automatically when the file is written or patched through
/// the plugin, and evicted lazily when a read finds a different mtime or size. Optionally
/// persisted to a session-scoped JSON file so the cache survives process restarts within
/// the same session directory.
/// </para>
/// </summary>
public sealed class SessionReadCache
{
    private readonly Dictionary<string, SessionCacheEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _persistPath;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public SessionReadCache(string? persistPath = null)
    {
        _persistPath = persistPath;
        if (persistPath is not null && File.Exists(persistPath))
            TryLoad();
    }

    /// <summary>
    /// Checks whether <paramref name="resolvedPath"/> is in the cache and unchanged on disk
    /// (mtime and size match the stored entry). Returns <c>true</c> on a cache hit; on a
    /// miss or stale entry the entry is evicted and <c>false</c> is returned.
    /// </summary>
    public bool TryGetHit(string resolvedPath, FileInfo fileInfo, out SessionCacheEntry? entry)
    {
        if (_entries.TryGetValue(resolvedPath, out entry))
        {
            if (entry.LastModifiedUtc == fileInfo.LastWriteTimeUtc
                && entry.SizeBytes == fileInfo.Length)
                return true;

            // File changed on disk — evict the stale entry so the next read goes through.
            _entries.Remove(resolvedPath);
        }
        entry = null;
        return false;
    }

    /// <summary>
    /// Records a successful read of <paramref name="resolvedPath"/> using the supplied
    /// <paramref name="fileInfo"/> snapshot. Increments the read counter and updates the
    /// last-read timestamp.
    /// </summary>
    public void RecordRead(string resolvedPath, FileInfo fileInfo)
    {
        _entries.TryGetValue(resolvedPath, out var existing);
        _entries[resolvedPath] = new SessionCacheEntry
        {
            LastModifiedUtc = fileInfo.LastWriteTimeUtc,
            SizeBytes       = fileInfo.Length,
            ReadCount       = (existing?.ReadCount ?? 0) + 1,
            LastReadUtc     = DateTime.UtcNow,
        };
        TryPersist();
    }

    /// <summary>Removes <paramref name="resolvedPath"/> from the cache.</summary>
    public void Invalidate(string resolvedPath)
    {
        if (_entries.Remove(resolvedPath))
            TryPersist();
    }

    private void TryLoad()
    {
        if (_persistPath is null) return;
        try
        {
            var json   = File.ReadAllText(_persistPath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, SessionCacheEntry>>(json, JsonOpts);
            if (loaded is not null)
                foreach (var kv in loaded)
                    _entries[kv.Key] = kv.Value;
        }
        catch { /* best effort — corrupt or missing file is treated as empty cache */ }
    }

    private void TryPersist()
    {
        if (_persistPath is null) return;
        try
        {
            var dir = Path.GetDirectoryName(_persistPath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(_persistPath, JsonSerializer.Serialize(_entries, JsonOpts));
        }
        catch { /* best effort */ }
    }
}

/// <summary>Metadata stored per cached file path.</summary>
public record SessionCacheEntry
{
    [JsonPropertyName("mtime")]  public DateTime LastModifiedUtc { get; init; }
    [JsonPropertyName("size")]   public long SizeBytes           { get; init; }
    [JsonPropertyName("reads")]  public int ReadCount            { get; init; }
    [JsonPropertyName("last")]   public DateTime LastReadUtc     { get; init; }
}
