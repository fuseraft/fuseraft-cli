using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using A2A;
using AgentGovernance;
using AgentGovernance.Audit;
using AgentGovernance.Hypervisor;
using AgentGovernance.Trust;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration;

namespace fuseraft.Infrastructure.Agents;

/// <summary>
/// Assembles <see cref="AIAgent"/> instances from <see cref="AgentConfig"/>,
/// injecting per-agent chat clients, tools, and optional middleware.
///
/// <para>
/// <b>Collaborators</b> (all in <c>fuseraft.Infrastructure.Agents</c>): plugin/tool
/// resolution is owned by <see cref="AgentToolResolver"/>. Chat-client middleware
/// composition (context-trim, adaptive retry, budget/payload enforcement, governance
/// wrapping) is owned by <see cref="AgentMiddlewareBuilder"/>, built on top of the always-on
/// per-turn filter pipeline in <see cref="AgentContextCompactionFilters"/> (also
/// independently consumed by <c>src/Cli/Commands/Repl/ReplFactory.cs</c>). This class
/// retains the small per-session/telemetry surface
/// (<see cref="SetSessionId"/>/<see cref="GetToolCount"/>/<see cref="OnAgentTurnStarting"/>/
/// <see cref="GetDid"/>) and <see cref="Create"/>'s conductor body.
/// </para>
/// </summary>
public sealed class AgentFactory(
    ChatClientFactory chatClientFactory,
    PluginRegistry pluginRegistry,
    SecurityConfig? securityConfig = null,
    ChangeTracker? changeTracker = null,
    ScratchpadConfig? scratchpadConfig = null,
    ChatroomConfig? chatroomConfig = null,
    GovernanceKernel? governanceKernel = null,
    IdentityRegistry? identityRegistry = null,
    EventEmitter? eventEmitter = null,
    ILoggerFactory? loggerFactory = null,
    AgentSkillsProvider? skillsProvider = null,
    ToolResultArtifactStore? toolArtifactStore = null)
{
    private string? _sessionId;
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger(nameof(AgentFactory))
        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void SetSessionId(string sessionId) => _sessionId = sessionId;

    // Maps agent name → DID for the current session. Populated by Create().
    private readonly ConcurrentDictionary<string, AgentIdentity> _identities = new(StringComparer.OrdinalIgnoreCase);

    // Maps agent name → number of registered tool functions. Used by the telemetry layer to
    // estimate tool-schema token overhead (which is not counted in context_chars).
    private readonly ConcurrentDictionary<string, int> _toolCounts = new(StringComparer.OrdinalIgnoreCase);

    // All ITurnResettable plugin instances seen across Create() calls (deduplicated).
    // OnAgentTurnStarting() calls BeginTurn() on every entry before each agent turn.
    // _resettablesLock guards both Add (from Create) and the snapshot (from OnAgentTurnStarting).
    private readonly HashSet<ITurnResettable> _turnResettables = [];
    private readonly object _resettablesLock = new();

    // Plain field initializer (not the lazy-property pattern _middlewareBuilder below needs) —
    // this constructor only closes over primary-constructor parameters, not other instance
    // fields, so it isn't subject to CS0236.
    private readonly AgentToolResolver _toolResolver = new(
        chatClientFactory, pluginRegistry, securityConfig, scratchpadConfig, chatroomConfig, eventEmitter);

    // Lazy (not a field initializer) because the constructor needs _logger, itself an
    // instance field rather than a primary-constructor parameter — CS0236 blocks field
    // initializers from referencing other instance members, but a property getter runs
    // after construction completes, so it's unrestricted. Same reasoning as
    // GraphOrchestrator's _services/_subGraphExecutor/_parallelFanOut fields.
    private AgentMiddlewareBuilder? _middlewareBuilderLazy;
    private AgentMiddlewareBuilder _middlewareBuilder =>
        _middlewareBuilderLazy ??= new(_logger, changeTracker, securityConfig, governanceKernel);

    /// <summary>
    /// Returns the number of tool functions registered for the named agent, or 0 if the
    /// agent has not been created in this session. Used to estimate tool-schema token overhead.
    /// </summary>
    public int GetToolCount(string agentName)
        => _toolCounts.TryGetValue(agentName, out var c) ? c : 0;

    /// <summary>
    /// Resets the per-turn state of all registered <see cref="ITurnResettable"/> plugins
    /// (e.g. FileSystemPlugin's read cache). Call this immediately before each agent turn
    /// so turn-scoped caches start clean.
    /// </summary>
    public void OnAgentTurnStarting()
    {
        ITurnResettable[] snapshot;
        lock (_resettablesLock) snapshot = [.. _turnResettables];
        foreach (var r in snapshot)
            r.BeginTurn();
    }

    /// <summary>
    /// Returns the DID for an agent by name, or a <c>did:fuseraft:</c> fallback
    /// if the agent was not created through this factory.
    /// </summary>
    public string GetDid(string agentName)
    {
        return _identities.TryGetValue(agentName, out var id)
            ? id.Did
            : $"did:fuseraft:{agentName.ToLowerInvariant()}";
    }

    /// <param name="onToolCalling">
    /// Optional callback fired the moment the agent begins executing a tool.
    /// Arguments: (agentName, toolName, argsSummary). Called synchronously from inside
    /// the tool wrapper so callers see each tool call in real time rather than in bulk
    /// after all tools in a batch have finished executing.
    /// </param>
    public AIAgent Create(AgentConfig config, ContextBudgetConfig? sessionBudget = null, Action<string, string, string?>? onToolCalling = null)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
            throw new ArgumentException("Agent Name must not be empty.", nameof(config));

        // Assign a DID to this agent. Replaces any prior identity for the same name
        // so each StreamAsync call gets a fresh identity (sessions don't share DIDs).
        var identity = AgentIdentity.Create(config.Name);
        _identities[config.Name] = identity;

        if (identityRegistry is not null)
        {
            try { identityRegistry.Register(identity); }
            catch (InvalidOperationException) { /* already registered from a prior Create call */ }
        }

        governanceKernel?.AuditEmitter.Emit(
            GovernanceEventType.AgentRegistered,
            agentId:   identity.Did,
            sessionId: "startup",
            data:      new Dictionary<string, object>
            {
                ["agent_name"]  = config.Name,
                ["trust_score"] = config.TrustScore,
            });

        // Remote A2A agent: resolve the agent card and wrap it as a local AIAgent.
        // Tools, plugins, ChatOptions, context budget, and the sandbox filter do not
        // apply — those are properties of the remote agent. Instructions and TrustScore
        // continue to apply (instructions are prepended per turn by the orchestrators;
        // TrustScore governs the governance ring assignment here).
        if (config.RemoteAgent is { Url: { Length: > 0 } remoteUrl } remoteCfg)
        {
            loggerFactory?.CreateLogger(nameof(AgentFactory)).LogWarning(
                "Agent '{AgentName}' uses the A2A protocol (currently preview). " +
                "The A2A integration depends on a pre-release package and its API may change. " +
                "For production-critical workflows, verify compatibility before upgrading.",
                config.Name);

            var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(remoteCfg.TimeoutSeconds) };
            var resolver   = new A2ACardResolver(new Uri(remoteUrl), httpClient);
            var remoteAgent = Task.Run(() => resolver.GetAIAgentAsync(httpClient, loggerFactory: loggerFactory))
                                      .GetAwaiter().GetResult();

            // ChangeTracker wraps at the turn level (BeginTurn / ApplyAsync), so remote
            // agents still appear in the session change log for observability.
            return changeTracker is not null
                ? changeTracker.WrapAgent(remoteAgent, config.Name)
                : remoteAgent;
        }

        var resolvedModel = chatClientFactory.Resolve(config.Model);
        var chatClient    = chatClientFactory.Create(resolvedModel);

        // Instructions are used as-is; memory is now injected at runtime by
        // ContextAssemblyPipeline rather than baked in at construction time.
        // This ensures memory reflects the current session and is ranked by relevance.
        var instructions = config.Instructions;

        // Build the per-agent tool list, apply offload caching, then wrap each tool with a
        // notifying proxy when a ToolCalling callback is registered so notifications fire
        // at invocation time (real-time) rather than after the whole batch finishes executing.
        var tools = _toolResolver.ConvertPluginTools(config, resolvedModel, _sessionId, _turnResettables, _resettablesLock);
        tools     = AgentToolResolver.BuildCachingMiddleware(tools, toolArtifactStore);
        tools     = AgentToolResolver.WrapWithNotifications(tools, config.Name, onToolCalling, _toolCounts);

        // Build ChatOptions (temperature, max tokens, tool mode).
        // The tool list is passed so that MergeOptions can always fall back to the
        // agent's own tools when the inner FunctionInvokingChatClient does not
        // populate ChatOptions.Tools itself — preventing tool_choice being sent
        // without a tools array (which Bedrock/LiteLLM rejects with HTTP 400).
        var chatOptions = AgentMiddlewareBuilder.BuildChatOptions(config, resolvedModel, tools);

        // Pre-flight context budget: 4 chars ≈ 1 token (conservative).
        // Checked before every inner LLM call so we fail fast with a clear message
        // instead of spending API credits on a request the provider will reject.
        var maxContextChars = resolvedModel.MaxContextTokens > 0
            ? resolvedModel.MaxContextTokens * 4
            : 0;

        // In-turn context trim limit. Prevents quadratic token growth: without trimming,
        // N tool calls cost O(N²) cumulative tokens because each LLM iteration in the
        // FunctionInvokingChatClient loop resends all prior tool results. When set, the
        // oldest tool-result messages are replaced with compact placeholders before each
        // inner LLM call so the context stays roughly constant across iterations.
        //
        // Priority order:
        //   1. Per-agent MaxInTurnContextTokens — explicit agent-level override.
        //   2. Session MaxSingleTurnInputTokens / 3 — allocates 1/3 of the per-turn
        //      budget to within-turn tool results, leaving headroom for the system
        //      prompt, tool schemas (~10–20 k tokens), and cross-turn history.
        //   3. Model MaxContextTokens — fall back to the model's context window.
        //   4. DefaultMaxInTurnChars — conservative floor for unconfigured agents.
        //      Halved from the previous 500 k to reduce the risk of single-turn
        //      explosions when neither the session nor the model has explicit limits.
        const int DefaultMaxInTurnChars = 200_000;
        var maxInTurnChars = config.MaxInTurnContextTokens > 0
            ? config.MaxInTurnContextTokens * 4
            : sessionBudget?.MaxSingleTurnInputTokens > 0
                ? sessionBudget.MaxSingleTurnInputTokens / 3 * 4
                : (maxContextChars > 0 ? maxContextChars : DefaultMaxInTurnChars);

        // Deterministic sliding-window cap: always keep only the last N tool call/result
        // pairs in full, replacing older ones with placeholders unconditionally.
        // Applied before the budget-reactive trim so the window runs first.
        // Default unconditionally — O(N²) tool-result accumulation is never desirable
        // regardless of whether MaxContextTokens is configured.
        const int DefaultToolPairsWhenBudgeted = 12;
        var maxInTurnToolPairs = config.MaxInTurnToolPairs > 0
            ? config.MaxInTurnToolPairs
            : DefaultToolPairsWhenBudgeted;

        // Tool schema overhead: computed once at build time since the tool list is fixed
        // for the lifetime of this agent. Included in the context budget and payload
        // estimates so the pre-flight checks account for schema tokens that are invisible
        // in the message list but still count toward the model's input limit.
        var toolSchemaChars = AgentMiddlewareBuilder.EstimateToolSchemaChars(chatOptions?.Tools);

        var maxPayloadBytes = resolvedModel.MaxPayloadBytes;

        var hasHandoff = config.Plugins.Any(p =>
            p.Equals(HandoffPlugin.PluginName, StringComparison.OrdinalIgnoreCase));

        // Always wrap: the adaptive context-trim retry fires on any provider rejection
        // classified as ContextExceeded, regardless of whether explicit limits are set.
        var effectiveClient = _middlewareBuilder.BuildMiddlewareChain(
            chatClient, config, chatOptions,
            maxContextChars, maxInTurnChars, maxInTurnToolPairs,
            toolSchemaChars, maxPayloadBytes, hasHandoff,
            emitter: eventEmitter);

        // Pre-configure FunctionInvokingChatClient and wrap the skills context provider.
        var agentChatClient = AgentMiddlewareBuilder.BuildEventEmitMiddleware(effectiveClient, config, skillsProvider);

        // Construct the base ChatClientAgent with tools and chat options.
        ChatClientAgent baseAgent = new(
            chatClient: agentChatClient,
            instructions: instructions,
            name: config.Name,
            description: config.Description,
            tools: tools.Count > 0 ? tools.Cast<AITool>().ToList() : null);

        // Wrap with middleware: ChangeTracker first (outermost), then Sandbox enforcement.
        // Ordering: ChangeTracker wraps first so it always observes the final result —
        // including [DENIED] responses from the sandbox — making every tool attempt auditable.
        // Set the name on the final wrapped agent so the orchestrator can identify it.
        // MAF's middleware builder preserves the name, but we verify here.
        return _middlewareBuilder.BuildGovernanceMiddleware(baseAgent, config);
    }
}
