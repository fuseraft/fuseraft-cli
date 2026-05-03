using fuseraft.Core.Models;

namespace fuseraft.Core.Interfaces;

/// <summary>
/// Persistent storage for session checkpoints.
/// </summary>
public interface ISessionStore
{
    /// <summary>
    /// Persist (create or overwrite) a checkpoint.
    /// </summary>
    Task SaveAsync(SessionCheckpoint checkpoint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Load a checkpoint by session ID, or null if not found.
    /// </summary>
    Task<SessionCheckpoint?> LoadAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a checkpoint by session ID.
    /// </summary>
    Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// List all stored checkpoints, newest first.
    /// </summary>
    Task<IReadOnlyList<SessionCheckpoint>> ListAsync(CancellationToken cancellationToken = default);
}
