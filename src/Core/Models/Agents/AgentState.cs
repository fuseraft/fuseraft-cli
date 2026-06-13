namespace fuseraft.Core.Models.Agents;

/// <summary>
/// Immutable versioned snapshot of the data crossing an agent handoff boundary.
/// Produced by <see cref="fuseraft.Orchestration.Workflow.StateHandoff.Advance"/> and never mutated after creation.
/// </summary>
public sealed record AgentState
{
    /// <summary>Monotonically-increasing snapshot number. Starts at 0 for the initial state.</summary>
    public required int Version { get; init; }

    /// <summary>Name of the agent that produced this snapshot.</summary>
    public required string CreatedBy { get; init; }

    /// <summary>UTC timestamp when the snapshot was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Arbitrary key-value payload carried across agent boundaries.</summary>
    public required IReadOnlyDictionary<string, object?> Data { get; init; }

    /// <summary>Returns an initial (version 0) state with an empty data payload.</summary>
    public static AgentState Initial(string createdBy) => new()
    {
        Version   = 0,
        CreatedBy = createdBy,
        CreatedAt = DateTimeOffset.UtcNow,
        Data      = new Dictionary<string, object?>()
    };
}
