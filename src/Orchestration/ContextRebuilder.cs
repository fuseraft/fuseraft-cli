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
            sb.AppendLine("CONTRACT STATUS (at compaction time \u2014 retained turns below may supersede failures):");
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
                        if (node.ExitCode is not (null or 0) && !string.IsNullOrWhiteSpace(node.Output))
                        {
                            var snippet = node.Output.Length > 400 ? node.Output[..400] + "\u2026" : node.Output;
                            sb.AppendLine($"      Output: {snippet.Replace('\n', ' ').Trim()}");
                        }
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

        if (snapshot.ActiveAdrs.Count > 0)
        {
            sb.AppendLine("ACTIVE ARCHITECTURE DECISIONS:");
            foreach (var adr in snapshot.ActiveAdrs)
                sb.AppendLine($"  [{adr.Id}] {adr.Title} (status: {adr.Status})");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(snapshot.ObjectiveState))
        {
            sb.AppendLine("ACTIVE OBJECTIVES:");
            sb.AppendLine(snapshot.ObjectiveState.TrimEnd());
            sb.AppendLine();
        }

        if (snapshot.ArchitectureViolations.Count > 0)
        {
            sb.AppendLine($"ARCHITECTURE VIOLATIONS ({snapshot.ArchitectureViolations.Count} at compaction time \u2014 verify before merging):");
            foreach (var v in snapshot.ArchitectureViolations)
                sb.AppendLine($"  \u26a0 {v}");
            sb.AppendLine();
        }

        if (snapshot.TopRepositoryMemories.Count > 0)
        {
            sb.AppendLine("REPOSITORY MEMORY (approved cross-session patterns):");
            foreach (var mem in snapshot.TopRepositoryMemories)
                sb.AppendLine($"  \u2022 {mem}");
            sb.AppendLine();
        }

        if (snapshot.ExpiredProvenanceWarnings.Count > 0)
        {
            sb.AppendLine("EXPIRED PROVENANCE WARNINGS (re-verify before acting on these artifacts):");
            foreach (var w in snapshot.ExpiredProvenanceWarnings)
                sb.AppendLine($"  \u26a0 {w}");
            sb.AppendLine();
        }

        var stateHint = snapshot.CurrentStateName is not null
            ? $" Continue from state '{snapshot.CurrentStateName}'."
            : string.Empty;

        sb.Append(
            "RESUMPTION NOTE: History compacted. Evidence entries (file writes, commands) above are " +
            "ground-truth from durable records. Contract status reflects disk state at compaction time " +
            $"and may be superseded by evidence in the retained turns below \u2014 verify from disk " +
            $"before acting on any \u2717 failures.{stateHint}");

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
