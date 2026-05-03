using System.Text;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration;

/// <summary>
/// Converts a <see cref="ContextSnapshot"/> into an <see cref="AgentMessage"/> that is
/// injected into conversation history in place of (or alongside) an LLM-generated summary.
///
/// <para>
/// Because the snapshot is derived entirely from durable disk artifacts — the evidence
/// graph, contract evaluations, and state machine position — no hallucination is possible.
/// The resulting message is authoritative: agents must not contradict it.
/// </para>
/// </summary>
public static class ContextRebuilder
{
    private const int MaxRecentEvidence = 20;

    /// <summary>
    /// Builds a compaction <see cref="AgentMessage"/> from <paramref name="snapshot"/>.
    /// </summary>
    public static AgentMessage BuildContextMessage(ContextSnapshot snapshot, int turnIndex = 0)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[CONTEXT RECONSTRUCTION — derived from durable evidence, not summarised by LLM]");
        sb.AppendLine();

        if (snapshot.CurrentStateName is not null)
        {
            sb.AppendLine($"STATE MACHINE POSITION: {snapshot.CurrentStateName}");
            sb.AppendLine();
        }

        if (snapshot.ContractResults.Count > 0)
        {
            sb.AppendLine("CONTRACT STATUS:");
            foreach (var r in snapshot.ContractResults.Where(r => r.Passed))
                sb.AppendLine($"  \u2713 {r.Name}");
            foreach (var r in snapshot.ContractResults.Where(r => !r.Passed))
            {
                sb.AppendLine($"  \u2717 {r.Name}");
                if (r.Error is not null)
                    sb.AppendLine($"      Reason: {r.Error.Replace('\n', ' ').Trim()}");
            }
            sb.AppendLine();
        }

        var evidence = snapshot.RecentEvidence.Take(MaxRecentEvidence).ToList();
        if (evidence.Count > 0)
        {
            sb.AppendLine("RECENT EVIDENCE (newest first):");
            foreach (var node in evidence)
            {
                sb.Append($"  [{node.NodeType}] ");
                switch (node.NodeType?.ToLowerInvariant())
                {
                    case "filewrite":
                        sb.AppendLine($"wrote {node.Path} (turn {node.Turn}, agent {node.Agent})");
                        break;
                    case "filedelete":
                        sb.AppendLine($"deleted {node.Path} (turn {node.Turn}, agent {node.Agent})");
                        break;
                    case "commandrun":
                        var exitStr = node.ExitCode.HasValue ? $" \u2192 exit {node.ExitCode}" : string.Empty;
                        sb.AppendLine($"{node.Command}{exitStr} (turn {node.Turn}, agent {node.Agent})");
                        break;
                    case "gitcommit":
                        sb.AppendLine($"{node.CommitMessage} (turn {node.Turn}, agent {node.Agent})");
                        break;
                    case "testresult":
                        sb.AppendLine($"{node.Criterion}: {node.Status} (turn {node.Turn}, agent {node.Agent})");
                        break;
                    default:
                        sb.AppendLine("(unknown type)");
                        break;
                }
            }
            sb.AppendLine();
        }

        var stateHint = snapshot.CurrentStateName is not null
            ? $" Continue from state '{snapshot.CurrentStateName}'."
            : string.Empty;

        var unsatisfied = snapshot.ContractResults.Any(r => !r.Passed);
        var contractHint = unsatisfied
            ? " Satisfy all \u2717 contracts before emitting a transition signal."
            : string.Empty;

        sb.Append(
            "RESUMPTION NOTE: History compacted. The above is ground-truth derived from the evidence " +
            $"graph \u2014 it is authoritative. Do not contradict it.{stateHint}{contractHint}");

        return new AgentMessage
        {
            AgentName           = "System",
            Content             = sb.ToString().TrimEnd(),
            Role                = "user",
            TurnIndex           = turnIndex,
            IsCompactionSummary = true,
        };
    }
}
