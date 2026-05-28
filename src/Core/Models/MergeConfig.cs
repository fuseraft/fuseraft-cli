namespace fuseraft.Core.Models;

/// <summary>
/// Controls how a parallel fan-out merges its branch outputs before transitioning
/// to the join state.
/// </summary>
public record MergeConfig
{
    /// <summary>
    /// How branch outputs are combined. Defaults to <see cref="MergeStrategy.Union"/>.
    /// </summary>
    public MergeStrategy Strategy { get; init; } = MergeStrategy.Union;

    /// <summary>
    /// Agent name used when <see cref="Strategy"/> is
    /// <see cref="MergeStrategy.Ranked"/> or <see cref="MergeStrategy.SemanticDiff"/>.
    /// The agent receives all branch outputs and returns the winning / resolved result.
    /// Ignored for other strategies.
    /// </summary>
    public string? Agent { get; init; }

    /// <summary>
    /// Fallback resolution pipeline tried in order when the primary strategy cannot
    /// reach a decision (e.g. a consensus vote ties). Values must be valid
    /// <see cref="MergeStrategy"/> names (case-insensitive).
    /// </summary>
    public List<string>? ConflictResolution { get; init; }
}

/// <summary>Strategies for combining parallel branch outputs into a single merged result.</summary>
public enum MergeStrategy
{
    /// <summary>Concatenate all branch outputs in declaration order.</summary>
    Union,

    /// <summary>Require all branches to agree before passing the merged result forward.</summary>
    Consensus,

    /// <summary>Use majority agreement among branches to select the result.</summary>
    Vote,

    /// <summary>
    /// Delegate to a scoring agent (named in <see cref="MergeConfig.Agent"/>) that
    /// picks the best branch output.
    /// </summary>
    Ranked,

    /// <summary>
    /// Use an LLM agent (named in <see cref="MergeConfig.Agent"/>) to resolve
    /// semantic conflicts between branch outputs.
    /// </summary>
    SemanticDiff,

    /// <summary>Select the branch whose produced artifact passes a runtime benchmark.</summary>
    Benchmark,
}
