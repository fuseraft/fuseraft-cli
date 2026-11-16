using fuseraft.Core.Models;

namespace fuseraft.Cli.Team;

// Shapes of the pre-v2 "Orchestration:" format that the runtime no longer has, read only by
// `fuseraft migrate`. Everything still shared with the runtime (agents, models, patterns,
// contracts, advanced sections) is bound to the runtime records instead.

/// <summary>The parts of an old config that only <c>fuseraft migrate</c> reads.</summary>
internal sealed record LegacyParts
{
    public LegacySelection Selection { get; init; } = new();
    public LegacyTermination? Termination { get; init; }
    public List<string>? TestAssertionPatterns { get; init; }
    public Dictionary<string, (List<string> Produces, List<string> Requires)> AgentDependencies { get; init; } = [];
}

internal sealed record LegacySelection
{
    public string Type { get; init; } = "sequential";
    public string? DefaultAgent { get; init; }
    public List<LegacyKeywordRoute>? Routes { get; init; }
    public List<LegacyStructuredRoute>? StructuredRoutes { get; init; }
    public LegacyStateMachine? StateMachine { get; init; }
    public LegacyGraph? Graph { get; init; }
}

internal sealed record LegacyKeywordRoute
{
    public string Keyword { get; init; } = string.Empty;
    public string Agent { get; init; } = string.Empty;
    public string? Validator { get; init; }
    public List<string>? Validators { get; init; }
    public List<string>? SourceAgents { get; init; }
    public string? RequiredCommandPattern { get; init; }
    public string? ShellFallbackPattern { get; init; }
    public bool RequireHumanApproval { get; init; }
    public string? RecoveryAgent { get; init; }
}

internal sealed record LegacyStructuredRoute
{
    public string Agent { get; init; } = string.Empty;
    public LegacyCondition Condition { get; init; } = new();
    public List<string>? SourceAgents { get; init; }
}

internal sealed record LegacyCondition
{
    public string Field { get; init; } = string.Empty;
    public string? Is { get; init; }
    public string? IsNot { get; init; }
    public string? Contains { get; init; }
    public bool? Exists { get; init; }
}

internal sealed record LegacyStateMachine
{
    public string Initial { get; init; } = string.Empty;
    public Dictionary<string, LegacyState> States { get; init; } = [];
}

internal sealed record LegacyState
{
    public string Agent { get; init; } = string.Empty;
    public List<LegacyTransition> Transitions { get; init; } = [];
    public bool Terminal { get; init; }
}

internal sealed record LegacyTransition
{
    public string To { get; init; } = string.Empty;
    public List<string>? Targets { get; init; }
    public bool Parallel { get; init; }
    public MergeConfig? Merge { get; init; }
    public string? Signal { get; init; }
    public string? Contract { get; init; }
    public List<string>? Contracts { get; init; }
    public List<string>? SourceAgents { get; init; }
    public string? RecoveryAgent { get; init; }
    public List<ContextSource>? HandoffContext { get; init; }
    public int MaxRevisits { get; init; }
    public string? ReviewArtifactPath { get; init; }

    public IReadOnlyList<string> AllContracts =>
        [.. (Contracts ?? []).Concat(Contract is { Length: > 0 } c ? [c] : []).Distinct(StringComparer.OrdinalIgnoreCase)];
}

internal sealed record LegacyGraph
{
    public string? EntryNode { get; init; }
    public int? MaxRetries { get; init; }
    public List<LegacyGraphNode> Nodes { get; init; } = [];
    public List<LegacyGraphEdge> Edges { get; init; } = [];
    public bool HasSubGraphs { get; init; }
}

internal sealed record LegacyGraphNode
{
    public string Id { get; init; } = string.Empty;
    public string Agent { get; init; } = string.Empty;
    public string? SubGraphId { get; init; }
    public bool Terminal { get; init; }
    public List<string>? Validators { get; init; }
}

internal sealed record LegacyGraphEdge
{
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
    public string? Keyword { get; init; }
    public string? Validator { get; init; }
    public List<string>? Validators { get; init; }
    public string? RequiredCommandPattern { get; init; }
    public string? ShellFallbackPattern { get; init; }
    public bool RequireHumanApproval { get; init; }
    public string? RecoveryAgent { get; init; }
}

internal sealed record LegacyTermination
{
    public string Type { get; init; } = "composite";
    public string? Pattern { get; init; }
    public int MaxIterations { get; init; }
    public string[]? AgentNames { get; init; }
    public List<LegacyTermination>? Strategies { get; init; }

    public int ResolveMaxIterations() =>
        MaxIterations > 0
            ? MaxIterations
            : Strategies?.FirstOrDefault(s => s.Type.Equals("maxiterations", StringComparison.OrdinalIgnoreCase))?.MaxIterations ?? 0;
}

/// <summary>Validator names that only old configs use; current files express them as gates.</summary>
internal static class LegacyValidatorNames
{
    public const string RequireShellPass        = "RequireShellPass";
    public const string RequireAllFilesWritten  = "RequireAllFilesWritten";
    public const string RequireBrief            = "RequireBrief";
    public const string RequireRelatedTestsPass = "RequireRelatedTestsPass";
    public const string TestReportValid         = "TestReportValid";
}
