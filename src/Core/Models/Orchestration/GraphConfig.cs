namespace fuseraft.Core.Models.Orchestration;

/// <summary>
/// Declarative directed-graph configuration for the <c>graph</c> selection type.
///
/// <para>
/// Agents are bound to named <see cref="Nodes"/>. Directed <see cref="Edges"/> define
/// how control flows between nodes after each agent turn. Edges carry optional keyword
/// conditions and routing validators — evaluated against the emitting agent's output before
/// the edge fires. Nodes marked <c>Terminal</c> end the session after the agent executes once.
/// </para>
///
/// <para>
/// Forward edges (advancing the graph's topological order) are wired into a MAF
/// <see cref="Microsoft.Agents.AI.Workflows.WorkflowBuilder"/> phase. Back-edges (returning
/// to earlier nodes) terminate the current MAF phase and restart the outer loop from the
/// target node — enabling cycles without violating the MAF DAG constraint per phase. This lets the graph
/// contain cycles without violating the MAF DAG constraint per phase.
/// </para>
///
/// Example YAML (linear pipeline with back-edges):
/// <code>
/// Selection:
///   Type: graph
///   Graph:
///     EntryNode: planner
///     MaxRetries: 4
///     Nodes:
///       - Id: planner
///         Agent: Planner
///       - Id: developer
///         Agent: Developer
///       - Id: tester
///         Agent: Tester
///       - Id: reviewer
///         Agent: Reviewer
///         Terminal: true
///     Edges:
///       - From: planner
///         To: developer
///         Keyword: "HANDOFF TO DEVELOPER"
///         Validators: [RequireBrief]
///       - From: developer
///         To: tester
///         Keyword: "HANDOFF TO TESTER"
///         Validators: [RequireWriteFile]
///       - From: tester
///         To: reviewer
///         Keyword: "HANDOFF TO REVIEWER"
///         Validators: [TestReportValid]
///       - From: tester
///         To: developer
///         Keyword: "BUGS FOUND"
///       - From: reviewer
///         To: developer
///         Keyword: "REVISION REQUIRED"
/// </code>
///
/// Example YAML (parallel fan-out/fan-in):
/// <code>
/// Selection:
///   Type: graph
///   Graph:
///     EntryNode: coordinator
///     Nodes:
///       - Id: coordinator
///         Agent: Coordinator
///       - Id: analyzer_a
///         Agent: AnalyzerA
///         Parallel: true
///       - Id: analyzer_b
///         Agent: AnalyzerB
///         Parallel: true
///       - Id: synthesizer
///         Agent: Synthesizer
///         Terminal: true
///     Edges:
///       - From: coordinator
///         To: analyzer_a
///         Keyword: "BEGIN PARALLEL ANALYSIS"
///       - From: coordinator
///         To: analyzer_b
///         Keyword: "BEGIN PARALLEL ANALYSIS"
///       - From: analyzer_a
///         To: synthesizer
///         Keyword: "ANALYSIS COMPLETE"
///       - From: analyzer_b
///         To: synthesizer
///         Keyword: "ANALYSIS COMPLETE"
/// </code>
/// </summary>
public record GraphConfig
{
    /// <summary>
    /// Node definitions. Each binds an agent role to a named position in the graph.
    /// Node IDs must be unique (case-insensitive).
    /// </summary>
    public List<GraphNodeConfig> Nodes { get; init; } = [];

    /// <summary>
    /// Directed edges. Each edge carries optional keyword and validator gates.
    /// Edges are evaluated in declaration order — the first matching edge fires.
    /// </summary>
    public List<GraphEdgeConfig> Edges { get; init; } = [];

    /// <summary>
    /// ID of the node where execution begins. Must match a value in <see cref="Nodes"/>.
    /// Defaults to the first node when null or empty.
    /// </summary>
    public string? EntryNode { get; init; }

    /// <summary>
    /// Maximum consecutive correction attempts per node before the orchestrator throws
    /// <see cref="fuseraft.Core.Exceptions.ValidatorStuckException"/> and surfaces HITL.
    /// Defaults to 4, matching <c>GraphOrchestrator.DefaultMaxRetries</c>.
    /// </summary>
    public int MaxRetries { get; init; } = 4;

    /// <summary>
    /// Multiplier applied to <see cref="MaxRetries"/> to derive the hard total-turn cap per
    /// node (<c>MaxRetries * MaxTotalTurnsMultiplier</c>) — a backstop against a node that
    /// keeps making some progress (so <see cref="MaxRetries"/>'s consecutive-failure counter
    /// keeps resetting) without ever completing. Shared by <c>GraphOrchestrator</c> and
    /// <c>WorkflowOrchestrator</c>. Defaults to 10.
    /// </summary>
    public int MaxTotalTurnsMultiplier { get; init; } = 10;

    /// <summary>
    /// Named sub-graph specs referenced by nodes via <see cref="GraphNodeConfig.SubGraphId"/>.
    /// Each spec must set exactly one of <c>Graph</c> (nested <c>GraphOrchestrator</c>) or
    /// <c>MapReduce</c> (nested <c>MapReduceOrchestrator</c>). The sub-orchestrator executes
    /// as a black-box step and its terminal output is injected into the parent history for
    /// keyword detection and forward-edge routing. All agents referenced inside sub-graphs
    /// must be declared in the top-level <c>Orchestration.Agents</c> list.
    ///
    /// Graph sub-graph example:
    /// <code>
    /// SubGraphs:
    ///   analysis_team:
    ///     Graph:
    ///       EntryNode: analyst
    ///       Nodes:
    ///         - Id: analyst
    ///           Agent: Analyst
    ///         - Id: reviewer
    ///           Agent: Reviewer
    ///           Terminal: true
    ///       Edges:
    ///         - From: analyst
    ///           To: reviewer
    ///           Keyword: "ANALYSIS COMPLETE"
    /// </code>
    ///
    /// Map-reduce sub-graph example:
    /// <code>
    /// SubGraphs:
    ///   parallel_analysis:
    ///     MapReduce:
    ///       Splitter: TaskSplitter
    ///       Mapper: Analyst
    ///       Reducer: Synthesizer
    ///       ItemsJsonPath: tasks
    ///       MaxConcurrency: 4
    /// </code>
    /// </summary>
    public Dictionary<string, SubGraphSpec>? SubGraphs { get; init; }
}

/// <summary>A single node in the execution graph.</summary>
public record GraphNodeConfig
{
    /// <summary>
    /// Unique identifier for this node within the graph. Used in
    /// <see cref="GraphEdgeConfig.From"/> and <see cref="GraphEdgeConfig.To"/>.
    /// Should be stable, lowercase, and concise — it appears in event log payloads.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Name of the agent responsible for work in this node. Must match a name in
    /// <c>Orchestration.Agents</c>. Multiple nodes may reference the same agent.
    /// Must be empty when <see cref="SubGraphId"/> is set; required otherwise.
    /// </summary>
    public string Agent { get; init; } = string.Empty;

    /// <summary>
    /// When set, this node runs the named <see cref="SubGraphSpec"/> from
    /// <see cref="GraphConfig.SubGraphs"/> as a black-box step instead of invoking a
    /// single agent. <see cref="Agent"/> must be empty when this is set.
    ///
    /// <para>
    /// A <c>SubGraphSpec.Graph</c> entry spawns a nested <c>GraphOrchestrator</c>;
    /// a <c>SubGraphSpec.MapReduce</c> entry spawns a nested <c>MapReduceOrchestrator</c>.
    /// All messages produced by the sub-orchestrator are streamed to the parent session
    /// and its terminal output is injected into the parent's shared history for keyword
    /// detection and forward-edge routing.
    /// </para>
    /// </summary>
    public string? SubGraphId { get; init; }

    /// <summary>
    /// When <c>true</c>, the session terminates after the agent executes once in this
    /// node. Outgoing edges are not evaluated. Defaults to <c>false</c>.
    /// </summary>
    public bool Terminal { get; init; } = false;

    /// <summary>
    /// When <c>true</c>, this node participates in a parallel fan-out group. A source
    /// node fans out to all <c>Parallel</c> nodes that share the same triggering keyword,
    /// running them concurrently with isolated conversation-history snapshots. After all
    /// parallel nodes complete, their outputs are merged into the shared history and
    /// control passes to the merge-target node (the common forward-edge destination
    /// declared on each parallel node). Defaults to <c>false</c>.
    /// </summary>
    public bool Parallel { get; init; } = false;

    /// <summary>
    /// Validators that must all pass before a <see cref="Terminal"/> node ends the session.
    /// Uses the same built-in validator names as edge validators:
    /// <c>RequireShellPass</c>, <c>RequireWriteFile</c>, <c>RequireBrief</c>,
    /// <c>TestReportValid</c>, <c>RequireAllFilesWritten</c>,
    /// <c>RequireReviewJudgement</c>, <c>RequireRelatedTestsPass</c>.
    /// Ignored when <see cref="Terminal"/> is <c>false</c>.
    /// </summary>
    public List<string>? Validators { get; init; }
}

/// <summary>
/// A directed edge between two graph nodes.
/// The edge fires when the source agent's output contains the declared <see cref="Keyword"/>
/// (on its own line, case-insensitive) AND all <see cref="AllValidators"/> pass.
/// </summary>
public record GraphEdgeConfig
{
    /// <summary>ID of the source node. Must match a <see cref="GraphNodeConfig.Id"/>.</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>ID of the destination node. Must match a <see cref="GraphNodeConfig.Id"/>.</summary>
    public string To { get; init; } = string.Empty;

    /// <summary>
    /// Keyword the source agent must emit on its own line (case-insensitive) for this edge
    /// to be eligible to fire. When null or empty the edge fires unconditionally after the
    /// source agent's turn — only safe on single-outgoing-edge nodes; ambiguous when
    /// multiple edges share a source.
    /// </summary>
    public string? Keyword { get; init; }

    /// <summary>
    /// Single routing validator name. Built-in validators match those recognised by
    /// <c>GraphOrchestrator</c>: <c>RequireShellPass</c>, <c>RequireWriteFile</c>,
    /// <c>RequireBrief</c>, <c>TestReportValid</c>, <c>RequireAllFilesWritten</c>,
    /// <c>RequireReviewJudgement</c>, <c>RequireRelatedTestsPass</c>,
    /// <c>BlockOnConsecutiveFail</c> (blocks the forward edge and forces REPLAN REQUIRED
    /// when the same command has failed in the last 3 turns — pair with
    /// <c>RequiredCommandPattern</c> to target a specific build command).
    /// Ignored when <see cref="Validators"/> is non-empty.
    /// </summary>
    public string? Validator { get; init; }

    /// <summary>
    /// Multiple validator names (AND semantics). All must pass before this edge fires.
    /// Takes precedence over the single <see cref="Validator"/> field.
    /// </summary>
    public List<string>? Validators { get; init; }

    /// <summary>
    /// When set alongside <c>Validator = "RequireShellPass"</c>, the passing shell command
    /// must contain at least one of these pipe-separated substrings (case-insensitive).
    /// Example: <c>"dotnet build|dotnet test"</c>.
    /// </summary>
    public string? RequiredCommandPattern { get; init; }

    /// <summary>
    /// When set alongside <c>Validator = "RequireWriteFile"</c>, a successful
    /// <c>shell_run</c> whose command matches at least one of these pipe-separated substrings
    /// is accepted in lieu of <c>write_file</c>. Example: <c>"go mod tidy|go get"</c>.
    /// </summary>
    public string? ShellFallbackPattern { get; init; }

    /// <summary>
    /// Optional list of agent names permitted to trigger this edge. When set, the edge only
    /// fires when the emitting agent is in this list. Null or empty allows any agent.
    /// </summary>
    public List<string>? SourceAgents { get; init; }

    /// <summary>
    /// When <c>true</c>, the operator must explicitly approve (<c>y</c>) before this edge fires.
    /// If rejected, the source agent is re-invoked with a "route blocked" message. Applies to
    /// both forward edges and back-edges.
    /// </summary>
    public bool RequireHumanApproval { get; init; }

    /// <summary>
    /// Optional agent name to invoke for one intervention turn when a routing validator has
    /// failed two or more consecutive times on this edge. The recovery agent receives a
    /// diagnostic message and may fix the blocking issue before control returns to the normal
    /// pipeline. Activates at most once per edge per session.
    /// </summary>
    public string? RecoveryAgent { get; init; }

    /// <summary>Returns the effective validator name list for this edge.</summary>
    internal IReadOnlyList<string> AllValidators =>
        Validators is { Count: > 0 }
            ? (IReadOnlyList<string>)Validators
            : Validator is not null ? [Validator]
            : [];
}
