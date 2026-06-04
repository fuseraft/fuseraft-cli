using fuseraft.Core.Models;
using fuseraft.Orchestration;

namespace fuseraft.Core.Interfaces;

/// <summary>
/// Ranks a set of <see cref="MemoryEntry"/> records by relevance to the current task,
/// replacing the legacy alphabetical sort used by <c>MemoryStore.FormatPromptBlock()</c>.
/// </summary>
public interface IMemoryRanker
{
    /// <summary>
    /// Returns <paramref name="entries"/> ordered from most to least relevant
    /// for the given <paramref name="signals"/>.
    /// </summary>
    IReadOnlyList<MemoryEntry> Rank(
        IReadOnlyList<MemoryEntry> entries,
        IntentSignals              signals);
}
