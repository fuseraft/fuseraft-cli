using Microsoft.Extensions.AI;
using fuseraft.Core.Models;

namespace fuseraft.Core.Interfaces;

/// <summary>
/// Deterministic pre-flight check run before a keyword route fires.
/// Receives the current chat history so it can inspect tool calls, file contents,
/// and disk artifacts without making an LLM call.
///
/// When <see cref="ValidateAsync"/> returns a failing <see cref="RoutingValidationResult"/>
/// the selection strategy blocks the intended route, injects the error message into the chat
/// as a user message, and re-routes to the agent that emitted the handoff keyword so it can
/// correct the problem.
/// </summary>
public interface IRoutingValidator
{
    Task<RoutingValidationResult> ValidateAsync(
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default);
}
