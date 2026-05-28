using Microsoft.Agents.AI;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Parallel;

/// <summary>
/// Describes a parallel fan-out: the agents to run concurrently, how to merge
/// their outputs, and the join state to enter after the merge completes.
/// </summary>
public sealed record ParallelAgentBatch(
    IReadOnlyList<(AIAgent Agent, string StateName)> Branches,
    MergeConfig Merge,
    string JoinState);
