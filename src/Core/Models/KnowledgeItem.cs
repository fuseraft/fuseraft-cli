namespace fuseraft.Core.Models;

/// <summary>
/// A single piece of knowledge retrieved by the context assembly pipeline.
/// Distinct from <see cref="KnowledgeResult"/> (raw layer output) in that it
/// carries a normalised confidence score and is ready for prompt injection.
/// </summary>
public sealed record KnowledgeItem(
    string Id,
    string Kind,
    string Title,
    string Content,
    float  Confidence);
