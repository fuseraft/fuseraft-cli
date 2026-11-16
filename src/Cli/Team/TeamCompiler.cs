using System.Text.Json;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Agents;
using fuseraft.Core.Models.Team;
using fuseraft.Orchestration;
using fuseraft.Orchestration.Contracts;

namespace fuseraft.Cli.Team;

/// <summary>Tracks facts several checks must agree on while a team compiles.</summary>
internal sealed class TeamCompileState
{
    public string? BriefPath { get; private set; }
    public string? BriefName { get; private set; }
    public string? TestReportPath { get; private set; }
    public bool UsesTestSelector { get; private set; }
    private string? _testSelectorAt;

    public void NoteBrief(string name, string path, string at)
    {
        if (BriefName is not null && BriefName != name)
            throw new TeamConfigException($"{at}: brief checks already use '{BriefName}'; only one brief artifact is supported per team.");
        BriefPath = path;
        BriefName = name;
    }

    public void NoteTestReport(string path, string at)
    {
        if (TestReportPath is not null && !TestReportPath.Equals(path, StringComparison.Ordinal))
            throw new TeamConfigException($"{at}: only one test report artifact is supported; '{TestReportPath}' is already used.");
        TestReportPath = path;
    }

    public void NeedsTestSelector(string at)
    {
        UsesTestSelector = true;
        _testSelectorAt ??= at;
    }

    public string? TestSelectorAt => _testSelectorAt;
}

/// <summary>Compiles a v2 <see cref="TeamConfig"/> into the runtime <see cref="OrchestrationConfig"/>.</summary>
public static class TeamCompiler
{
    /// <summary>The state name an edge to <c>end</c> targets.</summary>
    public const string EndState = "end";

    private static readonly string[] Patterns = ["magentic", "adversarial", "mapreduce", "scattergather"];

    private static readonly HashSet<string> AdvancedTopLevel = new(StringComparer.OrdinalIgnoreCase)
    {
        "systemPrompt", "systemPromptPath", "warnTurnTokens", "contextBudget", "compaction", "scratchpad",
        "chatroom", "telemetry", "checkpoint", "apiProfiles", "verifier", "failureHandling",
        "brownfield", "testSelector", "skillCuration", "memory", "output",
    };

    private static readonly HashSet<string> AdvancedAgent = new(StringComparer.OrdinalIgnoreCase)
    {
        "functionChoice", "trustScore", "contextWindow", "context", "skipExecutionState", "capabilities",
        "maxToolCallsPerTurn", "maxInTurnContextTokens", "maxInTurnToolPairs", "knowledgeWeight",
        "subagentModel", "subagentPlugins", "subagentMaxToolCalls",
    };

    /// <summary>Compiles <paramref name="team"/>; throws <see cref="TeamConfigException"/> naming the first problem.</summary>
    public static OrchestrationConfig Compile(TeamConfig team)
    {
        if (string.IsNullOrWhiteSpace(team.Team))
            throw new TeamConfigException("team: give the team a name, e.g. 'team: DevTeam'.");
        if (team.Agents.Count == 0)
            throw new TeamConfigException("agents: declare at least one agent.");
        foreach (var name in team.Agents.Keys)
            if (name.Equals(EndState, StringComparison.OrdinalIgnoreCase))
                throw new TeamConfigException("agents.end: 'end' is reserved for the edge that finishes a run; rename the agent.");
        foreach (var (name, artifact) in team.Artifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.Path))
                throw new TeamConfigException($"artifacts.{name}: path is required.");
            if (TeamChecks.IsCheck(name))
                throw new TeamConfigException($"artifacts.{name}: '{name}' is also a check name, so 'requires: [{name}]' would be ambiguous; rename the artifact (e.g. '{name}File').");
            if (artifact.Shape is { } shape && ArtifactShape.Describe(shape, $"artifacts.{name}.shape") is { } shapeError)
                throw new TeamConfigException(shapeError);
        }

        var state  = new TeamCompileState();
        var advanced = ReadAdvanced(team.Advanced);
        var flow   = CompileFlow(team, state);

        if (state.UsesTestSelector && advanced.TestSelector is null)
            throw new TeamConfigException($"{state.TestSelectorAt}: relatedTestsPass needs advanced.testSelector (findRelatedCommand and fullSuiteCommand).");

        var agents = team.Agents.Select(kv => CompileAgent(kv.Key, kv.Value, team, flow)).ToList();

        if (advanced.Verifier is { AgentName: { Length: > 0 } verifierName } && !team.Agents.ContainsKey(verifierName))
            throw new TeamConfigException($"advanced.verifier.agentName: '{verifierName}' isn't declared under agents.");

        var validation = new ValidationConfig();
        validation = validation with
        {
            BriefPath      = state.BriefPath ?? (team.Artifacts.TryGetValue("brief", out var b) ? b.Path : validation.BriefPath),
            TestReportPath = state.TestReportPath ?? (team.Artifacts.TryGetValue("testResults", out var tr) ? tr.Path : validation.TestReportPath),
        };

        return advanced with
        {
            Name           = team.Team,
            Description    = team.Description,
            Models         = team.Models,
            Agents         = agents,
            Selection      = flow.Selection,
            MaxTurns       = (team.Limits ?? new()).MaxTurns,
            MaxTotalTokens = team.Limits?.MaxTokens,
            Security       = team.Security ?? new SecurityConfig(),
            McpServers     = team.McpServers ?? [],
            Contracts      = flow.Contracts,
            Validation     = validation,
            ChangeTracking = new ChangeTrackingConfig(),
            EvidenceStore  = new EvidenceStoreConfig(),
            Events         = new EventsConfig(),
            Checkpoint     = advanced.Checkpoint ?? new CheckpointConfig(),
        };
    }

    private sealed record CompiledFlow(
        SelectionStrategyConfig Selection,
        List<ContractConfig> Contracts,
        Dictionary<string, List<(HandoffRoute Route, List<string> Gates)>> Routes,
        HashSet<string> FlowAgents);

    private static CompiledFlow CompileFlow(TeamConfig team, TeamCompileState state)
    {
        if (team.Flow.ValueKind != JsonValueKind.Object)
            throw new TeamConfigException("flow: required. Use 'start' + 'steps', or a 'pattern'.");

        var props = team.Flow.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        return props.ContainsKey("Pattern") ? CompilePattern(team, props) : CompileSteps(team, props, state);
    }

    private static CompiledFlow CompilePattern(TeamConfig team, Dictionary<string, JsonElement> props)
    {
        var pattern = props["Pattern"].GetString()?.ToLowerInvariant() ?? "";
        if (!Patterns.Contains(pattern))
            throw new TeamConfigException($"flow.pattern: '{pattern}' isn't a pattern. Use {string.Join(", ", Patterns)}, or write steps.");

        var rest = new Dictionary<string, JsonElement>(props, StringComparer.OrdinalIgnoreCase);
        rest.Remove("Pattern");
        var body = JsonSerializer.SerializeToElement(rest);
        var at = $"flow ({pattern})";

        void RequireAgent(string? name, string field)
        {
            if (string.IsNullOrEmpty(name))
                throw new TeamConfigException($"{at}: {field} is required.");
            if (!team.Agents.ContainsKey(name))
                throw new TeamConfigException($"{at}: {field} '{name}' isn't declared under agents.");
        }

        var selection = new SelectionStrategyConfig { Type = pattern };
        switch (pattern)
        {
            case "magentic":
            {
                if (rest.TryGetValue("Model", out var model) && model.ValueKind == JsonValueKind.String)
                    rest["Model"] = JsonSerializer.SerializeToElement(new { ModelId = model.GetString() });
                var cfg = TeamConfigLoader.Deserialize<MagenticManagerConfig>(JsonSerializer.SerializeToElement(rest), at);
                if (cfg.Model is null) throw new TeamConfigException($"{at}: model is required; the manager plans and picks who works next.");
                if (cfg.MaxRoundCount < 1) throw new TeamConfigException($"{at}: maxRoundCount must be at least 1 (got {cfg.MaxRoundCount}).");
                if (cfg.MaxStallCount < 1) throw new TeamConfigException($"{at}: maxStallCount must be at least 1 (got {cfg.MaxStallCount}).");
                if (cfg.MaxResetCount < 0) throw new TeamConfigException($"{at}: maxResetCount must be >= 0 (got {cfg.MaxResetCount}).");
                selection = selection with { Magentic = cfg };
                break;
            }
            case "adversarial":
            {
                var cfg = TeamConfigLoader.Deserialize<AdversarialConfig>(body, at);
                if (cfg.Stages.Count == 0) throw new TeamConfigException($"{at}: stages needs at least one generator/critic pair.");
                if (cfg.Rounds < 1) throw new TeamConfigException($"{at}: rounds must be at least 1 (got {cfg.Rounds}).");
                if (string.IsNullOrWhiteSpace(cfg.PassKeyword)) throw new TeamConfigException($"{at}: passKeyword can't be empty.");
                foreach (var stage in cfg.Stages)
                {
                    RequireAgent(stage.Generator, "stages[].generator");
                    RequireAgent(stage.Critic, "stages[].critic");
                }
                selection = selection with { Adversarial = cfg };
                break;
            }
            case "mapreduce":
            {
                var cfg = TeamConfigLoader.Deserialize<MapReduceConfig>(body, at);
                RequireAgent(cfg.Splitter, "splitter");
                RequireAgent(cfg.Mapper, "mapper");
                RequireAgent(cfg.Reducer, "reducer");
                if (cfg.MaxConcurrency < 0) throw new TeamConfigException($"{at}: maxConcurrency must be >= 0 (got {cfg.MaxConcurrency}); use 0 for unlimited.");
                if (cfg.MaxSplitterRetries < 1) throw new TeamConfigException($"{at}: maxSplitterRetries must be at least 1 (got {cfg.MaxSplitterRetries}).");
                if (string.IsNullOrWhiteSpace(cfg.ItemsJsonPath)) throw new TeamConfigException($"{at}: itemsJsonPath can't be empty.");
                selection = selection with { MapReduce = cfg };
                break;
            }
            case "scattergather":
            {
                var cfg = TeamConfigLoader.Deserialize<ScatterGatherConfig>(body, at);
                if (cfg.Participants.Count == 0) throw new TeamConfigException($"{at}: participants needs at least one agent.");
                foreach (var p in cfg.Participants) RequireAgent(p, "participants[]");
                RequireAgent(cfg.Synthesizer, "synthesizer");
                if (cfg.MaxConcurrency < 0) throw new TeamConfigException($"{at}: maxConcurrency must be >= 0 (got {cfg.MaxConcurrency}); use 0 for unlimited.");
                selection = selection with { ScatterGather = cfg };
                break;
            }
        }

        return new(selection, [], [], []);
    }

    private static CompiledFlow CompileSteps(TeamConfig team, Dictionary<string, JsonElement> props, TeamCompileState state)
    {
        foreach (var key in props.Keys)
            if (!key.Equals("Start", StringComparison.OrdinalIgnoreCase) && !key.Equals("Steps", StringComparison.OrdinalIgnoreCase))
                throw new TeamConfigException($"flow: unknown field '{key}'. A steps flow takes start and steps; a pattern flow starts with pattern.");

        var start = props.TryGetValue("Start", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : null;
        if (start is null) throw new TeamConfigException("flow.start: name the agent that runs first.");
        if (!team.Agents.ContainsKey(start)) throw new TeamConfigException($"flow.start: '{start}' isn't declared under agents.");

        if (!props.TryGetValue("Steps", out var stepsEl) || stepsEl.ValueKind != JsonValueKind.Object)
            throw new TeamConfigException("flow.steps: list each agent's outgoing edges, e.g. 'Planner: [{ to: Developer }]'.");

        var states    = new Dictionary<string, StateConfig>(StringComparer.Ordinal);
        var contracts = new List<ContractConfig>();
        var routes    = new Dictionary<string, List<(HandoffRoute, List<string>)>>(StringComparer.Ordinal);
        var targets   = new List<(string Target, string At)>();
        var branchOnly = new HashSet<string>(StringComparer.Ordinal);
        var usesEnd   = false;

        foreach (var step in stepsEl.EnumerateObject())
        {
            var stepAt = $"flow.steps.{step.Name}";
            if (!team.Agents.ContainsKey(step.Name))
                throw new TeamConfigException($"{stepAt}: a step is named after the agent that runs it, and '{step.Name}' isn't declared under agents.");
            if (step.Value.ValueKind != JsonValueKind.Array)
                throw new TeamConfigException($"{stepAt}: expected a list of edges, e.g. '- to: Tester'.");

            var transitions = new List<TransitionConfig>();
            var stepRoutes  = new List<(HandoffRoute, List<string>)>();
            var signals     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var i = 0;

            foreach (var edgeEl in step.Value.EnumerateArray())
            {
                var at = $"{stepAt}[{i++}]";
                var edge = ReadEdge(edgeEl, at);

                var parallel = edge.To.Count > 1;
                if (parallel && edge.Join is null)
                    throw new TeamConfigException($"{at}: an edge to several agents runs them in parallel and needs 'join:' naming the agent that runs after them.");
                if (!parallel && edge.Join is not null)
                    throw new TeamConfigException($"{at}: join only applies when to lists several agents.");
                if (!parallel && edge.Merge is not null)
                    throw new TeamConfigException($"{at}: merge only applies when to lists several agents.");
                if (edge.Merge is { Strategy: MergeStrategy.Ranked or MergeStrategy.SemanticDiff } m
                    && (m.Agent is null || !team.Agents.ContainsKey(m.Agent)))
                    throw new TeamConfigException($"{at}.merge: strategy {m.Strategy} needs agent, naming a declared agent that ranks or reconciles the branches.");

                foreach (var t in edge.To)
                {
                    if (t.Equals(EndState, StringComparison.OrdinalIgnoreCase))
                    {
                        if (parallel) throw new TeamConfigException($"{at}: 'end' can't be a parallel branch.");
                        usesEnd = true;
                        continue;
                    }
                    if (!team.Agents.ContainsKey(t))
                        throw new TeamConfigException($"{at}: to '{t}' isn't declared under agents.");
                    if (parallel) branchOnly.Add(t);
                    else targets.Add((t, at));
                }
                if (edge.Join is { } join)
                {
                    if (!team.Agents.ContainsKey(join))
                        throw new TeamConfigException($"{at}: join '{join}' isn't declared under agents.");
                    targets.Add((join, at));
                }
                if (edge.OnStuck is { } stuck && !stuck.Equals("abort", StringComparison.OrdinalIgnoreCase) && !team.Agents.ContainsKey(stuck))
                    throw new TeamConfigException($"{at}: onStuck '{stuck}' isn't an agent; name an agent or 'abort'.");

                var isEnd  = !parallel && edge.To[0].Equals(EndState, StringComparison.OrdinalIgnoreCase);
                var signal = edge.Name ?? (parallel ? string.Join("+", edge.To) : isEnd ? EndState : edge.To[0]);
                if (!signals.Add(signal))
                    throw new TeamConfigException($"{at}: '{step.Name}' already has an edge named '{signal}'; give one of them a name.");

                string? contractName = null;
                var gateText = new List<string>();
                if (edge.Requires.Count > 0)
                {
                    var predicates = new List<ContractPredicate>();
                    var j = 0;
                    foreach (var req in edge.Requires)
                    {
                        var compiled = TeamChecks.Compile(req, team, $"{at}.requires[{j++}]", state);
                        predicates.AddRange(compiled.Predicates);
                        gateText.Add(compiled.Description);
                    }
                    // Named after its gates, so diagrams and failure messages say what is checked.
                    contractName = string.Join(" + ", edge.Requires.Select(GateLabel));
                    if (contracts.Any(c => c.Name == contractName))
                        contractName += $" ({step.Name} → {signal})";
                    contracts.Add(new ContractConfig { Name = contractName, Requires = predicates });
                }

                transitions.Add(new TransitionConfig
                {
                    To            = parallel ? edge.Join! : isEnd ? EndState : edge.To[0],
                    Targets       = parallel ? edge.To : null,
                    Parallel      = parallel,
                    Merge         = edge.Merge,
                    Signal        = signal,
                    Contract      = contractName,
                    RecoveryAgent = edge.OnStuck is { } r && !r.Equals("abort", StringComparison.OrdinalIgnoreCase) ? r : null,
                    MaxRevisits   = edge.MaxRevisits,
                    RequireHumanApproval = edge.Approve,
                    // Escalation after MaxRevisits quotes the reviewing agent's own output.
                    ReviewArtifactPath = edge.MaxRevisits > 0 && team.Agents[step.Name].Writes.FirstOrDefault() is { } reviewArt
                        ? team.Artifacts[reviewArt].Path
                        : null,
                });

                var verb = isEnd ? "finish the run" : parallel ? $"run {string.Join(" and ", edge.To)} in parallel, then {edge.Join}" : $"hand off to {edge.To[0]}";
                var description = edge.When is { } w ? $"{verb} when {w}." : $"{verb}.";
                if (edge.Approve) description += " A person approves this handoff after the checks pass.";
                stepRoutes.Add((new HandoffRoute(signal, description), gateText));
            }

            if (transitions.Count == 0)
                throw new TeamConfigException($"{stepAt}: needs at least one edge; use '- to: end' to finish the run here.");

            states[step.Name] = new StateConfig { Agent = step.Name, Transitions = transitions };
            routes[step.Name] = stepRoutes;
        }

        foreach (var (target, at) in targets)
            if (!states.ContainsKey(target))
                throw new TeamConfigException($"{at}: '{target}' has no entry under flow.steps, so the run would stall there. Add its edges, e.g. '- to: end'.");
        foreach (var branch in branchOnly.Where(b => !states.ContainsKey(b)))
            states[branch] = new StateConfig { Agent = branch, Transitions = [] };
        if (!states.ContainsKey(start))
            throw new TeamConfigException($"flow.start: '{start}' has no entry under flow.steps.");
        if (!usesEnd)
            throw new TeamConfigException("flow.steps: no edge goes to 'end', so the run can only stop at limits.maxTurns. Add '- to: end' where the work is finished.");

        states[EndState] = new StateConfig { Agent = string.Empty, Terminal = true };

        var selection = new SelectionStrategyConfig
        {
            Type         = OrchestratorTypes.StateMachine,
            StateMachine = new StateMachineConfig { Initial = start, States = states },
        };
        var flowAgents = new HashSet<string>(states.Values.Select(v => v.Agent).Where(a => a.Length > 0), StringComparer.Ordinal);
        return new(selection, contracts, routes, flowAgents);
    }

    private static string GateLabel(JsonElement item) => item.ValueKind switch
    {
        JsonValueKind.String => TeamChecks.IsCheck(item.GetString()!) ? TeamChecks.Canonical(item.GetString()!) : item.GetString()!,
        JsonValueKind.Object => item.EnumerateObject().First(p => TeamChecks.IsCheck(p.Name)) is var check
            && check.Value.ValueKind == JsonValueKind.String
                ? $"{TeamChecks.Canonical(check.Name)} {check.Value.GetString()}"
                : TeamChecks.Canonical(check.Name),
        _ => item.GetRawText(),
    };

    private sealed record Edge(
        List<string> To, string? Name, string? When, List<JsonElement> Requires,
        string? OnStuck, string? Join, MergeConfig? Merge, int MaxRevisits, bool Approve);

    private static readonly HashSet<string> EdgeFields = new(StringComparer.OrdinalIgnoreCase)
        { "to", "name", "when", "requires", "onStuck", "join", "merge", "maxRevisits", "approve" };

    private static Edge ReadEdge(JsonElement el, string at)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new TeamConfigException($"{at}: an edge is a map, e.g. '- to: Tester'.");

        var p = el.EnumerateObject().ToDictionary(x => x.Name, x => x.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var key in p.Keys)
            if (!EdgeFields.Contains(key))
                throw new TeamConfigException($"{at}: unknown field '{key}'. Edges take {string.Join(", ", EdgeFields)}.");

        string? Str(string k) => p.TryGetValue(k, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : throw new TeamConfigException($"{at}.{k}: expected text.")
            : null;

        if (!p.TryGetValue("To", out var toEl))
            throw new TeamConfigException($"{at}: to is required; name an agent, a list of agents, or 'end'.");
        var to = toEl.ValueKind switch
        {
            JsonValueKind.String => [toEl.GetString()!],
            JsonValueKind.Array  => toEl.EnumerateArray().Select(x => x.GetString() ?? "").ToList(),
            _ => throw new TeamConfigException($"{at}.to: expected an agent name or a list of agent names."),
        };
        if (to.Count == 0 || to.Any(string.IsNullOrWhiteSpace))
            throw new TeamConfigException($"{at}.to: agent names can't be empty.");

        var requires = p.TryGetValue("Requires", out var reqEl)
            ? reqEl.ValueKind switch
            {
                JsonValueKind.Array => reqEl.EnumerateArray().ToList(),
                JsonValueKind.Null  => [],
                _                   => [reqEl],
            }
            : [];

        var merge = p.TryGetValue("Merge", out var mergeEl) ? TeamConfigLoader.Deserialize<MergeConfig>(mergeEl, $"{at}.merge") : null;
        var maxRevisits = p.TryGetValue("MaxRevisits", out var mr) && mr.ValueKind == JsonValueKind.Number ? mr.GetInt32() : 0;
        var approve = p.TryGetValue("Approve", out var ap) && ap.ValueKind switch
        {
            JsonValueKind.True  => true,
            JsonValueKind.False => false,
            _ => throw new TeamConfigException($"{at}.approve: expected true or false."),
        };

        return new(to, Str("Name"), Str("When"), requires, Str("OnStuck"), Str("Join"), merge, maxRevisits, approve);
    }

    private static AgentConfig CompileAgent(string name, TeamAgentConfig agent, TeamConfig team, CompiledFlow flow)
    {
        var at = $"agents.{name}";

        foreach (var art in agent.Reads.Concat(agent.Writes))
            if (!team.Artifacts.ContainsKey(art))
                throw new TeamConfigException($"{at}: '{art}' in reads/writes isn't declared under artifacts.");

        if (agent.RemoteAgent is not null && (agent.Model is not null || agent.Tools.Count > 0 || agent.Advanced is not null || agent.Sees is not null))
            throw new TeamConfigException($"{at}: a remoteAgent runs on its own server, so model, tools, sees and advanced don't apply; remove them.");

        if (agent.RemoteAgent is null && string.IsNullOrWhiteSpace(agent.Instructions))
            throw new TeamConfigException($"{at}.instructions: required; say what this agent does and how.");

        var magentic = flow.Selection.Type == "magentic";
        if (magentic && agent.Sees?.Equals("handoff", StringComparison.OrdinalIgnoreCase) == true)
            throw new TeamConfigException($"{at}.sees: the magentic pattern needs every agent to see the conversation; remove sees or use conversation.");
        var isolation = (agent.Sees ?? (magentic ? "conversation" : "handoff")).ToLowerInvariant() switch
        {
            "handoff"      => AgentIsolation.Fresh,
            "conversation" => AgentIsolation.Shared,
            "fork"         => AgentIsolation.Fork,
            var other      => throw new TeamConfigException($"{at}.sees: '{other}' isn't valid. Use handoff, conversation or fork."),
        };

        var model = agent.Model switch
        {
            null => new ModelConfig(),
            { ValueKind: JsonValueKind.String } alias => new ModelConfig { ModelId = alias.GetString()! },
            { } obj => TeamConfigLoader.Deserialize<ModelConfig>(obj, $"{at}.model"),
        };

        var baseAgent = new AgentConfig();
        if (agent.Advanced is { ValueKind: JsonValueKind.Object } adv)
        {
            foreach (var p in adv.EnumerateObject())
                if (!AdvancedAgent.Contains(p.Name))
                    throw new TeamConfigException(
                        $"{at}.advanced: '{p.Name}' isn't an advanced agent field" +
                        (IsOwnedAgentField(p.Name) ? $"; set it on the agent itself." : $". Fields: {string.Join(", ", AdvancedAgent)}."));
            baseAgent = TeamConfigLoader.Deserialize<AgentConfig>(adv, $"{at}.advanced");
        }

        var tools = new List<string>(agent.Tools);
        var routes = flow.Routes.TryGetValue(name, out var r) ? r : null;
        if (routes is { Count: > 0 } && agent.RemoteAgent is null && !tools.Contains(Infrastructure.Plugins.HandoffPlugin.PluginName, StringComparer.OrdinalIgnoreCase))
            tools.Add(Infrastructure.Plugins.HandoffPlugin.PluginName);

        var context = agent.Reads
            .Select(a => new ContextSource { Source = $"file:{team.Artifacts[a].Path}", Label = $"{a} ({team.Artifacts[a].Path})" })
            .Concat(baseAgent.Context ?? [])
            .ToList();

        // A Fresh agent re-invoked after a failed gate needs its own recent work to fix it.
        if (isolation == AgentIsolation.Fresh && flow.FlowAgents.Contains(name)
            && !context.Any(c => c.Source.StartsWith("own_history", StringComparison.OrdinalIgnoreCase)))
            context.Insert(0, new ContextSource { Source = "own_history:4" });

        return baseAgent with
        {
            Name          = name,
            Description   = agent.Role,
            Instructions  = TeamPrompt.Build(agent, team, routes),
            Model         = model,
            Plugins       = tools,
            Isolation     = isolation,
            Context       = context.Count > 0 ? context : null,
            RemoteAgent   = agent.RemoteAgent,
            HandoffRoutes = routes?.Select(x => x.Route).ToList() ?? [],
        };
    }

    private static bool IsOwnedAgentField(string field) =>
        OwnedAgentFields.Contains(field);

    private static readonly HashSet<string> OwnedAgentFields = new(StringComparer.OrdinalIgnoreCase)
        { "name", "instructions", "description", "model", "plugins", "isolation", "remoteAgent", "agentFile" };

    private static OrchestrationConfig ReadAdvanced(JsonElement? advanced)
    {
        if (advanced is not { ValueKind: JsonValueKind.Object } adv) return new OrchestrationConfig();

        foreach (var p in adv.EnumerateObject())
            if (!AdvancedTopLevel.Contains(p.Name))
                throw new TeamConfigException(
                    $"Advanced: '{p.Name}' isn't an advanced field. Fields: {string.Join(", ", AdvancedTopLevel.Order())}.");

        return TeamConfigLoader.Deserialize<OrchestrationConfig>(adv, "Advanced");
    }
}
