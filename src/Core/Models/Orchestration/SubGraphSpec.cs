namespace fuseraft.Core.Models.Orchestration;

/// <summary>
/// Discriminated spec for a node in <see cref="GraphConfig.SubGraphs"/>.
/// Exactly one of <see cref="Graph"/>, <see cref="MapReduce"/>, or
/// <see cref="ScatterGather"/> must be set.
///
/// <para>
/// <b>Graph sub-graph</b> — runs a nested <c>GraphOrchestrator</c>:
/// <code>
/// SubGraphs:
///   research_team:
///     Graph:
///       EntryNode: gatherer
///       Nodes:
///         - Id: gatherer
///           Agent: DataGatherer
///         - Id: analyst
///           Agent: Analyst
///           Terminal: true
///       Edges:
///         - From: gatherer
///           To: analyst
///           Keyword: "DATA READY"
/// </code>
/// </para>
///
/// <para>
/// <b>Map-reduce sub-graph</b> — runs a nested <c>MapReduceOrchestrator</c>:
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
/// </para>
///
/// <para>
/// <b>Scatter-gather sub-graph</b> — runs a nested <c>ScatterGatherOrchestrator</c>:
/// <code>
/// SubGraphs:
///   multi_expert_review:
///     ScatterGather:
///       Participants:
///         - LegalReviewer
///         - TechnicalReviewer
///         - BusinessReviewer
///       Synthesizer: LeadReviewer
/// </code>
/// </para>
/// </summary>
public record SubGraphSpec
{
    /// <summary>
    /// Nested graph configuration. Set to run a <c>GraphOrchestrator</c> as the sub-graph.
    /// Mutually exclusive with <see cref="MapReduce"/> and <see cref="ScatterGather"/>.
    /// </summary>
    public GraphConfig? Graph { get; init; }

    /// <summary>
    /// Map-reduce configuration. Set to run a <c>MapReduceOrchestrator</c> as the sub-graph.
    /// Mutually exclusive with <see cref="Graph"/> and <see cref="ScatterGather"/>.
    /// </summary>
    public MapReduceConfig? MapReduce { get; init; }

    /// <summary>
    /// Scatter-gather configuration. Set to run a <c>ScatterGatherOrchestrator</c> as the sub-graph.
    /// Mutually exclusive with <see cref="Graph"/> and <see cref="MapReduce"/>.
    /// </summary>
    public ScatterGatherConfig? ScatterGather { get; init; }

    private int SetCount => (Graph is null ? 0 : 1) + (MapReduce is null ? 0 : 1) + (ScatterGather is null ? 0 : 1);

    internal bool IsValid         => SetCount == 1;
    internal bool IsGraph         => Graph         is not null;
    internal bool IsMapReduce     => MapReduce     is not null;
    internal bool IsScatterGather => ScatterGather is not null;
}
