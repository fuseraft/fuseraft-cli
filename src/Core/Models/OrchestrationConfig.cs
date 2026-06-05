using fuseraft.Core;

namespace fuseraft.Core.Models;

/// <summary>
/// Top-level orchestration configuration loaded from <c>config/orchestration.yaml</c>.
/// </summary>
public record OrchestrationConfig
{
    /// <summary>
    /// Optional config format version. When set, fuseraft-cli validates that it
    /// understands this version and warns on unrecognized values rather than silently
    /// misinterpreting fields. Format: <c>"YYYY-MM"</c> (e.g. <c>"2026-05"</c>).
    /// Omitting this field disables version validation.
    /// </summary>
    public string? SchemaVersion { get; init; }

    /// <summary>
    /// Human-readable name for this orchestration setup.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Optional description shown at startup.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Path to a Markdown file that replaces the embedded FUSERAFT.md base system prompt
    /// prepended to every agent's instructions. Relative paths are resolved against the
    /// directory that contains the config file. Takes precedence over
    /// <see cref="SystemPrompt"/> when both are set. Omit to use the default embedded prompt.
    /// </summary>
    public string? SystemPromptPath { get; init; }

    /// <summary>
    /// Inline text that replaces the embedded FUSERAFT.md base system prompt prepended to
    /// every agent's instructions. Ignored when <see cref="SystemPromptPath"/> is also set.
    /// Omit to use the default embedded prompt.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Named model aliases reusable across agents.
    /// A value of <c>"Model": "my-alias"</c> in an agent config is resolved here first;
    /// if not found, the model ID is auto-detected against known provider prefixes.
    /// Per-agent <c>Temperature</c> and <c>MaxTokens</c> always take precedence over the alias values.
    /// </summary>
    public Dictionary<string, ModelConfig> Models { get; init; } = [];

    /// <summary>
    /// Ordered list of agents that participate in the group chat.
    /// </summary>
    public List<AgentConfig> Agents { get; init; } = [];

    /// <summary>
    /// Controls which agent speaks next each turn.
    /// </summary>
    public SelectionStrategyConfig Selection { get; init; } = new();

    /// <summary>
    /// Controls when the conversation ends.
    /// </summary>
    public TerminationStrategyConfig? Termination { get; init; }

    /// <summary>
    /// Optional security constraints for plugins that execute code, access files, or make
    /// network requests. All fields default to unrestricted when omitted.
    /// </summary>
    public SecurityConfig Security { get; init; } = new();

    /// <summary>
    /// Optional token cap for the entire session (input + output combined across all turns).
    /// When the cumulative token count exceeds this value, the orchestration stops before the
    /// next turn and surfaces a <see cref="BudgetExceededException"/>. Null (default) means
    /// no limit is enforced.
    /// </summary>
    public int? MaxTotalTokens { get; init; }

    /// <summary>
    /// Input-token threshold per agent turn that triggers a <c>TokenBudgetWarning</c> event.
    /// When an agent's input token count for a single turn exceeds this value, the event fires
    /// so the UI can surface an early warning before a budget blowup occurs.
    /// Defaults to 300,000. Set to 0 to disable the per-turn warning.
    /// </summary>
    public int WarnTurnTokens { get; init; } = 300_000;

    /// <summary>
    /// Optional per-agent context budget enforcement. Tracks cumulative input tokens per
    /// agent across turns; warns at <see cref="ContextBudgetConfig.WarnAt"/> tokens and
    /// triggers automatic compaction at <see cref="ContextBudgetConfig.CutoverAt"/> tokens,
    /// keeping the session alive rather than halting. Counters reset after each compaction
    /// cycle so a session can run indefinitely with compaction enabled.
    ///
    /// <para>
    /// Unlike <see cref="MaxTotalTokens"/> (hard stop, counts input+output across all agents),
    /// <c>ContextBudget</c> counts input tokens per agent independently and responds with
    /// compaction rather than termination.
    /// </para>
    ///
    /// <para>
    /// <see cref="ContextBudgetConfig.CutoverAt"/> requires a <see cref="Compaction"/>
    /// configuration to be present. Null (default) disables per-agent context budget tracking.
    /// </para>
    /// </summary>
    public ContextBudgetConfig? ContextBudget { get; init; }

    /// <summary>
    /// MCP servers to connect to at session startup. Each server's tools are registered
    /// under <see cref="McpServerConfig.Name"/> and can be referenced from agent <c>Plugins</c>
    /// lists alongside the built-in plugins.
    /// </summary>
    public List<McpServerConfig> McpServers { get; init; } = [];

    /// <summary>
    /// Optional compaction settings. When present, conversation history is automatically
    /// summarised once it exceeds the configured turn threshold, keeping the session alive
    /// indefinitely without hitting context-window limits. Null (default) disables compaction.
    /// </summary>
    public CompactionConfig? Compaction { get; init; }

    /// <summary>
    /// Optional validation middleware settings. When present, keyword routes that declare
    /// a <c>Validator</c> name run a deterministic pre-flight check before the route fires.
    /// If the check fails the route is blocked and the source agent is re-invoked with an
    /// injected error message. Null (default) disables all routing validation.
    /// </summary>
    public ValidationConfig? Validation { get; init; }

    /// <summary>
    /// Optional change tracking settings. When present, every file write, shell command,
    /// and git commit made by any agent is automatically recorded to a JSON log on disk.
    /// Downstream agents read the log via the <c>Changes</c> plugin to observe what prior
    /// agents actually did rather than inferring it from chat history. Null (default) disables
    /// change tracking.
    /// </summary>
    public ChangeTrackingConfig? ChangeTracking { get; init; }

    /// <summary>
    /// Optional scratchpad settings. When present, agents that include <c>"Scratchpad"</c>
    /// in their <c>Plugins</c> list gain access to a persistent per-agent key-value store
    /// that survives across sessions. Agents use it to remember decisions, facts, and
    /// summaries without re-reading the full prior history. Null (default) uses the
    /// built-in default path (<c>~/.fuseraft/scratchpad</c>).
    /// </summary>
    public ScratchpadConfig? Scratchpad { get; init; }

    /// <summary>
    /// Optional structured event logging. When present, every agent turn, validation
    /// failure, and HITL escalation is appended as a JSONL record to the configured path.
    /// Null (default) disables event logging.
    /// </summary>
    public EventsConfig? Events { get; init; }

    /// <summary>
    /// Optional chatroom settings. When present, agents that include <c>"Chatroom"</c>
    /// in their <c>Plugins</c> list can send and receive coordination messages through a
    /// shared append-only JSONL log. Null (default) uses the built-in default path
    /// (<c>.fuseraft/chatroom.jsonl</c>).
    /// </summary>
    public ChatroomConfig? Chatroom { get; init; }

    /// <summary>
    /// Optional OpenTelemetry export settings. When present, fuseraft-cli exports traces
    /// and metrics to the configured OTLP endpoint. Null (default) disables telemetry.
    /// </summary>
    public TelemetryConfig? Telemetry { get; init; }

    /// <summary>
    /// Optional checkpoint storage settings. Controls whether session checkpoints are
    /// persisted to disk (default) or kept in memory only. Null uses the global default
    /// (<c>~/.fuseraft/sessions/</c>).
    /// </summary>
    public CheckpointConfig? Checkpoint { get; init; }

    /// <summary>
    /// Named API profiles available to the <c>Http</c> plugin. Each profile bundles a base
    /// URL, default request headers (supports <c>${ENV_VAR}</c> expansion), and a timeout.
    /// Agents reference a profile by name via the <c>profile</c> parameter on any
    /// <c>http_*</c> tool call. Credentials stay in environment variables and never
    /// appear in agent instructions or conversation history.
    /// Example key: <c>"servicenow"</c>.
    /// </summary>
    public Dictionary<string, ApiProfileConfig> ApiProfiles { get; init; } = [];

    /// <summary>
    /// Optional saga (compensating rollback) configuration. When <see cref="SagaConfig.Enabled"/>
    /// is <c>true</c>, workflow execution is wrapped in a <c>SagaOrchestrator</c> that unwinds
    /// completed steps in reverse order and calls each step's compensation logic on failure.
    /// Null (default) disables the saga pattern.
    /// </summary>
    public SagaConfig? Saga { get; init; }

    /// <summary>
    /// Optional structured evidence graph configuration. When present, every file write,
    /// shell command, and git commit is recorded as a typed <see cref="EvidenceNode"/> in
    /// a queryable graph alongside the flat <c>changes.json</c> log. Evidence contracts
    /// query the graph for richer, more accurate results. Null (default) disables the
    /// evidence graph; contracts fall back to reading <c>changes.json</c> directly.
    /// </summary>
    public EvidenceStoreConfig? EvidenceStore { get; init; }

    /// <summary>
    /// Named evidence contracts that can be referenced by name from route
    /// <c>Contracts</c> lists. Each contract defines a composable set of predicates
    /// that must all hold (AND semantics) before a state transition is permitted.
    /// Contracts supplement or replace individual <c>Validators</c> on routes —
    /// both run with AND semantics when both are declared.
    /// </summary>
    public List<ContractConfig>? Contracts { get; init; }

    /// <summary>
    /// Optional self-verification meta-agent. When present, a named Verifier agent
    /// periodically audits the evidence graph for inconsistencies and challenges
    /// unverified claims before transitions fire. The verifier is also automatically
    /// invoked on suspicious transitions (ConflictingEvidence / NoProgress) when the
    /// state machine strategy is active.
    /// Null (default) disables self-verification.
    /// </summary>
    public VerifierConfig? Verifier { get; init; }

    /// <summary>
    /// Optional failure classification and handling policy. When present, routing
    /// validator failures are classified into typed failure modes
    /// (<c>MissingEvidence</c>, <c>InvalidTransition</c>, <c>ConflictingEvidence</c>,
    /// <c>NoProgress</c>) and each type gets a targeted response (reinstructions,
    /// audit triggers, escalation thresholds) instead of the uniform "N failures →
    /// HITL" behaviour. When null, default policies are applied that mirror the
    /// legacy uniform-threshold behaviour while providing better injected messages.
    /// </summary>
    public FailureHandlingConfig? FailureHandling { get; init; }

    /// <summary>
    /// Optional brownfield-mode settings. When present, enables structured recon-phase
    /// support: convention profile injection, change envelope seeding from the
    /// Archaeologist's discovery brief, and entry-point-aware context summarisation.
    /// Null (default) keeps standard greenfield behaviour.
    /// </summary>
    public BrownfieldConfig? Brownfield { get; init; }

    /// <summary>
    /// Optional incremental test-selection settings. Exposes the shell command template
    /// used by agents to discover the minimal test set for a changed file, enabling
    /// targeted verification instead of full-suite runs on every handoff.
    /// Null (default) means agents must use the full test command from the convention
    /// profile or their own instructions.
    /// </summary>
    public TestSelectorConfig? TestSelector { get; init; }

    /// <summary>
    /// Optional post-session skill curation. When enabled, a curator reviews each
    /// successful session and writes reusable procedural knowledge to the skills library
    /// as SKILL.md files. Null (default) disables curation.
    /// </summary>
    public SkillCurationConfig? SkillCuration { get; init; }

    /// <summary>
    /// Optional pluggable memory provider. When present, a <c>MemoryManager</c> is built
    /// and injected into the orchestrator's pre- and post-turn hooks: memory is loaded
    /// before each agent turn and appended to the agent's system instructions; the full
    /// turn history is offered to the provider for persistence after each turn.
    /// Null (default) disables orchestration-level memory (agents that set
    /// <c>EnableMemory: true</c> still use the static file-backed store at creation time).
    /// </summary>
    public MemoryConfig? Memory { get; init; }
}

/// <summary>
/// A named HTTP API profile that bundles a base URL, default request headers, and timeout.
/// Used by the <c>Http</c> plugin so agents can make authenticated API calls without
/// embedding credentials in their instructions. Header values support <c>${ENV_VAR}</c>
/// expansion, which is resolved at startup before agents are built.
/// </summary>
public record ApiProfileConfig
{
    /// <summary>
    /// Base URL prepended to relative paths supplied by the agent.
    /// Must include the scheme and host, and may include a path prefix.
    /// Supports <c>${ENV_VAR}</c> expansion.
    /// Example: <c>"https://${SNOW_INSTANCE}/api/now"</c>
    /// </summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Headers merged into every request that uses this profile. Per-call headers
    /// supplied by the agent take precedence over these defaults on key collision.
    /// Values support <c>${ENV_VAR}</c> expansion.
    /// Example: <c>{ "Authorization": "Basic ${SNOW_BASIC_AUTH}", "Accept": "application/json" }</c>
    /// </summary>
    public Dictionary<string, string> DefaultHeaders { get; init; } = [];

    /// <summary>
    /// Default request timeout in seconds for calls that use this profile.
    /// Can still be overridden per-call via the <c>timeoutSeconds</c> parameter.
    /// Defaults to 30.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>
/// Configuration for the structured JSONL event stream.
/// </summary>
public record EventsConfig
{
    /// <summary>
    /// File path where JSONL events are appended. The directory is created automatically.
    /// Supports <c>{session_id}</c> — expanded at runtime. Example: <c>".fuseraft/logs/sessions/{session_id}/events.jsonl"</c>
    /// </summary>
    public string Path { get; init; } = FuseraftPaths.LocalEventsLog;
}

/// <summary>
/// Controls how session checkpoints are stored.
/// </summary>
public record CheckpointConfig
{
    /// <summary>
    /// Storage backend.
    /// <list type="bullet">
    ///   <item><c>json</c> (default) — each checkpoint is written to an individual JSON file.</item>
    ///   <item><c>memory</c> — checkpoints are kept in memory only; nothing is written to disk.
    ///       Sessions cannot be resumed after the process exits.</item>
    /// </list>
    /// </summary>
    public string Mode { get; init; } = "json";

    /// <summary>
    /// Directory where checkpoint JSON files are stored when <see cref="Mode"/> is <c>json</c>.
    /// Relative paths are resolved against the working directory at run time.
    /// Defaults to <c>~/.fuseraft/sessions/</c> when null or empty.
    /// Example: <c>".fuseraft/checkpoints"</c>
    /// </summary>
    public string? Path { get; init; }
}
