using Microsoft.Extensions.AI;

namespace fuseraft.Core.Models.Agents;

/// <summary>
/// All information needed by <see cref="fuseraft.Core.Interfaces.IContextAssemblyPipeline"/>
/// to produce an <see cref="AssembledContext"/> for a single agent invocation.
/// </summary>
public sealed record AgentExecutionRequest
{
    /// <summary>Name of the agent that will consume the assembled context.</summary>
    public required string AgentName { get; init; }

    /// <summary>The original task or user request for this session.</summary>
    public required string Task { get; init; }

    /// <summary>The shared conversation history accumulated so far.</summary>
    public required IReadOnlyList<ChatMessage> SharedHistory { get; init; }

    /// <summary>Per-agent configuration (context window, knowledge weight, context sources, etc.).</summary>
    public AgentConfig? AgentConfig { get; init; }

    /// <summary>Active session ID, used to resolve session-scoped paths.</summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Additional runtime instructions to append to the agent's static instructions.
    /// Populated by <see cref="fuseraft.Infrastructure.MemoryManager"/> per-turn augmentation.
    /// </summary>
    public string? AdditionalInstructions { get; init; }
}
