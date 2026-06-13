using System.Text;
using fuseraft.Core.Models;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Diagram;

/// <summary>
/// Generates a Mermaid flowchart from an <see cref="OrchestrationConfig"/>.
///
/// <para>
/// For <c>keyword</c> selection every route becomes a labelled edge. Validators
/// appear as additional lines in the label. Terminal routes (self-routing convention:
/// <c>Agent</c> is listed in <c>SourceAgents</c>) point to a synthetic "Done" node
/// rather than back to the same agent.
/// </para>
/// <para>
/// For <c>sequential</c> selection agents are chained in declaration order.
/// </para>
/// </summary>
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
            case OrchestratorTypes.Keyword when config.Selection.Routes is { Count: > 0 }:
                RenderKeyword(sb, config);
                break;
            case OrchestratorTypes.Structured when config.Selection.StructuredRoutes is { Count: > 0 }:
                RenderStructured(sb, config);
                break;
            case OrchestratorTypes.Sequential:
                RenderSequential(sb, config);
                break;
            case OrchestratorTypes.Magentic:
                RenderMagentic(sb, config);
                break;
            case OrchestratorTypes.Graph when config.Selection.Graph is not null:
                RenderGraph(sb, config.Selection.Graph);
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

    private static void RenderKeyword(StringBuilder sb, OrchestrationConfig config)
    {
        var routes       = config.Selection.Routes!;
        var defaultAgent = config.Selection.DefaultAgent
                           ?? config.Agents.FirstOrDefault()?.Name;

        // Identify terminal keywords: any route where at least one SourceAgent equals Agent.
        var terminalKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            if (route.SourceAgents is { Count: > 0 } &&
                route.SourceAgents.Any(s =>
                    string.Equals(s, route.Agent, StringComparison.OrdinalIgnoreCase)))
            {
                terminalKeywords.Add(route.Keyword);
            }
        }

        // node declarations
        sb.AppendLine();
        if (defaultAgent is not null)
            sb.AppendLine("  Task([Task])");

        foreach (var agent in config.Agents)
            sb.AppendLine($"  {NodeId(agent.Name)}[\"{Esc(agent.Name)}\"]");

        if (terminalKeywords.Count > 0)
            sb.AppendLine("  Done([\"✓ Done\"])");

        // edges
        sb.AppendLine();

        // Task → default agent
        if (defaultAgent is not null)
        {
            sb.AppendLine($"  Task --> {NodeId(defaultAgent)}");
            sb.AppendLine();
        }

        foreach (var route in routes)
        {
            bool isTerminal = terminalKeywords.Contains(route.Keyword);
            string target   = isTerminal ? "Done" : NodeId(route.Agent);
            string label    = BuildLabel(route);

            if (route.SourceAgents is { Count: > 0 })
            {
                foreach (var src in route.SourceAgents)
                    sb.AppendLine($"  {NodeId(src)} -->|\"{label}\"| {target}");
            }
            else
            {
                // No source restriction — draw from every agent for completeness,
                // or from the default agent if that is cleaner.
                string src = defaultAgent is not null ? NodeId(defaultAgent) : "any";
                sb.AppendLine($"  {src} -->|\"{label}\"| {target}");
            }
        }
    }

    private static void RenderStructured(StringBuilder sb, OrchestrationConfig config)
    {
        var routes       = config.Selection.StructuredRoutes!;
        var defaultAgent = config.Selection.DefaultAgent
                           ?? config.Agents.FirstOrDefault()?.Name;

        // --- node declarations --------------------------------------------------
        sb.AppendLine();
        if (defaultAgent is not null)
            sb.AppendLine("  Task([Task])");

        foreach (var agent in config.Agents)
            sb.AppendLine($"  {NodeId(agent.Name)}[\"{Esc(agent.Name)}\"]");

        // --- edges --------------------------------------------------------------
        sb.AppendLine();

        if (defaultAgent is not null)
        {
            sb.AppendLine($"  Task --> {NodeId(defaultAgent)}");
            sb.AppendLine();
        }

        foreach (var route in routes)
        {
            string label = BuildConditionLabel(route.Condition);

            if (route.SourceAgents is { Count: > 0 })
            {
                foreach (var src in route.SourceAgents)
                    sb.AppendLine($"  {NodeId(src)} -->|\"{label}\"| {NodeId(route.Agent)}");
            }
            else
            {
                string src = defaultAgent is not null ? NodeId(defaultAgent) : "any";
                sb.AppendLine($"  {src} -->|\"{label}\"| {NodeId(route.Agent)}");
            }
        }
    }

    private static void RenderSequential(StringBuilder sb, OrchestrationConfig config)
    {
        sb.AppendLine();
        sb.AppendLine("  Task([Task])");
        foreach (var agent in config.Agents)
            sb.AppendLine($"  {NodeId(agent.Name)}[\"{Esc(agent.Name)}\"]");

        sb.AppendLine();
        if (config.Agents.Count > 0)
        {
            sb.AppendLine($"  Task --> {NodeId(config.Agents[0].Name)}");
            for (int i = 0; i < config.Agents.Count - 1; i++)
                sb.AppendLine(
                    $"  {NodeId(config.Agents[i].Name)} --> {NodeId(config.Agents[i + 1].Name)}");
        }
    }

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

    private static void RenderGraph(StringBuilder sb, GraphConfig graph)
    {
        var entryId = graph.EntryNode is { Length: > 0 }
            ? NodeId(graph.EntryNode)
            : (graph.Nodes.Count > 0 ? NodeId(graph.Nodes[0].Id) : "start");

        // node declarations
        sb.AppendLine();
        sb.AppendLine("  Task([Task])");
        foreach (var node in graph.Nodes)
        {
            var shape = node.Terminal  ? $"  {NodeId(node.Id)}([\"{Esc(node.Id)} ✓\"])"
                      : node.Parallel ? $"  {NodeId(node.Id)}[\"{Esc(node.Id)} ∥\"]"
                      :                  $"  {NodeId(node.Id)}[\"{Esc(node.Id)}\"]";
            sb.AppendLine(shape);
        }

        // edges
        sb.AppendLine();
        sb.AppendLine($"  Task --> {entryId}");

        foreach (var edge in graph.Edges)
        {
            var from  = NodeId(edge.From);
            var to    = NodeId(edge.To);
            var parts = new List<string>();
            if (edge.Keyword is { Length: > 0 }) parts.Add(Esc(edge.Keyword));

            var validators = edge.Validators is { Count: > 0 }
                ? edge.Validators
                : edge.Validator is { Length: > 0 }
                    ? [edge.Validator]
                    : (IEnumerable<string>)[];
            parts.AddRange(validators.Select(v => Esc(v)));

            if (edge.RequireHumanApproval) parts.Add("⚑ approval");

            var label = parts.Count > 0 ? string.Join("<br/>", parts) : string.Empty;
            var arrow = label.Length > 0 ? $"-->|\"{label}\"| " : "--> ";
            sb.AppendLine($"  {from} {arrow}{to}");
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
                if (t.Signal is { Length: > 0 }) parts.Add(Esc(t.Signal));

                var contracts = t.Contracts is { Count: > 0 }
                    ? t.Contracts
                    : t.Contract is { Length: > 0 }
                        ? [t.Contract]
                        : (IEnumerable<string>)[];
                parts.AddRange(contracts.Select(c => Esc(c)));

                var label = parts.Count > 0 ? string.Join("<br/>", parts) : string.Empty;
                var arrow = label.Length > 0 ? $"-->|\"{label}\"| " : "--> ";
                sb.AppendLine($"  {from} {arrow}{to}");
            }
        }
    }

    private static void RenderAdversarial(StringBuilder sb, fuseraft.Core.Models.AdversarialConfig adv)
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
    /// Builds a condition label for a structured route edge.
    /// Format: <c>field = value</c>, <c>field ≠ value</c>, <c>field ∋ value</c>, or <c>field exists</c>.
    /// </summary>
    private static string BuildConditionLabel(StructuredCondition c)
    {
        var field = Esc(c.Field);
        if (c.Is    is not null) return $"{field} = {Esc(c.Is)}";
        if (c.IsNot is not null) return $"{field} ≠ {Esc(c.IsNot)}";
        if (c.Contains  is not null) return $"{field} ∋ {Esc(c.Contains)}";
        if (c.Exists    is true)     return $"{field} exists";
        if (c.Exists    is false)    return $"{field} absent";
        return Esc(c.Field);
    }

    /// <summary>
    /// Builds the edge label: keyword on the first line, then one validator per line.
    /// Multi-line labels use HTML &lt;br/&gt; which all major Mermaid renderers support.
    /// </summary>
    private static string BuildLabel(KeywordRoute route)
    {
        var parts = new List<string> { Esc(route.Keyword) };

        var validators = route.Validators is { Count: > 0 }
            ? route.Validators
            : route.Validator is { Length: > 0 }
                ? [route.Validator]
                : (IEnumerable<string>)[];

        parts.AddRange(validators.Select(v => Esc(v)));
        return string.Join("<br/>", parts);
    }

    /// <summary>
    /// Converts an agent name to a valid Mermaid node ID (alphanumeric + underscore only).
    /// </summary>
    private static string NodeId(string name) =>
        new(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    /// <summary>
    /// Escapes characters that would break Mermaid label syntax inside double-quoted strings.
    /// </summary>
    private static string Esc(string text) =>
        text.Replace("\"", "#quot;")
            .Replace("<",  "#lt;")
            .Replace(">",  "#gt;");
}
