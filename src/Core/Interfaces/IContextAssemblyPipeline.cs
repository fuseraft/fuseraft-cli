using fuseraft.Core.Models;

namespace fuseraft.Core.Interfaces;

/// <summary>
/// Single entry point for all agent context construction.
///
/// <para>
/// Every agent invocation — sequential, parallel, state-machine transition,
/// handoff, review, or retry — must call <see cref="AssembleAsync"/> to obtain
/// its context. No orchestrator path may call <c>ContextWindowFilter.Apply()</c>
/// directly; that is an implementation detail of this pipeline.
/// </para>
///
/// <para>Pipeline stages (in order):</para>
/// <list type="number">
///   <item>System prompt — agent instructions augmented with relevance-ranked memory.</item>
///   <item>Intent analysis — extract keywords and symbols from the task description.</item>
///   <item>Knowledge retrieval — always-on query of the knowledge layer and repository memory.</item>
///   <item>Graph expansion — one-hop neighbour traversal for <c>KnowledgeWeight.High</c> agents.</item>
///   <item>Context budgeting — rank artifacts by confidence and trim to token limits.</item>
///   <item>Prompt construction — assemble the final <see cref="AssembledContext.Messages"/> list.</item>
/// </list>
/// </summary>
public interface IContextAssemblyPipeline
{
    /// <summary>
    /// Assembles the full context for a single agent invocation.
    /// The returned <see cref="AssembledContext.Messages"/> is ready to pass
    /// directly to <c>agent.RunAsync()</c>.
    /// </summary>
    Task<AssembledContext> AssembleAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Propagates the active session ID to session-scoped path resolution.</summary>
    void SetSessionId(string sessionId);
}
