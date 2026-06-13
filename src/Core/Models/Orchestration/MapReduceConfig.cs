namespace fuseraft.Core.Models.Orchestration;

/// <summary>
/// Configuration for the map-reduce orchestration mode (Selection.Type: "mapreduce").
///
/// <para>
/// Implements a three-phase execution pipeline:
/// <list type="number">
///   <item><b>Split</b> — <see cref="Splitter"/> produces a JSON array of work items.</item>
///   <item><b>Map</b> — <see cref="Mapper"/> is invoked in parallel for each item (up to
///       <see cref="MaxConcurrency"/> concurrent calls).</item>
///   <item><b>Reduce</b> — <see cref="Reducer"/> synthesises all mapper outputs into a final answer.</item>
/// </list>
/// </para>
///
/// Example YAML:
/// <code>
/// Selection:
///   Type: mapreduce
///   MapReduce:
///     Splitter: Planner        # emits { "items": ["task1", "task2", ...] }
///     Mapper: Developer        # invoked once per item, in parallel
///     Reducer: Synthesizer     # aggregates all Developer outputs
///     ItemsJsonPath: items     # JSON field that holds the array
///     MaxConcurrency: 4        # cap parallel mapper calls; 0 = unlimited
/// </code>
/// </summary>
public record MapReduceConfig
{
    /// <summary>
    /// Name of the agent that decomposes the task into a JSON array of work items.
    /// The agent must emit a JSON object (anywhere in its response) that contains an
    /// array at <see cref="ItemsJsonPath"/>. Must match a name in
    /// <c>Orchestration.Agents</c>.
    /// </summary>
    public string Splitter { get; init; } = string.Empty;

    /// <summary>
    /// Name of the agent invoked once per work item, in parallel.
    /// Each invocation receives the original task plus a system message identifying
    /// the specific item to process. Must match a name in <c>Orchestration.Agents</c>.
    /// </summary>
    public string Mapper { get; init; } = string.Empty;

    /// <summary>
    /// Name of the agent that synthesises all mapper outputs into a final answer.
    /// Receives the original task history plus all mapper responses before being invoked.
    /// Must match a name in <c>Orchestration.Agents</c>.
    /// </summary>
    public string Reducer { get; init; } = string.Empty;

    /// <summary>
    /// Dot-separated JSON path used to locate the items array in the splitter's response.
    /// Single-level field: <c>"items"</c>. Nested field: <c>"plan.tasks"</c>.
    /// Defaults to <c>"items"</c>.
    /// </summary>
    public string ItemsJsonPath { get; init; } = "items";

    /// <summary>
    /// Maximum number of mapper calls to run concurrently. 0 means all items are
    /// dispatched simultaneously (unbounded parallelism). Defaults to 0.
    /// </summary>
    public int MaxConcurrency { get; init; } = 0;

    /// <summary>
    /// Maximum consecutive retries when the splitter does not emit parseable JSON
    /// containing <see cref="ItemsJsonPath"/>. Defaults to 3.
    /// </summary>
    public int MaxSplitterRetries { get; init; } = 3;
}
