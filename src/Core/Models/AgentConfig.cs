namespace fuseraft.Core.Models;

/// <summary>
/// Full configuration for a single agent participating in an orchestration.
/// </summary>
public record AgentConfig
{
    /// <summary>
    /// Path to an external YAML file containing the base agent definition.
    /// Relative paths are resolved against the directory that contains the
    /// orchestration config file. Inline fields that differ from their default
    /// values override the file; fields left at their defaults are inherited.
    /// This lets commonly-reused agents (Archaeologist, Reviewer, etc.) live in
    /// versioned stand-alone files and be referenced from many orchestration configs.
    /// </summary>
    public string? AgentFile { get; init; }

    /// <summary>
    /// Unique name used to identify this agent within the group chat.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// System-prompt instructions defining the agent's persona and behavior.
    /// </summary>
    public string Instructions { get; init; } = string.Empty;

    /// <summary>
    /// Optional short description shown in selection-strategy prompts.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// LLM backend configuration for this agent.
    /// </summary>
    public ModelConfig Model { get; init; } = new();

    /// <summary>
    /// Named plugins to load into this agent's kernel.
    /// Each name must be registered in <see cref="fuseraft.Infrastructure.Plugins.PluginRegistry"/>.
    /// </summary>
    public List<string> Plugins { get; init; } = [];

    /// <summary>
    /// Controls how the model uses tools each turn.
    /// <list type="bullet">
    ///   <item><c>auto</c> (default): the model may call tools or respond with text.</item>
    ///   <item><c>required</c>: the model MUST call at least one tool per message. Use this
    ///     for action agents (Tester, Developer) to prevent the model from fabricating tool
    ///     output as plain text instead of actually invoking the tools.</item>
    ///   <item><c>none</c>: tools are registered but the model is not allowed to call them.</item>
    /// </list>
    /// Maps to <c>tool_choice</c> in the OpenAI API.
    /// </summary>
    public string FunctionChoice { get; init; } = "auto";

    /// <summary>
    /// Governance trust score (0.0–1.0) used to assign this agent to an execution ring
    /// when <c>EnableRings</c> is active. Higher scores grant more privilege:
    /// <list type="bullet">
    ///   <item><c>≥ 0.80</c> → Ring 1 (Trusted): full tool access within sandbox.</item>
    ///   <item><c>≥ 0.60</c> → Ring 2 (Standard): writes and network allowed. (default)</item>
    ///   <item><c>&lt; 0.60</c> → Ring 3 (Sandbox): read-only, no network.</item>
    /// </list>
    /// </summary>
    public double TrustScore { get; init; } = 0.7;

    /// <summary>
    /// Optional filter applied to the conversation history before it is passed to this
    /// agent each turn. When null the agent receives the full accumulated history
    /// (default behaviour). When set, tool messages, specific agents' output, or older
    /// tail messages can be stripped so the agent starts each turn with a leaner context.
    /// See <see cref="ContextWindowConfig"/> for available filter options.
    /// </summary>
    public ContextWindowConfig? ContextWindow { get; init; }

    /// <summary>
    /// Artifact sources assembled as this agent's context at each invocation.
    /// When set, the agent's context is constructed entirely from these sources rather than
    /// replaying the shared session transcript. This eliminates cross-agent history coupling:
    /// the agent sees only the artifacts it needs plus its own prior turns (via
    /// <c>own_history:N</c>), not the Planner's analysis or another agent's tool traces.
    ///
    /// <para>
    /// Example:
    /// <code>
    /// Context:
    ///   - Source: session_context
    ///   - Source: changes_recent:5
    ///   - Source: brief_field:test_targets
    ///   - Source: brief_field:build_command
    ///   - Source: own_history:4
    /// </code>
    /// </para>
    ///
    /// <para>
    /// When <c>Context</c> is set, <c>ContextWindow</c> is ignored.
    /// The task message is always included regardless of what sources are declared.
    /// </para>
    /// </summary>
    public List<ContextSource>? Context { get; init; }

    /// <summary>
    /// Per-plugin capability allowlist. When a plugin name appears here, only the tools
    /// whose capability tag is in the declared list are registered for this agent. Plugins
    /// listed in <see cref="Plugins"/> that have no entry here receive all of their tools
    /// (the default, backward-compatible behavior).
    ///
    /// <para>
    /// Keys are plugin names (same as in <see cref="Plugins"/>). Values are lists of
    /// capability strings. The available capabilities depend on the plugin:
    /// <list type="table">
    ///   <item><term>FileSystem</term><description><c>read</c>, <c>write</c>, <c>delete</c></description></item>
    ///   <item><term>Shell</term><description><c>read</c> (env/which/cwd), <c>run</c> (shell_run, shell_run_script)</description></item>
    ///   <item><term>Git</term><description><c>read</c> (status/diff/log), <c>write</c> (add/commit/checkout)</description></item>
    ///   <item><term>Http</term><description><c>get</c>, <c>post</c>, <c>put</c>, <c>patch</c>, <c>delete</c></description></item>
    ///   <item><term>Json</term><description><c>read</c>, <c>write</c> (merge)</description></item>
    ///   <item><term>Search</term><description><c>read</c></description></item>
    ///   <item><term>Plan</term><description><c>read</c>, <c>write</c></description></item>
    ///   <item><term>Changes</term><description><c>read</c></description></item>
    ///   <item><term>Scratchpad</term><description><c>read</c>, <c>write</c></description></item>
    ///   <item><term>Chatroom</term><description><c>read</c>, <c>write</c></description></item>
    ///   <item><term>Probe</term><description><c>run</c></description></item>
    ///   <item><term>CodeExecution</term><description><c>read</c>, <c>execute</c></description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Tools whose names are not in the built-in capability map (e.g. MCP-registered tools)
    /// are always allowed regardless of this setting.
    /// </para>
    ///
    /// Example — a Reviewer that can read files and inspect git history but cannot write,
    /// delete, or run shell commands:
    /// <code>
    /// "Capabilities": {
    ///   "FileSystem": ["read"],
    ///   "Git":        ["read"]
    /// }
    /// </code>
    /// </summary>
    public Dictionary<string, List<string>> Capabilities { get; init; } = [];

    /// <summary>
    /// Maximum number of LLM round-trips (iterations) allowed per turn.
    /// Maps to <see cref="Microsoft.Extensions.AI.FunctionInvokingChatClient.MaximumIterationsPerRequest"/>.
    /// 0 (default) uses the framework default of 40 iterations.
    ///
    /// Note: models can request multiple tools in a single response. Each such response counts as
    /// one iteration regardless of how many tools it requests. With typical 2-tool batching, an
    /// iteration limit of N results in approximately 2N individual tool executions per turn.
    /// Set this to half your desired tool-execution budget to account for batching.
    /// </summary>
    public int MaxToolCallsPerTurn { get; init; } = 0;

    /// <summary>
    /// Soft per-call context cap (tokens) applied within the tool-call loop.
    /// Before each inner LLM call, accumulated tool-result messages from the current turn
    /// are trimmed oldest-first so the total in-turn context stays within this budget.
    /// This prevents quadratic token growth: without trimming, N tool calls cost
    /// O(N²) cumulative tokens because each iteration resends all prior results.
    ///
    /// <para>
    /// Trimmed results are replaced with a short placeholder rather than dropped entirely,
    /// so the message structure (tool-call IDs) stays valid for strict providers.
    /// Compaction summaries and the initial user task are never trimmed.
    /// </para>
    ///
    /// <para>Recommended: 30 000–60 000 for action agents (Developer, Tester).</para>
    /// 0 (default) = no trimming.
    /// </summary>
    public int MaxInTurnContextTokens { get; init; } = 0;

    /// <summary>
    /// Hard sliding-window cap on the number of tool call/result pairs kept in full
    /// within the active turn. Before each inner LLM call, tool-result messages beyond
    /// the most-recent <c>MaxInTurnToolPairs</c> are replaced with a compact placeholder.
    /// Unlike <see cref="MaxInTurnContextTokens"/> (which is budget-reactive), this limit
    /// is applied unconditionally on every iteration — the context window cost is
    /// O(MaxInTurnToolPairs) regardless of how many tool calls the agent makes.
    ///
    /// <para>
    /// Use this when you want a deterministic bound rather than a soft budget.
    /// Compatible with <see cref="MaxInTurnContextTokens"/>: both are applied when set,
    /// with the sliding window running first.
    /// </para>
    ///
    /// <para>Recommended: 8–16 for high-volume action agents (Developer, Tester).</para>
    /// 0 (default) = no sliding window.
    /// </summary>
    public int MaxInTurnToolPairs { get; init; } = 0;

    /// <summary>
    /// Controls how much knowledge retrieval the context assembly pipeline performs
    /// for this agent. Retrieval is always on by default; <c>None</c> is the only
    /// way to disable it for latency-sensitive agents.
    /// </summary>
    public KnowledgeWeight KnowledgeWeight { get; init; } = KnowledgeWeight.Default;

    /// <summary>
    /// Superseded by <see cref="KnowledgeWeight"/>. Memory is now always injected at
    /// runtime through <see cref="fuseraft.Orchestration.ContextAssemblyPipeline"/>
    /// rather than baked into agent instructions at construction time.
    /// This property is kept for configuration compatibility but has no effect when
    /// <c>ContextAssemblyPipeline</c> is active (which is always the case for
    /// <see cref="fuseraft.Orchestration.AgentOrchestrator"/>).
    /// </summary>
    [Obsolete("Memory is now always runtime-injected through ContextAssemblyPipeline. " +
              "Set KnowledgeWeight instead to control retrieval breadth.")]
    public bool EnableMemory { get; init; } = false;

    /// <summary>
    /// Optional model override for the sub-agent spawned by the <c>SubAgent</c> plugin.
    /// When set, the sub-agent uses this model instead of inheriting the parent agent's model.
    /// Useful for cost control: a parent running on <c>claude-opus-4-7</c> can delegate
    /// exploration tasks to a cheaper <c>claude-haiku-4-5-20251001</c> sub-agent.
    ///
    /// <para>
    /// Accepts the same model identifier syntax as <see cref="ModelConfig.ModelId"/> — either
    /// a plain provider model ID or a named alias from the <c>Models</c> config section.
    /// </para>
    /// </summary>
    public string? SubAgentModel { get; init; }

    /// <summary>
    /// Plugin names to make available inside the sub-agent spawned by the <c>SubAgent</c> plugin.
    /// When null or empty, the sub-agent receives the default expanded tool set:
    /// FileSystem (read), Search, Shell (run), and Git (read).
    /// When specified, only the listed plugins are provided; each name must be registered in
    /// <see cref="fuseraft.Infrastructure.Plugins.PluginRegistry"/>.
    /// Use <see cref="Capabilities"/> to further filter which tools within each plugin are exposed.
    /// </summary>
    public List<string>? SubAgentPlugins { get; init; }

    /// <summary>
    /// Maximum tool-call iterations allowed inside the sub-agent loop spawned by the
    /// <c>SubAgent</c> plugin. Maps to
    /// <see cref="Microsoft.Extensions.AI.FunctionInvokingChatClient.MaximumIterationsPerRequest"/>
    /// for the sub-agent's inner <c>FunctionInvokingChatClient</c>.
    /// 0 (default) uses the sub-agent's built-in default of 20.
    /// Mirrors <see cref="MaxToolCallsPerTurn"/> but applies only inside the sub-agent loop.
    /// </summary>
    public int SubAgentMaxToolCalls { get; init; } = 0;

    /// <summary>
    /// Tokens produced by this agent when its turn completes successfully.
    /// Used by <see cref="fuseraft.Orchestration.DependencyPlanner"/> to mark dependencies as fulfilled.
    /// Supported token types: <c>artifact:&lt;name&gt;</c>, <c>file:&lt;path&gt;</c>,
    /// <c>symbol:&lt;name&gt;</c>, or plain coarse-capability strings (e.g. <c>analyzed_codebase</c>).
    /// </summary>
    public List<string> Produces { get; init; } = [];

    /// <summary>
    /// Tokens that must be in the fulfilled set before this agent is eligible to run.
    /// The orchestrator blocks this agent until all listed tokens are produced.
    /// Token format mirrors <see cref="Produces"/>.
    /// </summary>
    public List<string> Requires { get; init; } = [];

    /// <summary>
    /// When set, this agent is hosted remotely and accessed via the A2A protocol.
    /// <see cref="RemoteAgentConfig.Url"/> is the base URL of the remote agent;
    /// its agent card is fetched from <c>{Url}/.well-known/agent.json</c> at session startup.
    ///
    /// <para>
    /// When <see cref="RemoteAgent"/> is set, <see cref="Model"/>, <see cref="Plugins"/>,
    /// <see cref="FunctionChoice"/>, and <see cref="Capabilities"/> are ignored — those are
    /// properties of the remote agent and are not configurable from the caller side.
    /// <see cref="Instructions"/>, <see cref="TrustScore"/>, and <see cref="ContextWindow"/>
    /// continue to apply: instructions are prepended to each call, trust score governs the
    /// governance ring assignment, and the context window filter controls what history the
    /// remote agent receives.
    /// </para>
    /// </summary>
    public RemoteAgentConfig? RemoteAgent { get; init; }

}
