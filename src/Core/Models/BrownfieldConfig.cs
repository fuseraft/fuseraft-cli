using System.Text.Json.Serialization;

namespace fuseraft.Core.Models;

/// <summary>
/// Brownfield-mode settings. When present, fuseraft-cli enables a structured recon phase
/// and a tighter change envelope so agents work safely inside large existing codebases.
/// </summary>
public record BrownfieldConfig
{
    /// <summary>
    /// Files or directories that seed the Archaeologist agent's dependency walk.
    /// Typically the public entry points of the system (e.g. <c>"cmd/server/main.go"</c>,
    /// <c>"src/index.ts"</c>). Relative paths are resolved against the sandbox root.
    /// </summary>
    public List<string> EntryPoints { get; init; } = [];

    /// <summary>
    /// Path where the Archaeologist writes the discovery brief JSON.
    /// Defaults to <c>.fuseraft/brief.brownfield.json</c>.
    /// </summary>
    public string DiscoveryBriefPath { get; init; } = ".fuseraft/brief.brownfield.json";

    /// <summary>
    /// Path where the Archaeologist writes the detected convention profile JSON.
    /// When the file exists at session startup, its contents are injected into every
    /// agent's system prompt so agents follow project conventions without re-deriving them.
    /// Defaults to <c>.fuseraft/conventions.json</c>.
    /// </summary>
    public string ConventionProfilePath { get; init; } = ".fuseraft/conventions.json";

    /// <summary>
    /// When <c>true</c> (default) and <see cref="DiscoveryBriefPath"/> exists,
    /// the <c>in_scope_files</c> list from the discovery brief is merged into
    /// <see cref="SecurityConfig.ChangeEnvelope"/> at startup so only files the
    /// Archaeologist identified as in-scope may be written this session.
    /// </summary>
    public bool SeedEnvelopeFromBrief { get; init; } = true;
}

/// <summary>
/// Project conventions detected by the Archaeologist agent during the recon phase.
/// Written to <see cref="BrownfieldConfig.ConventionProfilePath"/> as JSON; loaded
/// at session startup and injected into every agent's system prompt.
/// </summary>
public record ConventionProfile
{
    /// <summary>Primary language/ecosystem (e.g. <c>"go"</c>, <c>"typescript"</c>).</summary>
    [JsonPropertyName("language")]
    public string? Language { get; init; }

    /// <summary>
    /// Naming conventions observed in the codebase.
    /// Example: <c>"test files match *_test.go"</c>.
    /// </summary>
    [JsonPropertyName("naming_patterns")]
    public List<string> NamingPatterns { get; init; } = [];

    /// <summary>
    /// Error-handling idioms to follow.
    /// Example: <c>"wrap errors with fmt.Errorf(\"%w\", err)"</c>.
    /// </summary>
    [JsonPropertyName("error_handling")]
    public List<string> ErrorHandling { get; init; } = [];

    /// <summary>
    /// Patterns that must not appear in written code.
    /// Example: <c>"no panic() outside main"</c>.
    /// </summary>
    [JsonPropertyName("forbidden_patterns")]
    public List<string> ForbiddenPatterns { get; init; } = [];

    /// <summary>
    /// Testing conventions.
    /// Example: <c>"table-driven tests use testify/require"</c>.
    /// </summary>
    [JsonPropertyName("test_patterns")]
    public List<string> TestPatterns { get; init; } = [];

    /// <summary>Structural observations about the codebase layout.</summary>
    [JsonPropertyName("structural_notes")]
    public List<string> StructuralNotes { get; init; } = [];

    /// <summary>Command that builds the project (e.g. <c>"go build ./..."</c>).</summary>
    [JsonPropertyName("build_command")]
    public string? BuildCommand { get; init; }

    /// <summary>Command that runs the full test suite (e.g. <c>"go test ./..."</c>).</summary>
    [JsonPropertyName("test_command")]
    public string? TestCommand { get; init; }
}

/// <summary>
/// Discovery brief written by the Archaeologist to <see cref="BrownfieldConfig.DiscoveryBriefPath"/>.
/// When <see cref="BrownfieldConfig.SeedEnvelopeFromBrief"/> is true, the
/// <see cref="InScopeFiles"/> list is merged into <see cref="SecurityConfig.ChangeEnvelope"/>
/// at session startup.
/// </summary>
public record BrownfieldDiscoveryBrief
{
    /// <summary>Entry-point files provided to the Archaeologist via config.</summary>
    [JsonPropertyName("entry_points")]
    public List<string> EntryPoints { get; init; } = [];

    /// <summary>
    /// Files determined to be in scope for this session's task.
    /// These are merged into <see cref="SecurityConfig.ChangeEnvelope"/> when
    /// <see cref="BrownfieldConfig.SeedEnvelopeFromBrief"/> is true.
    /// </summary>
    [JsonPropertyName("in_scope_files")]
    public List<string> InScopeFiles { get; init; } = [];

    /// <summary>Files flagged as fragile (high churn, dense TODO/HACK markers, etc.).</summary>
    [JsonPropertyName("fragility_signals")]
    public List<FragilitySignal> FragilitySignals { get; init; } = [];

    /// <summary>Files that lack a corresponding test file.</summary>
    [JsonPropertyName("test_coverage_gaps")]
    public List<string> TestCoverageGaps { get; init; } = [];

    /// <summary>Free-text summary of findings.</summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; init; }
}

/// <summary>A single fragility observation linked to a file.</summary>
public record FragilitySignal
{
    [JsonPropertyName("file")]
    public string File { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Incremental test-selection settings. Exposes the shell command template used to derive
/// the minimal set of tests that cover changed files, so agents can run targeted
/// verification instead of the full suite on every handoff.
/// </summary>
public record TestSelectorConfig
{
    /// <summary>
    /// Shell command template to discover tests related to a changed file.
    /// Use <c>{file}</c> as a placeholder for the file path.
    /// Example: <c>"pytest --collect-only -q --co {file} 2>/dev/null | grep '::'  | head -40"</c>
    /// Example: <c>"go test -list . $(go list -f '{{.Dir}}' {file})"</c>
    /// </summary>
    public string FindRelatedCommand { get; init; } = string.Empty;

    /// <summary>
    /// Full-suite command run when <see cref="FindRelatedCommand"/> returns no results.
    /// Defaults to the convention profile's <c>test_command</c> when null.
    /// </summary>
    public string? FullSuiteCommand { get; init; }
}
