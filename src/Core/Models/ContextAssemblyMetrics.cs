namespace fuseraft.Core.Models;

/// <summary>
/// Telemetry snapshot from a single <see cref="fuseraft.Core.Interfaces.IContextAssemblyPipeline.AssembleAsync"/> call.
///
/// <para>
/// Attached to every <see cref="AssembledContext"/> so callers can emit structured
/// <c>context_assembly</c> events without reaching back into the pipeline.
/// </para>
/// </summary>
public sealed record ContextAssemblyMetrics
{
    public string AgentName { get; init; } = string.Empty;

    /// <summary>Knowledge items returned by the retriever before budget trimming.</summary>
    public int KnowledgeItemsRetrieved { get; init; }

    /// <summary>Knowledge items that survived budget trimming and were injected into context.</summary>
    public int KnowledgeItemsIncluded { get; init; }

    /// <summary>Memory entries loaded from the agent's store before ranking.</summary>
    public int MemoryEntriesLoaded { get; init; }

    /// <summary>Memory entries that fit within the memory block budget and were injected.</summary>
    public int MemoryEntriesIncluded { get; init; }

    /// <summary>Total artifacts assembled (knowledge + session_context).</summary>
    public int ArtifactsAssembled { get; init; }

    /// <summary>Sum of characters across all messages in the final context.</summary>
    public int TotalContextChars { get; init; }

    /// <summary>Character length of the system prompt (0 when no system message).</summary>
    public int SystemPromptChars { get; init; }

    /// <summary>Character length of the memory block injected into the system prompt.</summary>
    public int MemoryChars { get; init; }

    /// <summary>
    /// Character length of the session context summary injected from disk (context_summary.md).
    /// 0 when the file does not exist or the agent uses an explicit Context: spec.
    /// </summary>
    public int SessionContextChars { get; init; }

    /// <summary>Character length of the knowledge artifact block injected into context.</summary>
    public int KnowledgeChars { get; init; }

    /// <summary>Sum of characters across filtered shared-history messages included in context.</summary>
    public int HistoryChars { get; init; }

    /// <summary>Total number of messages in the filtered history passed to the agent.</summary>
    public int HistoryMessageCount { get; init; }

    /// <summary>User-role message count within the filtered history.</summary>
    public int HistoryUserCount { get; init; }

    /// <summary>Assistant-role message count within the filtered history.</summary>
    public int HistoryAssistantCount { get; init; }

    /// <summary>Tool-role message count within the filtered history.</summary>
    public int HistoryToolCount { get; init; }

    /// <summary>
    /// Whether any message in the filtered history is a compaction summary.
    /// Useful for detecting whether cross-turn history is being replayed verbatim
    /// or has already been compressed by a compaction pass.
    /// </summary>
    public bool HistoryHasCompactionSummary { get; init; }

    /// <summary>Wall-clock time spent inside <c>AssembleAsync</c>.</summary>
    public TimeSpan AssemblyDuration { get; init; }

    public static readonly ContextAssemblyMetrics Empty = new();
}
