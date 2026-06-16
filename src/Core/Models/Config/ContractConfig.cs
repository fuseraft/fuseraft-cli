namespace fuseraft.Core.Models.Config;

/// <summary>
/// A named, composable evidence contract that defines what must be true in the world
/// before a state transition is allowed.
///
/// <para>
/// Contracts are declared once under <c>Orchestration.Contracts</c> and referenced by
/// name from route <c>Contracts</c> lists. This decouples <em>what must be proven</em>
/// from <em>where it is enforced</em>, making contracts reusable across multiple routes
/// and pipeline configurations.
/// </para>
///
/// Example YAML:
/// <code>
/// Contracts:
///   - Name: ImplementationComplete
///     Requires:
///       - Type: FilesWritten
///         Source: brief.json
///         Field: files_to_change
///       - Type: CommandSucceeded
///         Pattern: "build|compile|test"
///
///   - Name: BriefExists
///     Requires:
///       - Type: FileExists
///         Path: .fuseraft/brief.json
///
/// Routes:
///   - Keyword: "HANDOFF TO DEVELOPER"
///     Agent: Developer
///     Contracts: [BriefExists]
/// </code>
/// </summary>
public record ContractConfig
{
    /// <summary>
    /// Unique name used to reference this contract from route <c>Contracts</c> lists.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// All predicates that must evaluate to true for the contract to be satisfied (AND
    /// semantics). An empty list is always satisfied.
    /// </summary>
    public List<ContractPredicate> Requires { get; init; } = [];
}

/// <summary>
/// A single observable predicate that must hold for its enclosing contract to be satisfied.
///
/// <para>
/// Set <see cref="Type"/> to one of the built-in predicate types and fill the
/// corresponding fields. Unrecognised types are treated as always-failing with an
/// informative error message.
/// </para>
///
/// <list type="table">
///   <listheader><term>Type</term><description>What it checks</description></listheader>
///   <item>
///     <term><c>FilesWritten</c></term>
///     <description>
///       Every path listed in the JSON array at <see cref="Field"/> inside the file at
///       <see cref="Source"/> must appear in the current-session change log as a
///       successfully written file. Use this to enforce that the Developer wrote every
///       file listed in the Planner's brief.
///     </description>
///   </item>
///   <item>
///     <term><c>CommandSucceeded</c></term>
///     <description>
///       At least one shell command whose text matches the pattern must have succeeded
///       in the current session's change log. The pattern is either the literal
///       pipe-separated string in <see cref="Pattern"/>, or a value read at runtime
///       from the JSON field <see cref="PatternField"/> inside <see cref="PatternSource"/>
///       (defaults to the brief). Use <c>PatternField</c> to make this predicate
///       language-agnostic: the Planner writes the project-specific command into the
///       brief and the contract reads it from there.
///     </description>
///   </item>
///   <item>
///     <term><c>FileExists</c></term>
///     <description>
///       The file at <see cref="Path"/> must exist on disk right now.
///     </description>
///   </item>
///   <item>
///     <term><c>TestReport</c></term>
///     <description>
///       The test report at the configured <c>Validation.TestReportPath</c> must satisfy
///       the structural constraints expressed by <see cref="NoFailures"/> and
///       <see cref="HasAssertions"/>.
///     </description>
///   </item>
/// </list>
/// </summary>
public record ContractPredicate
{
    /// <summary>
    /// Predicate type. One of: <c>FilesWritten</c>, <c>ChecklistComplete</c>,
    /// <c>CommandSucceeded</c>, <c>FileExists</c>, <c>TestReport</c>, <c>RelatedTestsPass</c>.
    /// <para>
    /// <c>RelatedTestsPass</c> runs incremental test selection scoped to the current
    /// session's changed files using <c>TestSelector.FindRelatedCommand</c>, then
    /// executes the discovered tests via <c>TestSelector.FullSuiteCommand</c>.
    /// Requires both <c>TestSelector</c> fields to be set at the orchestration level.
    /// No additional predicate fields are needed beyond <c>Type</c>.
    /// </para>
    /// </summary>
    public string Type { get; init; } = string.Empty;

    // FilesWritten fields

    /// <summary>
    /// Path to a JSON file whose <see cref="Field"/> array lists the expected file paths.
    /// Relative to the working directory. Example: <c>".fuseraft/brief.json"</c>.
    /// Required when <see cref="Type"/> is <c>FilesWritten</c>.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Name of the JSON array field inside <see cref="Source"/> that lists expected paths.
    /// Example: <c>"files_to_change"</c>.
    /// Required when <see cref="Type"/> is <c>FilesWritten</c>.
    /// </summary>
    public string? Field { get; init; }

    // CommandSucceeded fields

    /// <summary>
    /// Pipe-separated substrings (case-insensitive). At least one recorded shell command
    /// in the current session must match at least one substring and have succeeded.
    /// Example: <c>"go build|go test"</c>.
    /// Mutually exclusive with <see cref="PatternField"/> — use one or the other.
    /// </summary>
    public string? Pattern { get; init; }

    /// <summary>
    /// JSON field name inside <see cref="PatternSource"/> whose string value is used as the
    /// match pattern at evaluation time. Use this instead of a hardcoded <see cref="Pattern"/>
    /// when the build/verify command varies by project (e.g. <c>"verify_command"</c> in
    /// the Planner's brief lets the model specify the correct invocation for any runtime).
    /// Required together with at most <see cref="PatternSource"/> when <see cref="Pattern"/>
    /// is omitted.
    /// </summary>
    public string? PatternField { get; init; }

    /// <summary>
    /// Path to the JSON file that contains <see cref="PatternField"/>.
    /// Defaults to the brief path configured in <c>Validation.BriefPath</c>
    /// (typically <c>.fuseraft/brief.json</c>) when omitted.
    /// </summary>
    public string? PatternSource { get; init; }

    // FileExists / general path

    /// <summary>
    /// File path to check for existence. Relative to working directory.
    /// Required when <see cref="Type"/> is <c>FileExists</c>.
    /// </summary>
    public string? Path { get; init; }

    // TestReport fields

    /// <summary>
    /// When <c>true</c>, the test report must contain no FAIL entries.
    /// Used when <see cref="Type"/> is <c>TestReport</c>.
    /// </summary>
    public bool? NoFailures { get; init; }

    /// <summary>
    /// When <c>true</c>, the test report must reference at least one real shell command
    /// that was executed in the current session (anti-hallucination guard).
    /// Used when <see cref="Type"/> is <c>TestReport</c>.
    /// </summary>
    public bool? HasAssertions { get; init; }
}
