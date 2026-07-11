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

    // Guards against the most common verify_command failure mode: backgrounding a
    // build-and-run wrapper (go run, npm run dev, cargo run) leaves an orphaned child
    // process that "$!"/pkill cannot target (the wrapper execs into a differently-named
    // PID), blocking the port for every later shell_run call in the session. The
    // CommandSucceeded contract matches verify_command as a substring of a single
    // shell_run invocation, so the fix must keep the smoke test self-contained rather
    // than route it through shell_run_background (which the contract does not see).
    private const string BackgroundedVerifyCommandRule =
        "If verify_command must start a long-running process (server, daemon, listener) to " +
        "exercise it, keep the whole check as ONE shell_run command and never background a " +
        "build-and-run wrapper (go run, npm run dev, cargo run) — they exec into a " +
        "differently-named child process that \"$!\" and pkill cannot reliably target, " +
        "leaving an orphan bound to the port for every later shell_run call this session. " +
        "Build the artifact first, then background the built binary directly, e.g.: " +
        "\"go build -o /tmp/srv ./cmd/server && (/tmp/srv & PID=$!; sleep 1; " +
        "curl -f http://localhost:8080/health; EXIT=$?; kill $PID 2>/dev/null; exit $EXIT)\". " +
        "Prefix the command with a defensive cleanup of any leaked prior instance, e.g. " +
        "\"pkill -f /tmp/srv 2>/dev/null; sleep 0.2;\", so a stale orphan self-heals instead " +
        "of cascading into every later verify_command attempt.";

    // Closes the gap where a Reviewer spot-check succeeds by luck against a stale
    // process left running by an earlier agent, then the Reviewer ignores its own
    // failed re-verification attempts and approves anyway. A spot-check is only
    // evidence if it ran cleanly, this turn, against a process the Reviewer controls.
    private const string ReviewerVerificationIntegrityRule =
        "A spot-check only counts as evidence if it ran cleanly THIS turn. If shell_run " +
        "fails (non-zero exit, \"address already in use\", connection refused, timeout, or " +
        "any error unrelated to the feature itself), the check is INCONCLUSIVE — do not " +
        "approve on an earlier lucky result, and do not treat a response from a process you " +
        "did not start this turn as evidence (a server left running by an earlier agent is " +
        "not proof the change works). If every spot-check attempt this turn fails, do not " +
        "call APPROVED — fix the command and retry once, or call handoff(route_keyword: " +
        "\"REVISION REQUIRED\") noting that verification could not be completed.";

    // Exact schema RequireReviewJudgementValidator parses deterministically (graph edges
    // gated with [RequireReviewJudgement]). The validator requires a fenced ```json block
    // with a top-level "review" array, one entry per acceptance criterion in the brief, and
    // — when any verdict is PASS — a shell_run that succeeded THIS turn. Looser prose here
    // ("emit a JSON review block...") lets the model drift from the schema and get stuck
    // failing the same gate on every retry until the graph aborts.
    private const string ReviewerJudgementBlockRule =
        "Before writing your routing keyword, emit a fenced ```json block (not prose) with " +
        "this exact shape: {\"review\": [{\"criterion\": \"...\", \"verdict\": \"PASS\", " +
        "\"evidence\": \"...\"}, ...]}. The validator checks this mechanically: " +
        "(a) one review entry per acceptance criterion in the brief — fewer entries than " +
        "criteria blocks the handoff; " +
        "(b) every entry needs non-empty criterion, verdict (exactly PASS or FAIL), and " +
        "evidence naming what you actually ran or inspected; " +
        "(c) if any verdict is PASS, a shell_run you executed THIS turn must have succeeded — " +
        "a non-zero exit, timeout, denial, or a result from an earlier turn does not count. " +
        "If any criterion is FAIL, do not write APPROVED — route REVISION REQUIRED (or " +
        "REPLAN REQUIRED) instead. Write the ```json block first, then the routing keyword " +
        "on its own line.";

    // The HasAssertions contract check (ContractEngine.EvaluateTestReportAsync) verifies each
    // test-report result's claimed command is a literal substring of a command that actually
    // succeeded in the change log. A Tester that runs one combined command (e.g. the whole test
    // directory at once) but then writes a narrower per-test command on each result row (e.g.
    // adding a pytest node-id selector it never actually invoked) trips the fabrication guard on
    // every row and can loop until the contract-failure threshold aborts the session.
    private const string TestReportCommandFieldRule =
        "The command field must be the EXACT shell_run command you actually executed for that " +
        "result — copy it verbatim, do not paraphrase or narrow it. If one shell_run verified " +
        "several test cases at once (e.g. running a whole test file or directory), reuse that " +
        "same exact command string for every result row it covers — do NOT invent a more " +
        "specific per-test command (e.g. adding a test node-id selector or extra flags) that " +
        "you never actually ran; the contract engine checks each claimed command against the " +
        "commands that really ran and treats an unmatched, narrower claim as fabricated.";

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
