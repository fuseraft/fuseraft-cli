using System.Text;
using fuseraft.Core.Models;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Diagram;

/// <summary>Generates a Mermaid flowchart of a compiled team: each flow edge labelled with its gates.</summary>
public static class WorkflowDiagramGenerator
{
    /// <summary>
    /// Returns a Mermaid flowchart (LR orientation) describing the workflow.
    /// Paste the output into <see href="https://mermaid.live"/> or any Mermaid renderer.
    /// </summary>
    public static string ToMermaid(OrchestrationConfig config)
    {
        var sb = new StringBuilder();
        sb.AppendLine("flowchart LR");

        switch (config.Selection.Type.ToLowerInvariant())
        {
            case OrchestratorTypes.Magentic:
                RenderMagentic(sb, config);
                break;
            case OrchestratorTypes.StateMachine when config.Selection.StateMachine is not null:
                RenderStateMachine(sb, config.Selection.StateMachine);
                break;
            case OrchestratorTypes.Adversarial when config.Selection.Adversarial is not null:
                RenderAdversarial(sb, config.Selection.Adversarial);
                break;
            default:
                RenderGeneric(sb, config);
                break;
        }

        return sb.ToString().TrimEnd();
    }

    // Renderers

    private static void RenderMagentic(StringBuilder sb, OrchestrationConfig config)
    {
        // node declarations
        sb.AppendLine();
        sb.AppendLine("  Task([Task])");
        sb.AppendLine("  Manager([\"Manager\"])");
        foreach (var agent in config.Agents)
            sb.AppendLine($"  {NodeId(agent.Name)}[\"{Esc(agent.Name)}\"]");

        // edges
        sb.AppendLine();
        sb.AppendLine("  Task --> Manager");
        foreach (var agent in config.Agents)
        {
            sb.AppendLine($"  Manager -->|\"selects\"| {NodeId(agent.Name)}");
            sb.AppendLine($"  {NodeId(agent.Name)} -.->|\"reports\"| Manager");
        }
    }

    private static void RenderStateMachine(StringBuilder sb, StateMachineConfig sm)
    {
        // node declarations
        sb.AppendLine();
        sb.AppendLine("  Task([Task])");
        foreach (var (name, state) in sm.States)
        {
            // Include the agent name when it differs from the state name so the
            // diagram communicates which agent is active in each state.
            var labelText = state.Agent is { Length: > 0 } &&
                            !string.Equals(state.Agent, name, StringComparison.OrdinalIgnoreCase)
                ? $"{Esc(name)}<br/>[{Esc(state.Agent)}]"
                : Esc(name);
            var shape = state.Terminal
                ? $"  {NodeId(name)}([\"{labelText} ✓\"])"
                : $"  {NodeId(name)}[\"{labelText}\"]";
            sb.AppendLine(shape);
        }

        // edges
        sb.AppendLine();
        if (sm.Initial is { Length: > 0 })
            sb.AppendLine($"  Task --> {NodeId(sm.Initial)}");

        foreach (var (name, state) in sm.States)
        {
            foreach (var t in state.Transitions)
            {
                var from  = NodeId(name);
                var to    = NodeId(t.To);
                var parts = new List<string>();
                // A route named after its target says nothing the arrow doesn't already.
                if (t.Signal is { Length: > 0 } && !string.Equals(t.Signal, t.To, StringComparison.OrdinalIgnoreCase))
                    parts.Add(Esc(t.Signal));

                var contracts = t.Contract is { Length: > 0 } ? [t.Contract] : (IEnumerable<string>)[];
                parts.AddRange(contracts.Select(c => Esc(c)));
                if (t.RequireHumanApproval) parts.Add("⚑ approval");

                var label = parts.Count > 0 ? string.Join("<br/>", parts) : string.Empty;
                var arrow = label.Length > 0 ? $"-->|\"{label}\"| " : "--> ";
                sb.AppendLine($"  {from} {arrow}{to}");
            }
        }
    }

    private static void RenderAdversarial(StringBuilder sb, AdversarialConfig adv)
    {
        sb.AppendLine();
        sb.AppendLine("  Task([Task])");

        for (int i = 0; i < adv.Stages.Count; i++)
        {
            var stage   = adv.Stages[i];
            var label   = stage.Label ?? $"{stage.Generator} → {stage.Critic}";
            var genId   = $"gen{i}";
            var critId  = $"crit{i}";
            var passId  = $"pass{i}";

            sb.AppendLine($"  {genId}[\"{Esc(stage.Generator)}\"]");
            sb.AppendLine($"  {critId}[\"{Esc(stage.Critic)}\n(critic)\"]");
            sb.AppendLine($"  {passId}([\"{Esc(label)} approved\"])");

            if (i == 0)
                sb.AppendLine($"  Task --> {genId}");
            else
                sb.AppendLine($"  pass{i - 1} --> {genId}");

            sb.AppendLine($"  {genId} -->|artifact| {critId}");
            sb.AppendLine($"  {critId} -->|\"{adv.PassKeyword}\"| {passId}");
            sb.AppendLine($"  {critId} -->|revise| {genId}");
        }

        if (adv.Stages.Count > 0)
        {
            sb.AppendLine($"  pass{adv.Stages.Count - 1} --> Done([Done])");
        }
    }

    private static void RenderGeneric(StringBuilder sb, OrchestrationConfig config)
    {
        sb.AppendLine();
        sb.AppendLine("  Task([Task])");
        foreach (var agent in config.Agents)
            sb.AppendLine($"  {NodeId(agent.Name)}[\"{Esc(agent.Name)}\"]");
    }

    // Helpers

    /// <summary>
    /// Converts an agent name to a valid Mermaid node ID (alphanumeric + underscore only).
    /// </summary>
    // Mermaid reserves some words as node ids ("end" closes a subgraph), so prefix those.
    private static readonly HashSet<string> MermaidReserved = new(StringComparer.OrdinalIgnoreCase) { "end", "graph", "subgraph", "flowchart", "style", "class", "click" };

    private static string NodeId(string name)
    {
        var id = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return MermaidReserved.Contains(id) ? "n_" + id : id;
    }

    /// <summary>
    /// Escapes characters that would break Mermaid label syntax inside double-quoted strings.
    /// </summary>
    private static string Esc(string text) =>
        text.Replace("\"", "#quot;")
            .Replace("<",  "#lt;")
            .Replace(">",  "#gt;");
}
