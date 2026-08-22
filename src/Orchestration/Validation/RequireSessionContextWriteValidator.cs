using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;

namespace fuseraft.Orchestration.Validation;

/// <summary>
/// Blocks a handoff route unless the source agent called <c>session_context_write</c>
/// (<see cref="fuseraft.Infrastructure.Plugins.SessionContextPlugin.WriteAsync"/>) during the
/// current turn.
///
/// <para>
/// Not auto-attached — opt in explicitly with <c>Validators: [RequireSessionContextWrite]</c>
/// on a route/edge/transition whose source agent has
/// <see cref="fuseraft.Core.Models.Agents.AgentIsolation.Fresh"/> isolation. A <c>Fresh</c>
/// agent's own turn — tool calls, intermediate reasoning — never reaches the next agent;
/// only its <c>session_context_write</c> summary and the synthesized
/// <see cref="fuseraft.Core.Models.Agents.AgentDirective"/> do. Without this validator, an
/// agent that forgets to write a summary silently hands the next agent nothing; attaching it
/// turns that into a hard, visible failure at handoff time instead of a discovered-later
/// context gap. See skills/craft-orchestration/references/schema-cheatsheet.md's "Built-in
/// validators" table.
/// </para>
/// </summary>
public sealed class RequireSessionContextWriteValidator : IRoutingValidator
{
    public Task<RoutingValidationResult> ValidateAsync(
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i];

            // User messages mark the turn boundary — stop here.
            if (msg.Role == ChatRole.User) break;

            if (msg.Role != ChatRole.Tool) continue;

            foreach (var item in msg.Contents)
            {
                if (item is not FunctionResultContent frc) continue;
                var funcName = HistoryHelpers.FindFunctionName(history, frc.CallId, i) ?? string.Empty;
                if (funcName.Equals("session_context_write", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(RoutingValidationResult.Pass());
            }
        }

        return Task.FromResult(RoutingValidationResult.Fail(
            "Handoff blocked: this agent runs in isolated (Fresh) context — the next agent will " +
            "not see this conversation, only what you write to session_context_write.\n\n" +
            "  1. Call session_context_write(summary: \"...\") — what you accomplished, files " +
            "changed, and anything the next agent needs to know.\n" +
            "  2. Emit the handoff keyword in the same response."));
    }
}
