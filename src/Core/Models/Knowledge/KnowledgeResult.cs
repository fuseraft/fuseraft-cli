namespace fuseraft.Core.Models.Knowledge;

/// <summary>Discriminates what kind of artifact a <see cref="KnowledgeResult"/> represents.</summary>
public enum KnowledgeKind { Decision, GraphNode, Memory, Claim, Objective }

/// <summary>Lightweight search result returned by <see cref="IKnowledgeLayer.SearchAsync"/>.</summary>
public sealed record KnowledgeResult
{
    public string           Id       { get; init; } = string.Empty;
    public KnowledgeKind    Kind     { get; init; }
    public string           Title    { get; init; } = string.Empty;
    public string?          Summary  { get; init; }
    public string?          FilePath { get; init; }
    public string?          Status   { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
}
