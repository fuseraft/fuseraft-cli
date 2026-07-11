using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace fuseraft.Infrastructure.Storage;

/// <summary>
/// Generic "load JSON from disk, reset to empty on corruption, read-modify-write under a
/// lock" helper. Extracted because the exact same shape — file-exists check, try/catch/log-
/// warning/reset-to-new(), directory-create-then-write, and a <see cref="SemaphoreSlim"/>
/// guarding read-modify-write — was independently hand-written in five places:
/// <c>ChangeTracker</c> (twice, internally), <c>IntentLog</c>, <c>FileVersionStore</c>, and
/// <c>EvidenceStore</c>. Behavior is preserved exactly (including the corrupt-file-resets-to-
/// empty-with-a-Warning-log contract); this only removes the duplication.
/// </summary>
internal sealed class JsonFileStore<T>(
    string path,
    JsonSerializerOptions jsonOpts,
    ILogger? logger,
    string storeName) where T : new()
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Loads and deserializes without acquiring the lock. Callers that need a
    /// consistent read under concurrent writers should use <see cref="ReadAsync{TResult}"/>
    /// or <see cref="WithLockAsync{TResult}"/> instead.</summary>
    public async Task<T> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path)) return new T();
        try
        {
            var raw = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<T>(raw, jsonOpts) ?? new T();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "{Store}: failed to load '{Path}' — reset to empty.", storeName, path);
            return new T();
        }
    }

    public async Task SaveAsync(T value, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, jsonOpts), ct);
    }

    /// <summary>Read-only access under the same lock writers use, so a read never observes a
    /// half-written file. Does not write anything back.</summary>
    public async Task<TResult> ReadAsync<TResult>(Func<T, TResult> read, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await LoadAsync(ct).ConfigureAwait(false);
            return read(current);
        }
        finally { _lock.Release(); }
    }

    /// <summary>Load → mutate → save under one lock acquisition.</summary>
    public async Task<TResult> WithLockAsync<TResult>(
        Func<T, Task<(T Updated, TResult Result)>> mutate,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await LoadAsync(ct).ConfigureAwait(false);
            var (updated, result) = await mutate(current).ConfigureAwait(false);
            await SaveAsync(updated, ct).ConfigureAwait(false);
            return result;
        }
        finally { _lock.Release(); }
    }
}
