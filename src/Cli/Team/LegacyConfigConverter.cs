using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Agents;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Team;

/// <summary>Converts a pre-v2 <c>Orchestration:</c> config into a v2 team file.</summary>
internal sealed class LegacyConfigConverter
{
    /// <summary>The v2 YAML plus notes about anything the conversion had to approximate.</summary>
    public sealed record Result(string Yaml, IReadOnlyList<string> Notes);

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly OrchestrationConfig _cfg;
    private readonly LegacyParts _legacy;
    private readonly List<string> _notes = [];
    private readonly Dictionary<string, string> _artifactsByPath = new(StringComparer.Ordinal);
    private readonly JsonObject _artifacts = [];
    private readonly Dictionary<string, List<ContextSource>> _extraContext = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _renames = new(StringComparer.Ordinal);

    private LegacyConfigConverter(OrchestrationConfig cfg, LegacyParts legacy) => (_cfg, _legacy) = (cfg, legacy);

    /// <summary>Converts <paramref name="cfg"/>; <paramref name="header"/> is the original file's leading comment block.</summary>
    public static Result Convert(OrchestrationConfig cfg, LegacyParts legacy, string? header = null)
    {
        var c = new LegacyConfigConverter(cfg, legacy);
        var root = (JsonObject)CamelKeys(c.Build(), parent: null)!;
        var yaml = (string.IsNullOrWhiteSpace(header) ? "" : header.TrimEnd() + "\n\n") + YamlWriter.Write(root);
        return new(yaml, c._notes);
    }

    // Maps whose direct children are names the user chose, and maps whose whole subtree is user data.
    private static readonly HashSet<string> NamedChildren = new(StringComparer.OrdinalIgnoreCase)
        { "Agents", "Models", "Steps", "Artifacts", "ApiProfiles" };
    private static readonly HashSet<string> Opaque = new(StringComparer.OrdinalIgnoreCase)
        { "Shape", "Env", "Headers", "DefaultHeaders", "Capabilities", "ToolResultCharOverrides" };

    // Team files are written in camelCase. Every key the converter emits is a schema field except
    // user-chosen names, and a check named in a requires list is written as its canonical name.
    private static JsonNode? CamelKeys(JsonNode? node, string? parent)
    {
        var inRequires = parent is not null && parent.Equals("Requires", StringComparison.OrdinalIgnoreCase);
        switch (node)
        {
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    var name = parent is not null && NamedChildren.Contains(parent) ? key
                             : inRequires && TeamChecks.IsCheck(key) ? TeamChecks.Canonical(key)
                             : Camel(key);
                    result[name] = Opaque.Contains(key) ? value?.DeepClone() : CamelKeys(value, key);
                }
                return result;
            case JsonArray arr:
                return new JsonArray(arr.Select(item =>
                    inRequires && item is JsonValue v && v.TryGetValue<string>(out var s) && TeamChecks.IsCheck(s)
                        ? JsonValue.Create(TeamChecks.Canonical(s))
                        : CamelKeys(item, parent)).ToArray());
            default:
                return node?.DeepClone();
        }
    }

    private static string Camel(string key) =>
        key == "OAuth" ? "oauth" : key.Length > 0 && char.IsUpper(key[0]) ? char.ToLowerInvariant(key[0]) + key[1..] : key;

    private sealed record Edge(string From, string To, string Signal, string? When, JsonArray Requires,
        string? OnStuck = null, List<string>? Parallel = null, JsonNode? Merge = null, int MaxRevisits = 0, bool Approve = false);

    private JsonObject Build()
    {
        DeclareKnownArtifacts();

        JsonNode flow;
        var type = _legacy.Selection.Type.ToLowerInvariant();
        switch (type)
        {
            case "magentic":
            {
                var m = Diff(_cfg.Selection.Magentic ?? new MagenticManagerConfig(), new MagenticManagerConfig())!.AsObject();
                if (_cfg.Selection.Magentic?.Model is { } model)
                    m["Model"] = ModelNode(model);
                flow = WithPattern("magentic", m);
                break;
            }
            case "adversarial":
                flow = WithPattern("adversarial", Diff(_cfg.Selection.Adversarial ?? new(), new AdversarialConfig())!.AsObject());
                break;
            case "mapreduce":
                flow = WithPattern("mapreduce", Diff(_cfg.Selection.MapReduce ?? new(), new MapReduceConfig())!.AsObject());
                break;
            case "scattergather":
                flow = WithPattern("scattergather", Diff(_cfg.Selection.ScatterGather ?? new(), new ScatterGatherConfig())!.AsObject());
                break;
            default:
                var (start, edges) = type switch
                {
                    "statemachine"              => FromStateMachine(),
                    "keyword"                   => FromKeyword(),
                    "graph" or "workflow"       => FromGraph(),
                    "structured"                => FromStructured(),
                    "sequential" or "roundrobin" => FromRoundRobin(),
                    "llm" => throw new TeamConfigException(
                        "Selection.Type 'llm' has no v2 equivalent. Rewrite it as Steps with handoffs, or use 'Pattern: magentic'."),
                    _ => throw new TeamConfigException($"Selection.Type '{_legacy.Selection.Type}' isn't recognised."),
                };
                flow = StepsNode(start, edges);
                break;
        }

        var root = new JsonObject { ["Team"] = _cfg.Name };
        if (!string.IsNullOrWhiteSpace(_cfg.Description)) root["Description"] = _cfg.Description.Trim();
        if (_cfg.Models.Count > 0)
        {
            var models = new JsonObject();
            foreach (var (alias, model) in _cfg.Models) models[alias] = Diff(model, new ModelConfig()) ?? new JsonObject();
            root["Models"] = models;
        }

        var agents = BuildAgents(type);
        if (_artifacts.Count > 0) root["Artifacts"] = _artifacts;
        root["Agents"] = agents;
        root["Flow"] = flow;

        var maxTurns = _legacy.Termination?.ResolveMaxIterations() ?? 0;
        var limits = new JsonObject();
        if (maxTurns > 0 && maxTurns != 40) limits["MaxTurns"] = maxTurns;
        if (_cfg.MaxTotalTokens is { } tokens) limits["MaxTokens"] = tokens;
        if (limits.Count > 0) root["Limits"] = limits;

        if (Diff(_cfg.Security, new SecurityConfig()) is { } security) root["Security"] = security;
        if (_cfg.McpServers.Count > 0) root["McpServers"] = new JsonArray(_cfg.McpServers.Select(s => Diff(s, new McpServerConfig()) ?? new JsonObject()).ToArray());

        var advanced = BuildAdvanced();
        if (advanced.Count > 0) root["Advanced"] = advanced;
        return root;
    }

    private static JsonObject WithPattern(string pattern, JsonObject body)
    {
        var obj = new JsonObject { ["Pattern"] = pattern };
        foreach (var (k, v) in body.ToList()) { body.Remove(k); obj[k] = v; }
        return obj;
    }

    // ── Flow sources ───────────────────────────────────────────────────────

    private (string Start, List<Edge> Edges) FromStateMachine()
    {
        var sm = _legacy.Selection.StateMachine
            ?? throw new TeamConfigException("Selection.StateMachine is missing.");
        var contracts = (_cfg.Contracts ?? []).ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        var byAgent = sm.States.Where(s => !s.Value.Terminal).GroupBy(s => s.Value.Agent);
        foreach (var g in byAgent.Where(g => g.Count() > 1))
            throw new TeamConfigException(
                $"Agent '{g.Key}' runs in several states ({string.Join(", ", g.Select(s => s.Key))}). " +
                "v2 steps are named after their agent, so split it into separate agents, then migrate again.");

        // A terminal state runs its agent once more; it only collapses into 'end' when that
        // agent is the one handing off (the usual "Done: Agent: Reviewer" shape).
        string AgentOf(string state, string? from = null) =>
            sm.States.TryGetValue(state, out var st)
                ? st.Terminal && (string.IsNullOrEmpty(st.Agent) || st.Agent == from) ? TeamCompiler.EndState : st.Agent
                : throw new TeamConfigException($"Transition target state '{state}' isn't defined.");

        var edges = new List<Edge>();
        foreach (var terminal in sm.States.Values.Where(s => s.Terminal && !string.IsNullOrEmpty(s.Agent)).Select(s => s.Agent).Distinct())
            if (sm.States.Values.Any(s => !s.Terminal && s.Transitions.Any(t => t.To is { } to && sm.States[to].Terminal && sm.States[to].Agent == terminal && s.Agent != terminal)))
                edges.Add(new Edge(terminal, TeamCompiler.EndState, TeamCompiler.EndState, "your work is complete", []));
        foreach (var (stateName, state) in sm.States.Where(s => !s.Value.Terminal))
        {
            foreach (var t in state.Transitions)
            {
                var requires = new JsonArray();
                foreach (var name in t.AllContracts)
                {
                    if (!contracts.TryGetValue(name, out var contract))
                        throw new TeamConfigException($"Transition {stateName} → {t.To} references unknown contract '{name}'.");
                    foreach (var p in contract.Requires) requires.Add(FromPredicate(p));
                }

                var to = AgentOf(t.To, state.Agent);
                if (t.HandoffContext is { Count: > 0 } hc && to != TeamCompiler.EndState)
                    AddContext(to, hc);
                if (t.SourceAgents is { Count: > 0 })
                    _notes.Add($"{stateName} → {t.To}: SourceAgents dropped; only the step's own agent can hand off in v2.");
                if (t.ReviewArtifactPath is { } reviewPath)
                    AddWrites(state.Agent, Artifact(reviewPath));

                edges.Add(new Edge(state.Agent, to, t.Signal ?? to, WhenFromSignal(t.Signal, to), requires,
                    t.RecoveryAgent,
                    t.Parallel && t.Targets is { Count: > 0 } ? t.Targets.Select(x => AgentOf(x)).ToList() : null,
                    t.Merge is null ? null : Diff(t.Merge, new MergeConfig()),
                    t.MaxRevisits));
            }
        }

        if (sm.States.Values.Any(s => s.Terminal))
            DropRedundantTermination();
        return (AgentOf(sm.Initial), edges);
    }

    private (string Start, List<Edge> Edges) FromKeyword()
    {
        var routes = _legacy.Selection.Routes ?? [];
        var agents = _cfg.Agents.Select(a => a.Name).ToList();
        var edges = new List<Edge>();
        foreach (var r in routes)
        {
            var sources = r.SourceAgents is { Count: > 0 } s ? s : agents.Where(a => a != r.Agent).ToList();
            if (r.SourceAgents is not { Count: > 0 })
                _notes.Add($"Route '{r.Keyword}' had no SourceAgents; added it to every other agent ({string.Join(", ", sources)}).");
            foreach (var from in sources)
                edges.Add(new Edge(from, r.Agent, r.Keyword, WhenFromSignal(r.Keyword, r.Agent),
                    ValidatorChecks(r.Validators ?? (r.Validator is { } v ? [v] : []), r.RequiredCommandPattern, r.ShellFallbackPattern, $"route '{r.Keyword}'"),
                    r.RecoveryAgent, Approve: r.RequireHumanApproval));
        }
        edges.AddRange(EndEdgesFromTermination(agents));
        var start = _legacy.Selection.DefaultAgent ?? agents.FirstOrDefault() ?? "";
        return (start, edges);
    }

    private (string Start, List<Edge> Edges) FromGraph()
    {
        var g = _legacy.Selection.Graph ?? throw new TeamConfigException("Selection.Graph is missing.");
        if (g.HasSubGraphs || g.Nodes.Any(n => n.SubGraphId is not null))
            throw new TeamConfigException("Sub-graphs have no v2 equivalent yet; convert this config by hand.");
        // A terminal node that reuses the agent handing off to it is just "finish here".
        bool CollapsesToEnd(LegacyGraphEdge e) =>
            g.Nodes.First(n => n.Id == e.To) is { Terminal: true } t && t.Agent == g.Nodes.First(n => n.Id == e.From).Agent;
        var collapsed = g.Nodes.Where(n => n.Terminal && g.Edges.Where(e => e.To == n.Id).All(CollapsesToEnd) && g.Edges.Any(e => e.To == n.Id))
            .Select(n => n.Id).ToHashSet();
        foreach (var dup in g.Nodes.Where(n => !collapsed.Contains(n.Id)).GroupBy(n => n.Agent).Where(x => x.Count() > 1))
            throw new TeamConfigException($"Agent '{dup.Key}' backs several graph nodes; split it into separate agents, then migrate again.");

        var agentOf = g.Nodes.ToDictionary(n => n.Id, n => n.Agent);
        var edges = new List<Edge>();
        foreach (var e in g.Edges)
        {
            var to = CollapsesToEnd(e) ? TeamCompiler.EndState : agentOf[e.To];
            var checks = ValidatorChecks(e.Validators ?? (e.Validator is { } v ? [v] : []), e.RequiredCommandPattern, e.ShellFallbackPattern, $"edge {e.From} → {e.To}");
            if (CollapsesToEnd(e))
                foreach (var c in ValidatorChecks(g.Nodes.First(n => n.Id == e.To).Validators ?? [], null, null, $"node {e.To}")) checks.Add(c);
            edges.Add(new Edge(agentOf[e.From], to, e.Keyword ?? to, WhenFromSignal(e.Keyword, to), checks, e.RecoveryAgent, Approve: e.RequireHumanApproval));
        }
        foreach (var node in g.Nodes.Where(n => n.Terminal && !collapsed.Contains(n.Id)))
            edges.Add(new Edge(node.Agent, TeamCompiler.EndState, TeamCompiler.EndState, "your work is complete",
                ValidatorChecks(node.Validators ?? [], null, null, $"node {node.Id}")));
        if (g.MaxRetries is > 0)
            _notes.Add("Graph.MaxRetries dropped; gate retries follow Advanced.FailureHandling.");
        return (agentOf[g.EntryNode ?? throw new TeamConfigException("Selection.Graph.EntryNode is missing.")], edges);
    }

    private (string Start, List<Edge> Edges) FromStructured()
    {
        var edges = new List<Edge>();
        var agents = _cfg.Agents.Select(a => a.Name).ToList();
        foreach (var r in _legacy.Selection.StructuredRoutes ?? [])
        {
            var c = r.Condition;
            var when = c.Is is { } isVal ? $"{c.Field} is {isVal}"
                : c.IsNot is { } notVal ? $"{c.Field} is not {notVal}"
                : c.Contains is { } has ? $"{c.Field} contains {has}"
                : $"you have produced {c.Field}";
            foreach (var from in r.SourceAgents is { Count: > 0 } s ? s : agents.Where(a => a != r.Agent))
                edges.Add(new Edge(from, r.Agent, r.Agent, when, []));
        }
        _notes.Add("Structured JSON routing became handoffs: agents now call handoff instead of emitting routing JSON. Review their instructions.");
        edges.AddRange(EndEdgesFromTermination(agents));
        return (_legacy.Selection.DefaultAgent ?? agents.First(), edges);
    }

    private (string Start, List<Edge> Edges) FromRoundRobin()
    {
        var agents = _cfg.Agents.Select(a => a.Name).ToList();
        var edges = new List<Edge>();
        for (var i = 0; i < agents.Count; i++)
        {
            var next = agents[(i + 1) % agents.Count];
            if (agents.Count > 1)
                edges.Add(new Edge(agents[i], next, next, null, []));
        }
        edges.AddRange(EndEdgesFromTermination(agents));
        _notes.Add("Turn-taking became explicit handoffs: each agent now calls handoff to pass to the next one or to finish.");
        return (agents.First(), edges);
    }

    private IEnumerable<Edge> EndEdgesFromTermination(List<string> agents)
    {
        var rules = new List<LegacyTermination>();
        void Collect(LegacyTermination? t)
        {
            if (t is null) return;
            if (t.Type.Equals("regex", StringComparison.OrdinalIgnoreCase) || t.Type.Equals("structured", StringComparison.OrdinalIgnoreCase)) rules.Add(t);
            foreach (var s in t.Strategies ?? []) Collect(s);
        }
        Collect(_legacy.Termination);

        var enders = rules.SelectMany(r => r.AgentNames is { Length: > 0 } names ? names : [.. agents]).Distinct().ToList();
        if (enders.Count == 0)
        {
            enders = [agents.Last()];
            _notes.Add($"No termination rule named an agent; '{enders[0]}' can now finish the run.");
        }
        var pattern = rules.FirstOrDefault()?.Pattern;
        if (pattern is not null)
            _notes.Add($"The run used to end when an agent wrote '{pattern}'; it now ends when an agent hands off to 'end'. Update instructions that tell agents to write it.");
        return enders.Select(a => new Edge(a, TeamCompiler.EndState, TeamCompiler.EndState, "the work is complete", []));
    }

    private void DropRedundantTermination()
    {
        if (_legacy.Termination?.Strategies is { Count: > 0 } || _legacy.Termination?.Pattern is not null)
            _notes.Add("Termination rules dropped; the run now ends on the edge to 'end' or at Limits.MaxTurns.");
    }

    private static string? WhenFromSignal(string? signal, string to)
    {
        if (string.IsNullOrWhiteSpace(signal)) return null;
        if (Regex.IsMatch(signal, @"^(HANDOFF|HAND OFF)\s+TO\s+", RegexOptions.IgnoreCase)) return null;
        if (signal.Equals(to, StringComparison.OrdinalIgnoreCase)) return null;
        var words = signal.Replace('_', ' ').Trim().ToLowerInvariant();
        return $"{words}";
    }

    private JsonObject StepsNode(string start, List<Edge> edges)
    {
        // Walk the flow from Start so steps read in the order they run.
        var order = new List<string>();
        var queue = new Queue<string>([start]);
        while (queue.Count > 0)
        {
            var agent = queue.Dequeue();
            if (agent == TeamCompiler.EndState || order.Contains(agent)) continue;
            order.Add(agent);
            foreach (var e in edges.Where(e => e.From == agent))
            {
                foreach (var b in e.Parallel ?? []) queue.Enqueue(b);
                queue.Enqueue(e.To);
            }
        }
        order.AddRange(edges.Select(e => e.From).Distinct().Where(a => !order.Contains(a)));

        var steps = new JsonObject();
        foreach (var group in edges.GroupBy(e => e.From).OrderBy(g => order.IndexOf(g.Key)))
        {
            var list = new JsonArray();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in group)
            {
                var defaultName = e.Parallel is { } p ? string.Join("+", p) : e.To;
                var name = e.Signal != defaultName && WrittenAsText(e.From, e.Signal) && used.Add(e.Signal) ? e.Signal
                    : used.Add(defaultName) ? null
                    : SignalName(e.Signal, used);
                RecordRename(e.From, e.Signal, name ?? defaultName);

                var edge = new JsonObject();
                if (e.Parallel is { } branches)
                {
                    edge["To"] = new JsonArray(branches.Select(b => (JsonNode)b).ToArray());
                    edge["Join"] = e.To;
                    if (e.Merge is { } merge) edge["Merge"] = merge;
                }
                else edge["To"] = e.To;
                if (name is not null) edge["Name"] = name;
                if (e.When is not null && name != e.Signal) edge["When"] = e.When;
                var requires = DropCoveredArtifacts(e.Requires);
                if (requires.Count > 0) edge["Requires"] = requires;
                if (e.OnStuck is not null) edge["OnStuck"] = e.OnStuck;
                if (e.MaxRevisits > 0) edge["MaxRevisits"] = e.MaxRevisits;
                if (e.Approve) edge["Approve"] = true;
                list.Add(edge);
            }
            steps[group.Key] = list;
        }

        foreach (var target in edges.Where(e => e.To != TeamCompiler.EndState).Select(e => e.To).Distinct().Where(t => !steps.ContainsKey(t)))
        {
            steps[target] = new JsonArray(new JsonObject { ["To"] = TeamCompiler.EndState });
            _notes.Add($"'{target}' had no outgoing route; it now finishes the run. Check that's what you want.");
        }
        if (!edges.Any(e => e.To == TeamCompiler.EndState))
        {
            var last = edges.LastOrDefault()?.From ?? start;
            ((JsonArray)steps[last]!).Add(new JsonObject { ["To"] = TeamCompiler.EndState });
            _notes.Add($"No route finished the run; '{last}' can now hand off to 'end'.");
        }

        return new JsonObject { ["Start"] = start, ["Steps"] = steps };
    }

    // True when the agent's instructions tell it to write the signal as text rather than pass it to handoff.
    private bool WrittenAsText(string agent, string signal)
    {
        var text = _cfg.Agents.FirstOrDefault(a => a.Name == agent)?.Instructions ?? "";
        var raw = Regex.Matches(text, Regex.Escape(signal)).Count;
        var quoted = Regex.Matches(text, Regex.Escape($"\"{signal}\"")).Count;
        return raw > quoted;
    }

    // A bare artifact item is redundant beside a TestReport check on the same artifact.
    private static JsonArray DropCoveredArtifacts(JsonArray requires)
    {
        var covered = requires.OfType<JsonObject>()
            .Where(o => o.ContainsKey("TestReport"))
            .Select(o => o["TestReport"]!.GetValue<string>())
            .ToHashSet();
        var result = new JsonArray();
        foreach (var item in requires)
            if (!(item is JsonValue v && covered.Contains(v.GetValue<string>())))
                result.Add(item?.DeepClone());
        return result;
    }

    private static string SignalName(string signal, HashSet<string> used)
    {
        var words = Regex.Split(signal.Trim().ToLowerInvariant(), @"[^a-z0-9]+").Where(w => w.Length > 0);
        var name = string.Concat(words.Select((w, i) => i == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
        if (name.Length == 0) name = "route";
        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++) candidate = name + n;
        return candidate;
    }

    private void RecordRename(string agent, string oldSignal, string newName)
    {
        if (!_renames.TryGetValue(agent, out var map)) _renames[agent] = map = new(StringComparer.Ordinal);
        map.TryAdd(oldSignal, newName);
    }

    // ── Gates ──────────────────────────────────────────────────────────────

    private JsonNode FromPredicate(ContractPredicate p)
    {
        switch (p.Type.ToLowerInvariant())
        {
            case "fileexists":
                return _artifactsByPath.TryGetValue(p.Path ?? "", out var art)
                    ? art
                    : new JsonObject { ["FileExists"] = p.Path };
            case "fileswritten":
            case "checklistcomplete":
                return new JsonObject { [p.Type.ToLowerInvariant() == "fileswritten" ? "FilesWritten" : "ChecklistComplete"] = $"{Artifact(p.Source!)}.{p.Field}" };
            case "commandsucceeded":
                if (p.PatternField is { } field)
                {
                    var obj = new JsonObject { ["CommandSucceeded"] = $"{Artifact(p.PatternSource ?? BriefPath())}.{field}" };
                    if (p.Pattern is { } fb) obj["Fallback"] = fb;
                    return obj;
                }
                return new JsonObject { ["CommandSucceeded"] = p.Pattern };
            case "testreport":
            {
                var obj = new JsonObject { ["TestReport"] = Artifact(p.Path ?? _cfg.Validation?.TestReportPath ?? new ValidationConfig().TestReportPath, "testResults") };
                if (p.NoFailures != true) obj["NoFailures"] = false;
                if (p.HasAssertions != true) obj["HasAssertions"] = false;
                return obj;
            }
            case "relatedtestspass":
                return "RelatedTestsPass";
        }
        throw new TeamConfigException($"Contract predicate type '{p.Type}' isn't recognised.");
    }

    private JsonArray ValidatorChecks(IEnumerable<string> names, string? commandPattern, string? fallback, string where)
    {
        var arr = new JsonArray();
        foreach (var n in names)
        {
            JsonNode node = n switch
            {
                _ when Is(n, LegacyValidatorNames.RequireBrief)            => Artifact(BriefPath(), "brief"),
                _ when Is(n, LegacyValidatorNames.RequireAllFilesWritten)  => new JsonObject { ["FilesWritten"] = $"{Artifact(BriefPath(), "brief")}.files_to_change" },
                _ when Is(n, LegacyValidatorNames.TestReportValid)         => new JsonObject { ["TestReport"] = Artifact(_cfg.Validation?.TestReportPath ?? new ValidationConfig().TestReportPath, "testResults") },
                _ when Is(n, ValidatorNames.RequireReviewJudgement)  => "ReviewJudgement",
                _ when Is(n, ValidatorNames.RequireAcceptanceCriteriaPassed) => new JsonObject { ["AcceptanceCriteriaPassed"] = Artifact(BriefPath(), "brief") },
                _ when Is(n, LegacyValidatorNames.RequireRelatedTestsPass) => "RelatedTestsPass",
                _ when Is(n, ValidatorNames.ArchitectureValidator)   => "Architecture",
                _ when Is(n, ValidatorNames.RequireSessionContextWrite) => "SessionContextWritten",
                _ when Is(n, ValidatorNames.BlockOnConsecutiveFail)  => new JsonObject { ["NoRepeatedShellFailures"] = commandPattern },
                _ when Is(n, ValidatorNames.RequireWriteFile)        => fallback is null ? "AnyFileWritten" : new JsonObject { ["AnyFileWritten"] = true, ["Fallback"] = fallback },
                _ when Is(n, LegacyValidatorNames.RequireShellPass)        => new JsonObject { ["CommandSucceeded"] = commandPattern ?? Note("test", $"{where}: RequireShellPass had no command pattern; it now requires a command matching 'test'.") },
                _ => throw new TeamConfigException($"{where}: validator '{n}' isn't recognised."),
            };
            arr.Add(node);
        }
        return arr;
    }

    private string Note(string value, string note) { _notes.Add(note); return value; }

    private static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private string BriefPath() => _cfg.Validation?.BriefPath ?? new ValidationConfig().BriefPath;

    private void DeclareKnownArtifacts()
    {
        // Brief and test report keep their conventional names so prompts and checks read naturally.
        if (_cfg.Validation?.BriefPath is { } brief) _artifactsByPath.TryAdd(brief, "brief");
        if (_cfg.Validation?.TestReportPath is { } report) _artifactsByPath.TryAdd(report, "testResults");
    }

    // Returns the artifact name for a path, declaring it on first use.
    private string Artifact(string path, string? preferredName = null)
    {
        if (_artifactsByPath.TryGetValue(path, out var known))
        {
            if (!_artifacts.ContainsKey(known)) _artifacts[known] = new JsonObject { ["Path"] = path };
            return known;
        }
        var name = preferredName ?? CamelStem(path);
        // An artifact named like a check would be ambiguous in a requires list.
        if (TeamChecks.IsCheck(name)) name += "File";
        var stem = name;
        for (var i = 2; _artifacts.ContainsKey(name) || _artifactsByPath.ContainsValue(name); i++) name = stem + i;
        _artifactsByPath[path] = name;
        _artifacts[name] = new JsonObject { ["Path"] = path };
        return name;
    }

    private static string CamelStem(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var words = Regex.Split(stem, @"[^A-Za-z0-9]+").Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return "artifact";
        var name = words[0].ToLowerInvariant() + string.Concat(words.Skip(1).Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
        return char.IsDigit(name[0]) ? "a" + name : name;
    }

    // ── Agents ─────────────────────────────────────────────────────────────

    private JsonObject BuildAgents(string selectionType)
    {
        var defaults = new AgentConfig();
        var agents = new JsonObject();
        foreach (var a in _cfg.Agents)
        {
            var node = new JsonObject();
            if (a.Description is { Length: > 0 } role) node["Role"] = role.Trim();

            if (a.RemoteAgent is not null)
            {
                node["RemoteAgent"] = Diff(a.RemoteAgent, new RemoteAgentConfig()) ?? new JsonObject();
                if (!string.IsNullOrWhiteSpace(a.Instructions)) node["Instructions"] = RewriteSignals(a.Name, a.Instructions);
                agents[a.Name] = node;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(a.Model.ModelId) || Diff(a.Model, new ModelConfig()) is not null)
                node["Model"] = ModelNode(a.Model);

            var tools = a.Plugins.Where(p => !p.Equals("Handoff", StringComparison.OrdinalIgnoreCase)).ToList();
            if (tools.Count > 0) node["Tools"] = new JsonArray(tools.Select(t => (JsonNode)t).ToArray());

            var context = (a.Context ?? []).Concat(_extraContext.GetValueOrDefault(a.Name) ?? []).ToList();
            var reads = new List<string>();
            foreach (var src in context.ToList())
                if (src.Source.StartsWith("file:", StringComparison.Ordinal) && _artifactsByPath.ContainsKey(src.Source[5..]))
                {
                    reads.Add(Artifact(src.Source[5..]));
                    context.Remove(src);
                }
            if (reads.Count > 0) node["Reads"] = new JsonArray(reads.Distinct().Select(r => (JsonNode)r).ToArray());
            if (_extraWrites.TryGetValue(a.Name, out var writes))
                node["Writes"] = new JsonArray(writes.Select(w => (JsonNode)w).ToArray());

            var magentic = selectionType == "magentic";
            switch (a.Isolation)
            {
                case AgentIsolation.Shared when !magentic: node["Sees"] = "conversation"; break;
                case AgentIsolation.Fork:                  node["Sees"] = "fork"; break;
            }

            node["Instructions"] = RewriteSignals(a.Name, a.Instructions ?? "");

            var adv = Diff(a with
            {
                Name = defaults.Name, Instructions = defaults.Instructions, Description = defaults.Description,
                Model = defaults.Model, Plugins = defaults.Plugins, Isolation = defaults.Isolation,
                RemoteAgent = null, AgentFile = null,
                Context = context.Count > 0 ? context.Distinct().ToList() : null,
            }, defaults);
            if (_legacy.AgentDependencies.ContainsKey(a.Name))
                _notes.Add($"{a.Name}: Produces/Requires dependency tokens dropped; Flow order and Requires gates cover them.");
            if (adv is JsonObject advObj && advObj.Count > 0) node["Advanced"] = advObj;

            agents[a.Name] = node;
        }
        return agents;
    }

    private string RewriteSignals(string agent, string text)
    {
        if (!_renames.TryGetValue(agent, out var map)) return text.TrimEnd() + "\n";
        foreach (var (oldSignal, newName) in map.OrderByDescending(kv => kv.Key.Length))
            if (!oldSignal.Equals(newName, StringComparison.Ordinal))
                text = text.Replace($"\"{oldSignal}\"", $"\"{newName}\"");
        return text.TrimEnd() + "\n";
    }

    private readonly Dictionary<string, List<string>> _extraWrites = new(StringComparer.Ordinal);

    private void AddWrites(string agent, string artifact)
    {
        if (!_extraWrites.TryGetValue(agent, out var list)) _extraWrites[agent] = list = [];
        if (!list.Contains(artifact)) list.Insert(0, artifact);
    }

    private void AddContext(string agent, IEnumerable<ContextSource> sources)
    {
        if (!_extraContext.TryGetValue(agent, out var list)) _extraContext[agent] = list = [];
        foreach (var s in sources)
            if (!list.Any(x => x.Source == s.Source)) list.Add(s);
    }

    private JsonNode ModelNode(ModelConfig model)
    {
        var diff = Diff(model, new ModelConfig()) as JsonObject ?? new JsonObject();
        return diff.Count == 1 && diff.ContainsKey("ModelId") && _cfg.Models.ContainsKey(model.ModelId)
            ? model.ModelId
            : diff;
    }

    // ── Advanced ───────────────────────────────────────────────────────────

    private JsonObject BuildAdvanced()
    {
        var adv = new JsonObject();
        void Add(string key, object? value, object defaults)
        {
            if (value is not null && Diff(value, defaults) is { } d) adv[key] = d;
        }

        if (!string.IsNullOrWhiteSpace(_cfg.SystemPrompt)) adv["SystemPrompt"] = _cfg.SystemPrompt;
        if (!string.IsNullOrWhiteSpace(_cfg.SystemPromptPath)) adv["SystemPromptPath"] = _cfg.SystemPromptPath;
        if (_cfg.WarnTurnTokens != new OrchestrationConfig().WarnTurnTokens) adv["WarnTurnTokens"] = _cfg.WarnTurnTokens;
        Add("ContextBudget", _cfg.ContextBudget, new ContextBudgetConfig());
        Add("Compaction", _cfg.Compaction, new CompactionConfig());
        Add("Scratchpad", _cfg.Scratchpad, new ScratchpadConfig());
        Add("Chatroom", _cfg.Chatroom, new ChatroomConfig());
        Add("Telemetry", _cfg.Telemetry, new TelemetryConfig());
        if (_cfg.Checkpoint is { } cp && (cp.Mode != "json" || cp.Path is not null && !cp.Path.StartsWith(".fuseraft/checkpoints", StringComparison.Ordinal)))
            Add("Checkpoint", cp, new CheckpointConfig());
        if (_cfg.ApiProfiles.Count > 0) adv["ApiProfiles"] = JsonSerializer.SerializeToNode(_cfg.ApiProfiles, Json);
        Add("Verifier", _cfg.Verifier, new VerifierConfig());
        Add("FailureHandling", _cfg.FailureHandling, new FailureHandlingConfig());
        Add("Brownfield", _cfg.Brownfield, new BrownfieldConfig());
        Add("TestSelector", _cfg.TestSelector, new TestSelectorConfig());
        Add("SkillCuration", _cfg.SkillCuration, new SkillCurationConfig());
        Add("Memory", _cfg.Memory, new MemoryConfig());
        Add("Output", _cfg.Output, new OutputConfig());

        if (_legacy.TestAssertionPatterns is { Count: > 0 })
            _notes.Add("Validation.TestAssertionPatterns dropped; the TestReport check uses the built-in patterns.");
        return adv;
    }

    // ── Diffing ────────────────────────────────────────────────────────────

    // Returns only the fields of value that differ from defaults; null when nothing differs.
    private static JsonNode? Diff(object value, object defaults)
    {
        var v = JsonSerializer.SerializeToNode(value, value.GetType(), Json);
        var d = JsonSerializer.SerializeToNode(defaults, defaults.GetType(), Json);
        return DiffNodes(v, d);
    }

    private static JsonNode? DiffNodes(JsonNode? v, JsonNode? d)
    {
        if (v is JsonObject vo && d is JsonObject dobj)
        {
            var result = new JsonObject();
            foreach (var (key, child) in vo)
            {
                if (child is null) continue;
                var diff = DiffNodes(child, dobj[key]);
                if (diff is not null) result[key] = diff;
            }
            return result.Count > 0 ? result : null;
        }
        if (v is null) return null;
        if (d is not null && JsonNode.DeepEquals(v, d)) return null;
        if (v is JsonArray { Count: 0 } && d is null) return null;
        return Prune(v.DeepClone());
    }

    // Drops nulls and empty collections from a node copied without a default to compare against.
    private static JsonNode? Prune(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (k, child) in obj.ToList())
                {
                    var pruned = Prune(child);
                    if (pruned is null) obj.Remove(k);
                    else if (!ReferenceEquals(pruned, child)) obj[k] = pruned;
                }
                return obj.Count > 0 ? obj : null;
            case JsonArray arr:
                return arr.Count > 0 ? arr : null;
            default:
                return node;
        }
    }
}
