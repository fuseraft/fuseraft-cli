using Microsoft.Extensions.AI;

namespace fuseraft.Core.Models.Context;

/// <summary>
/// Result of <see cref="fuseraft.Orchestration.Context.ContextAssembler.AssembleForAgentAsync"/>.
/// </summary>
/// <param name="Messages">Ready-to-use message list replacing shared-history replay.</param>
/// <param name="EmptySources">
/// Declared artifact source specs (excluding <c>own_history</c>) that resolved to no content —
/// e.g. a <c>brief_field:</c> naming a field absent from <c>brief.json</c>.
/// </param>
public sealed record AgentContextAssembly(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<string>      EmptySources);
