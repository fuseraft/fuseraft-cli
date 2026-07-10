using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace fuseraft.Infrastructure.Storage;

/// <summary>
/// Lightweight per-file version store backed by <c>.fuseraft/file_versions.json</c>.
///
/// <para>
/// Each entry records a monotonic version counter, a content hash, and the last-modified
/// timestamp for a file that was written through <c>FileSystemPlugin.write_file</c>. The
/// counter increments on every successful write, giving agents a cheap way to detect
/// concurrent-write conflicts without re-reading the full file content.
/// </para>
///
/// <para>
/// Agents use <c>get_file_info</c> to probe the current version before issuing writes.
/// Passing <c>baseVersion</c> to <c>write_file</c> causes the plugin to reject the write
/// with <c>VERSION_MISMATCH</c> when the current version differs, preventing lost updates.
/// </para>
/// </summary>
public sealed class FileVersionStore
{
    private readonly JsonFileStore<Dictionary<string, FileVersionRecord>> _store;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public FileVersionStore(string storePath, ILogger<FileVersionStore>? logger = null)
    {
        _store = new JsonFileStore<Dictionary<string, FileVersionRecord>>(
            storePath, JsonOpts, logger, nameof(FileVersionStore));
    }

    /// <summary>
    /// Returns the current version of <paramref name="path"/>, or <c>0</c> if never versioned.
    /// </summary>
    public async Task<int> GetVersionAsync(string path, CancellationToken ct = default)
    {
        var record = await StatAsync(path, ct);
        return record?.Version ?? 0;
    }

    /// <summary>
    /// Increments the version for <paramref name="path"/> and records the content hash.
    /// Returns the new version number.
    /// </summary>
    public Task<int> BumpVersionAsync(string path, string? contentHash = null, CancellationToken ct = default) =>
        _store.WithLockAsync(store =>
        {
            var key = NormalizePath(path);
            store.TryGetValue(key, out var existing);
            var next = new FileVersionRecord
            {
                Path         = path,
                Version      = (existing?.Version ?? 0) + 1,
                ContentHash  = contentHash,
                LastModified = DateTime.UtcNow,
            };
            store[key] = next;
            return Task.FromResult((store, next.Version));
        }, ct);

    /// <summary>
    /// Returns the <see cref="FileVersionRecord"/> for <paramref name="path"/>, or null
    /// when the file has never been written through the version store.
    /// </summary>
    public Task<FileVersionRecord?> StatAsync(string path, CancellationToken ct = default) =>
        _store.ReadAsync(store => store.TryGetValue(NormalizePath(path), out var r) ? r : null, ct);

    /// <summary>
    /// Removes the version record for <paramref name="path"/> (e.g. after the file is
    /// deleted or moved). No-op when the path was never versioned.
    /// </summary>
    public Task RemoveAsync(string path, CancellationToken ct = default) =>
        _store.WithLockAsync(store =>
        {
            store.Remove(NormalizePath(path));
            return Task.FromResult((store, true));
        }, ct);

    /// <summary>
    /// Computes a SHA-256 hash of <paramref name="content"/> suitable for storing in a
    /// <see cref="FileVersionRecord"/>. Returns the first 12 hex chars (48-bit prefix).
    /// </summary>
    public static string HashContent(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).ToLowerInvariant();
}

/// <summary>Per-file version metadata stored in <c>.fuseraft/file_versions.json</c>.</summary>
public record FileVersionRecord
{
    public string Path { get; init; } = string.Empty;
    public int Version { get; init; }
    public string? ContentHash { get; init; }
    public DateTime LastModified { get; init; }
}
