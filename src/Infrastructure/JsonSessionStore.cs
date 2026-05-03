using System.Text.Json;
using System.Text.Json.Serialization;
using fuseraft.Core;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using Microsoft.Extensions.Logging;

namespace fuseraft.Infrastructure;

/// <summary>
/// File-backed session store. Each checkpoint is saved as an individual JSON file
/// under <c>~/.fuseraft/sessions/&lt;sessionId&gt;.json</c>.
/// </summary>
public sealed class JsonSessionStore(ILogger<JsonSessionStore> logger, string? sessionDir = null) : ISessionStore
{
    private readonly string SessionDir = sessionDir ?? FuseraftPaths.GlobalSessions;

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

    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = FilePath(sessionId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<SessionCheckpoint>> ListAsync(CancellationToken cancellationToken = default)
    {
        EnsureDir();
        var files = Directory.GetFiles(SessionDir, "*.json");
        var results = new List<SessionCheckpoint>(files.Length);

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

    private string FilePath(string sessionId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(sessionId, @"^[0-9a-f]{8}$"))
            throw new ArgumentException($"Invalid session ID '{sessionId}'. Expected 8 lowercase hex characters.");
        return Path.Combine(SessionDir, $"{sessionId}.json");
    }

    private void EnsureDir() => Directory.CreateDirectory(SessionDir);
}
