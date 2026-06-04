using Microsoft.Extensions.AI;

namespace fuseraft.Core.Models;

/// <summary>
/// The fully assembled context produced by <see cref="fuseraft.Core.Interfaces.IContextAssemblyPipeline"/>
/// for a single agent invocation.
///
/// <para>
/// <see cref="Messages"/> is the ready-to-use message list to pass to <c>agent.RunAsync()</c>.
/// The system prompt is always the first message when non-empty.
/// <see cref="Artifacts"/> and <see cref="Knowledge"/> carry the typed sources used
/// to construct the context, enabling observability and debugging.
/// </para>
/// </summary>
public sealed record AssembledContext(
    string SystemPrompt,
    IReadOnlyList<ChatMessage>     Messages,
    IReadOnlyList<ContextArtifact> Artifacts,
    IReadOnlyList<KnowledgeItem>   Knowledge,
    TokenBudget                    Budget,
    ContextAssemblyMetrics         Metrics);
