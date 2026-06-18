using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public sealed record GeneratedConfig(
    string MainConfig,
    IReadOnlyList<(string RelativePath, string Content)> AgentFiles)
{
    public static GeneratedConfig Inline(string yaml) => new(yaml, []);
}

/// <summary>
/// Factory for <c>fuseraft init</c> template scaffolding. Each template is implemented as a
/// partial class method in its own file; this file contains the dispatch entry point and
/// shared helpers used by all templates.
/// </summary>
public static partial class InitTemplates
{
    /// <summary>
    /// Dispatches to the template factory identified by <paramref name="template"/> and returns
    /// the generated main config YAML together with any agent files to write alongside it.
    /// Unrecognised template names fall back to <see cref="DevTeam"/>.
    /// </summary>
    public static GeneratedConfig Build(string template, string model, string? endpoint) =>
        template switch
        {
            "solo"        => GeneratedConfig.Inline(Solo(model, endpoint)),
            "research"    => Research(model, endpoint),
            "pipeline"    => Pipeline(model, endpoint),
            "swe"         => Swe(model, endpoint),
            "greenfield"  => Greenfield(model, endpoint),
            "brownfield"  => Brownfield(model, endpoint),
            "magentic"    => GeneratedConfig.Inline(Magentic(model, endpoint)),
            "debate"      => GeneratedConfig.Inline(Debate(model, endpoint)),
            "audit"       => Audit(model, endpoint),
            "data"        => Data(model, endpoint),
            "devops"      => DevOps(model, endpoint),
            _             => Swe(model, endpoint),
        };

    /// <summary>Returns a newline-prefixed <c>Endpoint:</c> line for inline agent blocks, or empty when <paramref name="endpoint"/> is unset.</summary>
    private static string Ep(string? endpoint, string pad) =>
        string.IsNullOrWhiteSpace(endpoint) ? string.Empty : $"\n{pad}Endpoint: {endpoint}";

    /// <summary>Returns a newline-prefixed <c>Endpoint:</c> line sized for agent YAML files (two-space indent under <c>Model:</c>), or empty when <paramref name="endpoint"/> is unset.</summary>
    private static string EpAgent(string? endpoint) =>
        string.IsNullOrWhiteSpace(endpoint) ? string.Empty : $"\n  Endpoint: {endpoint}";

    // Large-file reading protocol — canonical per-role wording shared across all templates.
    // Update here; each template file references the constant rather than embedding the prose.
    private const string LargeFileProtocol =
        "call get_file_summary first (shows first 30 lines and file size), grep_file to locate the relevant section, then read_file with startLine/maxLines for that section only — files can exceed 10,000 lines; never cold-read a large file in full.";
    private const string LargeFileProtocolArchaeologist =
        "call get_file_summary first (shows the first 30 lines and total line count), grep_file to locate key structures (classes, entry points, imports), then read_file with startLine/maxLines for those sections only — files can exceed 10,000 lines; never cold-read a large file in full.";
    private const string LargeFileProtocolDeveloper =
        "call get_file_summary to check its size, grep_file to locate the exact section to edit, then read_file with startLine/maxLines for that section only — never cold-read a large file in full.";
    private const string LargeFileProtocolReviewer =
        "call get_file_summary first, grep_file to locate the section to inspect, then read_file with startLine/maxLines — never cold-read a large file in full.";

    // Session context handoff protocol — read on entry, write before routing.
    // These steps prevent agents from re-reading files that previous agents already
    // summarised, and give successor agents a current-state snapshot without needing
    // to replay the full conversation history.
    private const string ContextReadStep =
        "Call session_context_read. If a prior summary exists, use it to catch up — do not re-read files that are already described there.";
    private const string ContextWriteStep =
        "Call session_context_write with a short bullet summary: what you accomplished, which files changed, and any open issues (keep it under 200 words).";

    // Standard ContextWindow blocks used by developer and tester agents to strip tool
    // frames from cross-turn history and cap how far back each turn looks.
    private const string DeveloperContextWindow = """
        MaxInTurnContextTokens: 60000
        ContextWindow:
          TextOnly: true
          MaxTurnAge: 5
        """;
    private const string TesterContextWindow = """
        ContextWindow:
          TextOnly: true
          MaxTurnAge: 6
        """;
    private const string VerifierContextWindow = """
        ContextWindow:
          TextOnly: true
          MaxTurnAge: 6
        """;

    private const string AgentFileOptions = """

        # -- Optional overrides -------------------------------------------------------
        # ContextWindow:
        #   TextOnly: true          # strip tool frames from cross-turn history
        #   MaxTurnAge: 10          # keep only the last N agent turns in this agent's context
        #   MaxTailMessages: 40     # hard cap: keep only the last N messages after other filters
        #   ExcludeAgents: []       # remove all messages authored by these agents
        # FunctionChoice: required  # force at least one tool call per turn (auto|required|none)
        # TrustScore: 0.8           # 0.0–1.0; governs sandbox ring (≥0.8 → ring 1)
        # MaxToolCallsPerTurn: 20
        # MaxInTurnToolPairs: 12        # sliding window: keep only last N tool results per turn (deterministic)
        # MaxTokens: 4096
        # Capabilities:             # per-plugin tool allowlist
        #   Shell: [shell_run]
        #   FileSystem: [read_file, list_files]
        """;

    private static string OptionalSections(string model, string? endpoint) => $"""

          # ---------------------------------------------------------------------------
          # OPTIONAL SECTIONS — uncomment and fill in as needed
          # ---------------------------------------------------------------------------

          # Named model aliases. Agents reference these by alias so you only need to
          # change the model ID in one place. Supports any OpenAI-compatible endpoint.
          # Models:
          #   fast:
          #     ModelId: {model}
          #     Endpoint: {(string.IsNullOrWhiteSpace(endpoint) ? "https://api.openai.com/v1" : endpoint)}
          #     ApiKeyEnvVar: OPENAI_API_KEY
          #     MaxContextTokens: 128000
          #   reasoning:
          #     ModelId: {model}
          #     ReasoningEffort: low

          # Sandbox agents to a directory and restrict outbound HTTP hosts.
          # Security:
          #   FileSystemSandboxPath: ~/my-project
          #   HttpAllowedHosts:
          #     - api.github.com
          #     - registry.npmjs.org

          # EvidenceStore: structured evidence graph — required for contracts and lossless compaction.
          # EvidenceStore:
          #   Path: {FuseraftPaths.LocalEvidence}

          # Contracts: evidence-gated guards on routes or state machine transitions.
          # Contracts:
          #   - Name: BriefExists
          #     Requires:
          #       - Type: FileExists
          #         Path: {FuseraftPaths.LocalBrief}
          #   - Name: ImplementationComplete
          #     Requires:
          #       - Type: CommandSucceeded
          #         Pattern: "build|compile"

          # FailureHandling: targeted correction policy per failure type.
          # FailureHandling:
          #   MissingEvidence:
          #     Action: Reinstruct
          #     Threshold: 3
          #   ConflictingEvidence:
          #     Action: Reinstruct
          #     Threshold: 2

          # Verifier: meta-agent that audits the evidence graph for inconsistencies.
          # Verifier:
          #   AgentName: Verifier          # must match an agent name in Agents
          #   EveryNTurns: 5
          #   TriggerOnSuspiciousTransition: true
          #   FindingsKeyword: INCONSISTENCY

          Compaction:
            TriggerTurnCount: 30
            KeepRecentTurns: 8
            Mode: lossless

          # Checkpoint: save and resume sessions across restarts.
          # Checkpoint:
          #   Mode: json
          #   Path: {FuseraftPaths.LocalCheckpoints}

          # ChangeTracking: record every file write/delete made by agents.
          # ChangeTracking:
          #   Path: {FuseraftPaths.LocalChanges}

          # Validation: paths used by built-in routing validators.
          # Validation:
          #   BriefPath: {FuseraftPaths.LocalBrief}
          #   TestReportPath: {FuseraftPaths.LocalTestReport}
          #   ChangeLogPath: {FuseraftPaths.LocalChanges}

          Events:
            Path: {FuseraftPaths.LocalEventsLog}

          # MaxTotalTokens: token budget (input + output combined) — session halts when exceeded.
          # MaxTotalTokens: 200000

          # McpServers: connect to Model Context Protocol tool servers.
          # McpServers:
          #   - Name: my-mcp-server
          #     Command: npx
          #     Args: [-y, "@modelcontextprotocol/server-filesystem", "."]
        """;
}
