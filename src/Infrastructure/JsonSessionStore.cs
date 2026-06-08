using System.Text.Json;
using System.Text.Json.Serialization;
using fuseraft.Core;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using Microsoft.Extensions.Logging;

namespace fuseraft.Infrastructure;

/// <summary>
/// File-backed session store. Each checkpoint is saved as an individual JSON file
/// under <c>~/.fuseraft/sessions/&lt;sessionId&gt;.json</c>. A lightweight
/// <c>index.json</c> in the same directory is kept in sync on every save and delete
/// so that listing sessions never requires loading the full checkpoint files.
/// </summary>
public sealed class JsonSessionStore(ILogger<JsonSessionStore> logger, string? sessionDir = null) : ISessionStore
{
    private readonly string SessionDir = sessionDir ?? FuseraftPaths.GlobalSessions;
    private readonly SemaphoreSlim _indexLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task SaveAsync(SessionCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        EnsureDir();
        var path = FilePath(checkpoint.SessionId);

        checkpoint.LastUpdatedAt = DateTime.UtcNow;

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, checkpoint, JsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        // Restrict session files to owner-only on Unix (0600) to prevent other users
        // on multi-user systems from reading potentially sensitive session content.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        await UpdateIndexAsync(checkpoint, cancellationToken);

        if (checkpoint.IsComplete)
            logger.LogDebug("Session complete: {SessionId} ({Turns} turns)", checkpoint.SessionId, checkpoint.Messages.Count);
        else
            logger.LogDebug("Checkpoint saved: {SessionId} ({Turns} turns)", checkpoint.SessionId, checkpoint.Messages.Count);
    }

    public async Task<SessionCheckpoint?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var path = FilePath(sessionId);
        if (!File.Exists(path)) return null;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<SessionCheckpoint>(stream, JsonOptions, cancellationToken);
    }

    public async Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = FilePath(sessionId);
        if (File.Exists(path)) File.Delete(path);

        var indexPath = IndexPath();
        if (File.Exists(indexPath))
        {
            await _indexLock.WaitAsync(cancellationToken);
            try
            {
                var entries = await ReadIndexAsync(indexPath, cancellationToken);
                if (entries.Remove(sessionId))
                    await WriteIndexAsync(indexPath, entries, cancellationToken);
            }
            finally
            {
                _indexLock.Release();
            }
        }
    }

    public async Task<IReadOnlyList<SessionCheckpoint>> ListAsync(CancellationToken cancellationToken = default)
    {
        EnsureDir();
        var files = Directory.GetFiles(SessionDir, "*.json")
            .Where(f => !Path.GetFileName(f).Equals("index.json", StringComparison.OrdinalIgnoreCase));
        var results = new List<SessionCheckpoint>();

        foreach (var file in files)
        {
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                var checkpoint = await JsonSerializer.DeserializeAsync<SessionCheckpoint>(stream, JsonOptions, cancellationToken);
                if (checkpoint is not null) results.Add(checkpoint);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Could not read session file {File}: {Error}", file, ex.Message);
            }
        }

        results.Sort((a, b) => b.LastUpdatedAt.CompareTo(a.LastUpdatedAt));
        return results;
    }

    public async Task<IReadOnlyList<SessionIndexEntry>> ListIndexAsync(CancellationToken cancellationToken = default)
    {
        EnsureDir();
        var indexPath = IndexPath();

        if (!File.Exists(indexPath))
        {
            // No index yet — build it from the checkpoint files and persist it.
            var all = await ListAsync(cancellationToken);
            if (all.Count > 0)
            {
                var built = all.ToDictionary(c => c.SessionId, ToIndexEntry);
                await WriteIndexAsync(indexPath, built, cancellationToken);
                return built.Values.OrderByDescending(e => e.LastUpdatedAt).ToList();
            }
            return [];
        }

        var entries = await ReadIndexAsync(indexPath, cancellationToken);
        return entries.Values
            .OrderByDescending(e => e.LastUpdatedAt)
            .ToList();
    }

    // ── index helpers ──────────────────────────────────────────────────────────

    private string IndexPath() => Path.Combine(SessionDir, "index.json");

    private async Task UpdateIndexAsync(SessionCheckpoint checkpoint, CancellationToken ct)
    {
        var indexPath = IndexPath();
        await _indexLock.WaitAsync(ct);
        try
        {
            var entries = await ReadIndexAsync(indexPath, ct);
            entries[checkpoint.SessionId] = ToIndexEntry(checkpoint);
            await WriteIndexAsync(indexPath, entries, ct);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private static async Task<Dictionary<string, SessionIndexEntry>> ReadIndexAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return new();
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<Dictionary<string, SessionIndexEntry>>(json, JsonOptions) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static async Task WriteIndexAsync(
        string path,
        Dictionary<string, SessionIndexEntry> entries,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
    }

    private static SessionIndexEntry ToIndexEntry(SessionCheckpoint c) => new()
    {
        SessionId       = c.SessionId,
        Task            = IndexTask(c.Task),
        WorkingDirectory = c.WorkingDirectory,
        ConfigPath      = c.ConfigPath,
        StartedAt       = c.StartedAt,
        LastUpdatedAt   = c.LastUpdatedAt,
        IsComplete      = c.IsComplete,
        TurnCount       = c.Messages.Count,
    };

    /// <summary>Returns the first non-empty line of a task string, capped at 120 chars.</summary>
    private static string IndexTask(string task)
    {
        foreach (var raw in task.Split('\n'))
        {
            var line = raw.TrimStart('#', ' ').Trim();
            if (line.Length == 0) continue;
            return line.Length > 120 ? line[..120] + "…" : line;
        }
        return task.Length > 120 ? task[..120] + "…" : task;
    }

    private string FilePath(string sessionId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(sessionId, @"^[0-9a-f]{8}$"))
            throw new ArgumentException($"Invalid session ID '{sessionId}'. Expected 8 lowercase hex characters.");
        return Path.Combine(SessionDir, $"{sessionId}.json");
    }

    private void EnsureDir() => Directory.CreateDirectory(SessionDir);
}
