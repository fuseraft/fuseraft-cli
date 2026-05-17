using Microsoft.Extensions.AI;

namespace fuseraft.Core.Interfaces;

/// <summary>
/// Abstracts an external or local memory store that can supply context to an agent
/// before its turn and persist new facts after it responds.
///
/// <para>
/// Implementations are registered via <c>Memory.Provider</c> in the orchestration config.
/// Built-in values: <c>local</c> (file-backed <c>MemoryStore</c>) and <c>webhook</c>
/// (generic HTTP endpoint). Custom providers can be wired in code via
/// <see cref="fuseraft.Infrastructure.MemoryManager"/>.
/// </para>
///
/// <para>
/// Both methods must be non-throwing — internal errors should be caught and logged by the
/// implementation rather than propagated, so a memory failure never interrupts a session.
/// Only <see cref="OperationCanceledException"/> may propagate.
/// </para>
/// </summary>
public interface IMemoryProvider
{
    /// <summary>
    /// Called before each agent turn. Returns a formatted memory block to prepend to
    /// the agent's system instructions, or <see langword="null"/> when nothing applies.
    /// </summary>
    Task<string?> LoadAsync(string agentName, CancellationToken ct = default);

    /// <summary>
    /// Called after each agent turn with the full accumulated history.
    /// Implementations may persist learned facts or update their store asynchronously.
    /// </summary>
    Task SaveAsync(string agentName, IReadOnlyList<ChatMessage> history, CancellationToken ct = default);
}
