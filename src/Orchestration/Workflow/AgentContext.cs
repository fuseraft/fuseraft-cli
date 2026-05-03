using System.Threading.Channels;
using Microsoft.Extensions.AI;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Workflow;

/// <summary>
/// Shared mutable context passed between workflow executors within a single phase.
/// All executors in a phase share the same instance, allowing them to build on the
/// same conversation history and stream messages back to the caller via the channel.
/// </summary>
public sealed class AgentContext
{
    /// <summary>Full conversation history shared across all agents in the session.</summary>
    public List<ChatMessage> History { get; } = new();

    /// <summary>Channel writer for streaming agent output back to the caller.</summary>
    public required ChannelWriter<AgentMessage> MessageSink { get; init; }

    /// <summary>The last routing keyword emitted by an agent. Set by each executor after it runs.</summary>
    public string? LastKeyword { get; set; }

    /// <summary>Running total of tokens (input + output) consumed by the session.</summary>
    public int CumulativeTokens { get; set; }

    /// <summary>Monotonically-increasing turn index, shared across all phases.</summary>
    public int TurnIndex { get; set; }

    /// <summary>
    /// The most recent immutable state snapshot. Advanced by
    /// <see cref="fuseraft.Orchestration.Workflow.StateHandoff.Advance"/> each time an
    /// agent successfully routes to the next step. The initial value is a version-0 snapshot
    /// created at session start.
    /// </summary>
    public AgentState CurrentState { get; set; } = AgentState.Initial("session");
}
