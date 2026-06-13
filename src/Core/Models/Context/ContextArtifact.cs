namespace fuseraft.Core.Models.Context;

/// <summary>
/// A typed, titled chunk of context that the <see cref="fuseraft.Orchestration.ContextAssemblyPipeline"/>
/// assembles and budgets before constructing the final prompt.
///
/// <para>
/// Using artifacts instead of raw strings makes retrieval explainable and debuggable:
/// callers can inspect which artifacts were included, what type they are, and with
/// what priority they were ranked.
/// </para>
/// </summary>
public sealed record ContextArtifact(
    string Type,
    string Title,
    string Content,
    int Priority);
