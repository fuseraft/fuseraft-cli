using System.Collections.Concurrent;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure;

/// <summary>
/// In-memory session store. Checkpoints are kept for the lifetime of the process only;
/// nothing is written to disk. Sessions cannot be resumed after the process exits.
/// </summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, SessionCheckpoint> _store = new();

    public Task SaveAsync(SessionCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        checkpoint.LastUpdatedAt = DateTime.UtcNow;
        _store[checkpoint.SessionId] = checkpoint;
        return Task.CompletedTask;
    }

    public Task<SessionCheckpoint?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _store.TryGetValue(sessionId, out var checkpoint);
        return Task.FromResult(checkpoint);
    }

    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _store.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SessionCheckpoint>> ListAsync(CancellationToken cancellationToken = default)
    {
        var results = _store.Values
            .OrderByDescending(c => c.LastUpdatedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<SessionCheckpoint>>(results);
    }
}
