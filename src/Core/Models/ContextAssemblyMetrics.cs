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

    /// <summary>Wall-clock time spent inside <c>AssembleAsync</c>.</summary>
    public TimeSpan AssemblyDuration { get; init; }

    public static readonly ContextAssemblyMetrics Empty = new();
}
