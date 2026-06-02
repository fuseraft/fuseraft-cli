namespace fuseraft.Core.Models;

/// <summary>Full artifact returned by <see cref="IKnowledgeLayer.RetrieveAsync"/>.</summary>
public sealed record KnowledgeArtifact
{
    public string               Id        { get; init; } = string.Empty;
    public KnowledgeKind        Kind      { get; init; }
    public AdrEntry?            Decision  { get; init; }
    public RepositoryGraphNode? GraphNode { get; init; }
}
