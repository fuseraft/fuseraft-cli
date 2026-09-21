using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;

namespace fuseraft.Infrastructure.Memory;

/// <summary>
/// Memory provider backed by the file-based <see cref="MemoryStore"/>.
/// Loads from <c>~/.fuseraft/memory/agents/{agentName}/</c> before each turn.
/// Save is a no-op — agents do not auto-write memories; the REPL's
/// <see cref="MemoryExtractor"/> handles extraction for interactive sessions.
/// </summary>
internal sealed class LocalMemoryProvider : IMemoryProvider
{
    // No try/catch here: MemoryManager.PreTurnAsync already wraps every provider's LoadAsync
    // call in a try/catch that logs via ILogger and swallows non-cancellation exceptions, so a
    // second, provider-local safety net (previously logging to Console.Error instead of the
    // shared logger) only duplicated that guarantee inconsistently.
    public Task<string?> LoadAsync(string agentName, CancellationToken ct = default)
        => LoadAsync(agentName, [], ct);

    public async Task<string?> LoadAsync(string agentName, IReadOnlyList<string> relevanceTerms, CancellationToken ct = default)
    {
        var store = MemoryStore.ForAgent(agentName);
        return await store.BuildPromptBlockAsync(relevanceTerms, ct);
    }

    public Task SaveAsync(string agentName, IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
        => Task.CompletedTask;
}
