using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;

namespace fuseraft.Infrastructure;

/// <summary>
/// Memory provider backed by the file-based <see cref="MemoryStore"/>.
/// Loads from <c>~/.fuseraft/memory/agents/{agentName}/</c> before each turn.
/// Save is a no-op — agents do not auto-write memories; the REPL's
/// <see cref="MemoryExtractor"/> handles extraction for interactive sessions.
/// </summary>
internal sealed class LocalMemoryProvider : IMemoryProvider
{
    public async Task<string?> LoadAsync(string agentName, CancellationToken ct = default)
    {
        try
        {
            var store = MemoryStore.ForAgent(agentName);
            return await store.BuildPromptBlockAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LocalMemoryProvider] Load failed for '{agentName}': {ex.Message}");
            return null;
        }
    }

    public Task SaveAsync(string agentName, IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
        => Task.CompletedTask;
}
