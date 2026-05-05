using fuseraft.Core;

namespace fuseraft.Cli.Commands;

internal sealed record GeneratedConfig(
    string MainConfig,
    IReadOnlyList<(string RelativePath, string Content)> AgentFiles)
{
    internal static GeneratedConfig Inline(string yaml) => new(yaml, []);
}

/// <summary>
/// Factory for <c>fuseraft init</c> template scaffolding. Each template is implemented as a
/// partial class method in its own file; this file contains the dispatch entry point and
/// shared helpers used by all templates.
/// </summary>
internal static partial class InitTemplates
{
    /// <summary>
    /// Dispatches to the template factory identified by <paramref name="template"/> and returns
    /// the generated main config YAML together with any agent files to write alongside it.
    /// Unrecognised template names fall back to <see cref="DevTeam"/>.
    /// </summary>
    internal static GeneratedConfig Build(string template, string model, string? endpoint) =>
        template switch
        {
            "research"   => Research(model, endpoint),
            "devops"     => DevOps(model, endpoint),
            "content"    => Content(model, endpoint),
            "minimal"    => GeneratedConfig.Inline(Minimal(model, endpoint)),
            "magentic"   => GeneratedConfig.Inline(Magentic(model, endpoint)),
            "designer"   => GeneratedConfig.Inline(Designer(model, endpoint)),
            "brownfield" => Brownfield(model, endpoint),
            "graph"            => Graph(model, endpoint),
            "brownfield-graph" => BrownfieldGraph(model, endpoint),
            _                  => DevTeam(model, endpoint),
        };

    /// <summary>Returns a newline-prefixed <c>Endpoint:</c> line for inline agent blocks, or empty when <paramref name="endpoint"/> is unset.</summary>
    private static string Ep(string? endpoint, string pad) =>
        string.IsNullOrWhiteSpace(endpoint) ? string.Empty : $"\n{pad}Endpoint: {endpoint}";

    /// <summary>Returns a newline-prefixed <c>Endpoint:</c> line sized for agent YAML files (two-space indent under <c>Model:</c>), or empty when <paramref name="endpoint"/> is unset.</summary>
    private static string EpAgent(string? endpoint) =>
        string.IsNullOrWhiteSpace(endpoint) ? string.Empty : $"\n  Endpoint: {endpoint}";

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

          # Compaction: summarise old turns to prevent context-window overflow.
          # Compaction:
          #   TriggerTurnCount: 30
          #   KeepRecentTurns: 8
          #   Mode: lossless    # or "hybrid" (reconstruction + LLM narrative), "llm" (default)

          # Checkpoint: save and resume sessions across restarts.
          # Checkpoint:
          #   Mode: json
          #   Path: .fuseraft/checkpoints

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
