using System.Collections.Concurrent;
using A2A;
using AgentGovernance;
using AgentGovernance.Audit;
using AgentGovernance.Hypervisor;
using AgentGovernance.Trust;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration;

namespace fuseraft.Infrastructure;

/// <summary>
/// Assembles <see cref="AIAgent"/> instances from <see cref="AgentConfig"/>,
/// injecting per-agent chat clients, tools, and optional middleware.
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

    // All ITurnResettable plugin instances seen across Create() calls (deduplicated).
    // OnAgentTurnStarting() calls BeginTurn() on every entry before each agent turn.
    // _resettablesLock guards both Add (from Create) and the snapshot (from OnAgentTurnStarting).
    private readonly HashSet<ITurnResettable> _turnResettables = [];
    private readonly object _resettablesLock = new();

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
    public AIAgent Create(AgentConfig config, Action<string, string, string?>? onToolCalling = null)
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

        // Prepend persistent memory to instructions when the agent opts in.
        var instructions = config.Instructions;
        if (config.EnableMemory)
        {
            var memBlock = MemoryStore.ForAgent(config.Name).BuildPromptBlock();
            if (memBlock is not null)
                instructions = $"{memBlock}\n\n{instructions}";
        }

        // Build the per-agent tool list. Wrap each tool with a notifying proxy when a
        // ToolCalling callback is registered so notifications fire at invocation time
        // (real-time) rather than after the whole batch finishes executing.
        var tools = BuildTools(config, resolvedModel, config.Name, onToolCalling);

        // Build ChatOptions (temperature, max tokens, tool mode).
        // The tool list is passed so that MergeOptions can always fall back to the
        // agent's own tools when the inner FunctionInvokingChatClient does not
        // populate ChatOptions.Tools itself — preventing tool_choice being sent
        // without a tools array (which Bedrock/LiteLLM rejects with HTTP 400).
        var chatOptions = BuildChatOptions(config, resolvedModel, tools);

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
        var maxInTurnChars = config.MaxInTurnContextTokens > 0
            ? config.MaxInTurnContextTokens * 4
            : 0;

        // Deterministic sliding-window cap: always keep only the last N tool call/result
        // pairs in full, replacing older ones with placeholders unconditionally.
        // Applied before the budget-reactive trim so the window runs first.
        // When MaxContextTokens is set but no explicit pair limit is configured, default
        // to 12 pairs to prevent O(N²) tool-result accumulation within a turn.
        const int DefaultToolPairsWhenBudgeted = 12;
        var maxInTurnToolPairs = config.MaxInTurnToolPairs > 0
            ? config.MaxInTurnToolPairs
            : (resolvedModel.MaxContextTokens > 0 ? DefaultToolPairsWhenBudgeted : 0);

        // Tool schema overhead: computed once at build time since the tool list is fixed
        // for the lifetime of this agent. Included in the context budget and payload
        // estimates so the pre-flight checks account for schema tokens that are invisible
        // in the message list but still count toward the model's input limit.
        var toolSchemaChars = EstimateToolSchemaChars(chatOptions?.Tools);

        var maxPayloadBytes = resolvedModel.MaxPayloadBytes;

        var hasHandoff = config.Plugins.Any(p =>
            p.Equals(HandoffPlugin.PluginName, StringComparison.OrdinalIgnoreCase));

        // Always wrap: the adaptive context-trim retry fires on any provider rejection
        // classified as ContextExceeded, regardless of whether explicit limits are set.
        var effectiveClient = chatClient.AsBuilder()
            .Use(
                getResponseFunc: async (messages, options, inner, ct) =>
                {
                    // Strip verbose reasoning text from ALL intermediate tool-calling assistant
                    // messages before the window filter — reasoning from prior calls in the
                    // same turn is never needed again and is the primary cause of the O(N²)
                    // token growth seen with grok-build and other reasoning-heavy models.
                    messages = TruncateIntermediateAssistantReasoning(messages);

                    if (maxInTurnToolPairs > 0)
                        messages = KeepLastToolPairs(messages, maxInTurnToolPairs);

                    if (maxInTurnChars > 0)
                        messages = TrimInTurnContext(messages, maxInTurnChars);

                    // Stop the FunctionInvokingChatClient loop immediately after handoff —
                    // no follow-up LLM call is made, so the agent cannot call more tools.
                    if (hasHandoff && HandoffWasInvoked(messages))
                        return new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty));

                    var merged  = chatOptions is not null ? MergeOptions(messages, options, chatOptions) : options;
                    var baseMsg = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

                    // Adaptive retry: on ContextExceeded the context is progressively
                    // trimmed (tool results truncated → dropped) and the call retried.
                    // Pre-flight budget/payload checks run on each attempt so they act as
                    // early-exit guards rather than hard failures.
                    for (int attempt = 0; ; attempt++)
                    {
                        var ctx = attempt == 0
                            ? (IEnumerable<ChatMessage>)baseMsg
                            : AdaptiveTrimMessages(baseMsg, attempt);
                        try
                        {
                            if (maxContextChars > 0)
                                EnforceContextBudget(config.Name, ctx, maxContextChars, toolSchemaChars);
                            if (maxPayloadBytes > 0)
                                EnforcePayloadLimit(config.Name, ctx, toolSchemaChars, maxPayloadBytes);
                            return await inner.GetResponseAsync(ctx, merged, ct);
                        }
                        catch (Exception ex) when (attempt < AdaptiveContextTrimMaxRetries
                                                   && IsContextLimitException(ex))
                        {
                            _logger.LogWarning(
                                "[context-trim] {Agent} stage {Stage}/{Max}: {Error} — reducing tool results and retrying",
                                config.Name, attempt + 1, AdaptiveContextTrimMaxRetries,
                                ex.Message[..Math.Min(ex.Message.Length, 120)].Replace('\n', ' '));
                        }
                    }
                },
                getStreamingResponseFunc: (messages, options, inner, ct) =>
                {
                    messages = TruncateIntermediateAssistantReasoning(messages);

                    if (maxInTurnToolPairs > 0)
                        messages = KeepLastToolPairs(messages, maxInTurnToolPairs);

                    if (maxInTurnChars > 0)
                        messages = TrimInTurnContext(messages, maxInTurnChars);
                    if (hasHandoff && HandoffWasInvoked(messages))
                        return EmptyStreamingResponse();

                    var merged = chatOptions is not null ? MergeOptions(messages, options, chatOptions) : options;

                    // Cannot retry mid-stream — pre-trim proactively when limits are known.
                    // Without configured limits we have no target, so trimming is skipped and
                    // a provider rejection surfaces as a normal error for the user to see.
                    if (maxContextChars > 0 || maxPayloadBytes > 0)
                        messages = ProactivelyTrimIfNeeded(
                            config.Name, messages, maxContextChars, maxPayloadBytes, toolSchemaChars, _logger);

                    return inner.GetStreamingResponseAsync(messages, merged, ct);
                })
            .Build();

        // Pre-configure FunctionInvokingChatClient so ChatClientAgent reuses our instance
        // (it only adds its own when none is present in the pipeline). This lets us set
        // MaximumIterationsPerRequest per agent instead of accepting the framework default (40).
        // We always set this so the limit is explicit and visible, even when using the default.
        var maxIterations = config.MaxToolCallsPerTurn > 0 ? config.MaxToolCallsPerTurn : 40;
        var functionInvokingClient = effectiveClient
            .AsBuilder()
            .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = maxIterations)
            .Build();

        // Skills context provider wraps outside the function-invoker so that skill tools
        // (load_skill, run_skill_script, etc.) are visible to the function-invoker when
        // the model requests them. AIContextProvider must be the outermost layer.
        IChatClient agentChatClient = skillsProvider is not null
            ? functionInvokingClient.AsBuilder().UseAIContextProviders(skillsProvider).Build()
            : functionInvokingClient;

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
        AIAgent agent = baseAgent;

        if (changeTracker is not null)
            agent = changeTracker.WrapAgent(agent, config.Name);

        if (!string.IsNullOrEmpty(securityConfig?.FileSystemSandboxPath))
        {
            var ring = governanceKernel?.Rings?.ComputeRing(config.TrustScore) ?? ExecutionRing.Ring2;
            agent = new SandboxEnforcementFilter(
                    securityConfig.FileSystemSandboxPath,
                    governanceKernel?.InjectionDetector,
                    ring,
                    securityConfig.ChangeEnvelope,
                    securityConfig.FileSystemPermissions)
                .WrapAgent(agent);
        }

        // Set the name on the final wrapped agent so the orchestrator can identify it.
        // MAF's middleware builder preserves the name, but we verify here.
        return agent;
    }

    // Helpers

    private List<AIFunction> BuildTools(
        AgentConfig config,
        ModelConfig resolvedModel,
        string agentName,
        Action<string, string, string?>? onToolCalling)
    {
        var tools = new List<AIFunction>();

        foreach (var pluginName in config.Plugins)
        {
            IEnumerable<AIFunction> functions;

            // "Skills" is handled by AgentSkillsProvider (UseAIContextProviders), which
            // injects load_skill / run_skill_script as tools on the chat client pipeline.
            // The Plugins entry is a declaration of intent; no registry lookup is needed.
            if (pluginName.Equals("Skills", StringComparison.OrdinalIgnoreCase))
                continue;
            // "Scratchpad" is per-agent — each agent gets its own file.
            else if (pluginName.Equals("Scratchpad", StringComparison.OrdinalIgnoreCase))
            {
                var basePath = scratchpadConfig?.BasePath ?? FuseraftPaths.GlobalScratchpad;
                functions = PluginRegistry.GetFunctionsFromObject(new ScratchpadPlugin(config.Name, basePath));
            }
            // "SubAgent" is per-agent — each agent gets its own lightweight IChatClient
            // (optionally on a different, cheaper model) and a configurable tool set so
            // the sub-agent respects the same sandbox constraints.
            else if (pluginName.Equals("SubAgent", StringComparison.OrdinalIgnoreCase))
            {
                // Allow the sub-agent to run on a different model (e.g. Haiku for cost control).
                var subModel  = string.IsNullOrWhiteSpace(config.SubAgentModel)
                    ? resolvedModel
                    : chatClientFactory.Resolve(new ModelConfig { ModelId = config.SubAgentModel });
                var subClient = chatClientFactory.Create(subModel);

                var explorerTools = BuildSubAgentTools(config, pluginRegistry, securityConfig);

                functions = PluginRegistry.GetFunctionsFromObject(
                    new SubAgentPlugin(subClient, explorerTools,
                        eventEmitter:    eventEmitter,
                        parentAgentName: config.Name,
                        maxToolCalls:    config.SubAgentMaxToolCalls));
            }
            // "Chatroom" is per-agent (own sender name) but all agents share the same file.
            else if (pluginName.Equals("Chatroom", StringComparison.OrdinalIgnoreCase))
            {
                var chatPath = FuseraftPaths.ExpandSessionId(
                    chatroomConfig?.Path ?? FuseraftPaths.LocalChatroom,
                    _sessionId ?? "startup");
                functions = PluginRegistry.GetFunctionsFromObject(new ChatroomPlugin(config.Name, chatPath));
            }
            else if (pluginRegistry.TryGetAIFunctions(pluginName, out var aiFunctions))
            {
                functions = aiFunctions;
            }
            else if (pluginRegistry.TryGet(pluginName, out var plugin))
            {
                functions = PluginRegistry.GetFunctionsFromObject(plugin);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Agent '{config.Name}' references unknown plugin '{pluginName}'. " +
                    $"Registered plugins: {string.Join(", ", pluginRegistry.RegisteredPlugins)}");
            }

            // Apply per-plugin capability filter when the agent declares constraints.
            // Tools absent from the capability map (e.g. MCP tools) pass through unfiltered.
            if (config.Capabilities.TryGetValue(pluginName, out var caps) && caps.Count > 0)
                functions = functions.Where(f => PluginCapabilityMap.IsAllowed(f.Name, caps));

            tools.AddRange(functions);
        }

        // Collect any newly-seen ITurnResettable plugin instances so OnAgentTurnStarting
        // can reset their per-turn state before each agent's turn begins.
        foreach (var pluginName in config.Plugins)
        {
            if (pluginRegistry.TryGet(pluginName, out var obj) && obj is ITurnResettable tr)
                lock (_resettablesLock) _turnResettables.Add(tr);
        }

        // Wrap every tool with an offload filter so oversized results are stored to disk
        // before they enter the conversation history. Applied before the notification proxy
        // so the stub is what the provider receives, not the raw large content.
        if (toolArtifactStore is not null)
            tools = tools.Select(f => (AIFunction)new ToolResultOffloadFilter(f, toolArtifactStore)).ToList();

        // Wrap every tool with a notifying proxy so onToolCalling fires the moment the
        // tool begins execution, not after the whole batch finishes.
        if (onToolCalling is not null)
            return tools.Select(f => (AIFunction)new NotifyingAIFunction(f, agentName, onToolCalling)).ToList();

        return tools;
    }

    // Assembles the tool list for a sub-agent spawned by SubAgentPlugin.
    // When config.SubAgentPlugins is set, uses those plugins (capability-filtered like normal agents).
    // Otherwise falls back to the expanded default: FileSystem read, Search, Shell run, Git read.
    private static List<AIFunction> BuildSubAgentTools(
        AgentConfig config,
        PluginRegistry pluginRegistry,
        SecurityConfig? securityConfig)
    {
        var tools = new List<AIFunction>();

        if (config.SubAgentPlugins is { Count: > 0 })
        {
            // Custom plugin list — resolve and capability-filter the same way BuildTools does.
            foreach (var name in config.SubAgentPlugins)
            {
                IEnumerable<AIFunction> fns;
                if (pluginRegistry.TryGetAIFunctions(name, out var aiFns))
                    fns = aiFns;
                else if (pluginRegistry.TryGet(name, out var p))
                    fns = PluginRegistry.GetFunctionsFromObject(p);
                else
                    throw new InvalidOperationException(
                        $"Agent '{config.Name}' references unknown sub-agent plugin '{name}'. " +
                        $"Registered plugins: {string.Join(", ", pluginRegistry.RegisteredPlugins)}");

                if (config.Capabilities.TryGetValue(name, out var caps) && caps.Count > 0)
                    fns = fns.Where(f => PluginCapabilityMap.IsAllowed(f.Name, caps));

                tools.AddRange(fns);
            }
            return tools;
        }

        // Default: expanded read-oriented set. FileSystem (sandboxed, read ops only).
        var fsPlugin = new FileSystemPlugin(securityConfig?.FileSystemSandboxPath);
        var fsReadTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "read_file", "list_files", "grep_file", "get_file_summary", "get_file_info" };
        tools.AddRange(
            PluginRegistry.GetFunctionsFromObject(fsPlugin)
                          .Where(f => fsReadTools.Contains(f.Name)));

        // Search: all tools.
        if (pluginRegistry.TryGet("Search", out var searchPlugin))
            tools.AddRange(PluginRegistry.GetFunctionsFromObject(searchPlugin));

        // Shell: run commands (builds, tests) + env/path helpers.
        if (pluginRegistry.TryGet("Shell", out var shellPlugin))
        {
            var shellAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "shell_run", "shell_get_env", "shell_which", "shell_get_working_directory" };
            tools.AddRange(
                PluginRegistry.GetFunctionsFromObject(shellPlugin)
                              .Where(f => shellAllowed.Contains(f.Name)));
        }

        // Git: read-only operations.
        if (pluginRegistry.TryGet("Git", out var gitPlugin))
        {
            var gitReadOps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "git_status", "git_diff", "git_log", "git_show", "git_branch_list", "git_stash_list" };
            tools.AddRange(
                PluginRegistry.GetFunctionsFromObject(gitPlugin)
                              .Where(f => gitReadOps.Contains(f.Name)));
        }

        return tools;
    }

    /// <summary>
    /// Transparent proxy that fires <paramref name="onToolCalling"/> the moment a tool
    /// begins executing, forwarding all schema and metadata from the inner function.
    /// Deterministically validates that all required parameters are present before invocation.
    /// Using <see cref="DelegatingAIFunction"/> means the model sees the exact same
    /// parameter schema as the original tool.
    /// </summary>
    private sealed class NotifyingAIFunction : DelegatingAIFunction
    {
        private readonly string _agentName;
        private readonly Action<string, string, string?> _onToolCalling;

        public NotifyingAIFunction(AIFunction inner, string agentName, Action<string, string, string?> onToolCalling)
            : base(inner)
        {
            _agentName     = agentName;
            _onToolCalling = onToolCalling;
        }

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            _onToolCalling(_agentName, Name, ToolCallHelper.SummarizeArgs(arguments));
            
            // Deterministically validate required parameters BEFORE invocation.
            // This prevents the ArgumentException from being thrown deep in the invocation stack
            // and returns a structured error message that the LLM can see and correct.
            var validationError = ValidateRequiredParameters(arguments);
            if (validationError is not null)
                return validationError;
            
            return await InnerFunction.InvokeAsync(arguments, cancellationToken);
        }

        /// <summary>
        /// Validates that all required parameters (non-nullable, non-optional) are present
        /// in the arguments dictionary. Returns a structured error message if any are missing.
        /// </summary>
        private string? ValidateRequiredParameters(AIFunctionArguments arguments)
        {
            // Access the underlying C# method to get accurate parameter metadata
            var method = InnerFunction.GetType()
                .GetProperty("UnderlyingMethod", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(InnerFunction) as System.Reflection.MethodInfo;

            if (method is null)
                return null; // Can't validate without method metadata

            var missing = new List<string>();
            foreach (var param in method.GetParameters())
            {
                // Skip CancellationToken
                if (param.ParameterType == typeof(CancellationToken))
                    continue;

                // A parameter is required if it's not optional and not nullable
                bool isOptional = param.IsOptional || param.HasDefaultValue;
                bool isNullable = param.ParameterType.IsClass || 
                                  Nullable.GetUnderlyingType(param.ParameterType) != null;

                if (!isOptional && !isNullable && !arguments.ContainsKey(param.Name!))
                {
                    missing.Add(param.Name!);
                }
            }

            if (missing.Count == 0)
                return null;

            // Build a structured error message that tells the LLM exactly what's wrong.
            var paramList = string.Join(", ", missing.Select(p => $"'{p}'"));
            var plural = missing.Count > 1 ? "parameters" : "parameter";
            return $"[ERROR] Tool call failed: required {plural} {paramList} not provided.\n\n" +
                   $"To fix: Call {Name} again with all required parameters included.";
        }
    }

    /// <summary>
    /// Unconditionally keeps only the most-recent <paramref name="maxPairs"/> tool call/result
    /// pairs in full; older pairs are replaced with a compact placeholder. Applied on every
    /// inner LLM call regardless of total context size, giving an O(maxPairs) tool-result
    /// footprint per iteration. Non-tool messages are never touched.
    /// </summary>
    // Maximum chars kept for text/reasoning content in an intermediate tool-calling message.
    private const int MaxIntermediateAssistantTextChars = 120;
    // Maximum chars kept for a single function-call argument value in an intermediate message.
    // Large values (e.g. write_file content argument) accumulate in every subsequent step's
    // call frame, causing O(N) growth per step that compounds across N steps to O(N²) total.
    private const int MaxIntermediateArgValueChars = 500;

    /// <summary>
    /// Truncates verbose content in intermediate (tool-calling) assistant messages:
    /// <list type="bullet">
    ///   <item>Text and reasoning content truncated to <see cref="MaxIntermediateAssistantTextChars"/>.
    ///     <see cref="TextReasoningContent.ProtectedData"/> is preserved so the provider can
    ///     continue the reasoning chain.</item>
    ///   <item>Large <see cref="FunctionCallContent"/> argument values truncated to
    ///     <see cref="MaxIntermediateArgValueChars"/>. Short values (paths, flags) are kept
    ///     in full; only bulk payloads (file contents, scripts) are elided.</item>
    /// </list>
    /// Pure-text (non-tool) messages are never modified.
    /// </summary>
    private static IEnumerable<ChatMessage> TruncateIntermediateAssistantReasoning(
        IEnumerable<ChatMessage> messages)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();

        // Fast path: no assistant messages with tool calls.
        if (!list.Any(m => m.Role == ChatRole.Assistant &&
                           m.Contents.OfType<FunctionCallContent>().Any()))
            return list;

        var result = new List<ChatMessage>(list.Count);
        foreach (var msg in list)
        {
            if (msg.Role != ChatRole.Assistant)
            {
                result.Add(msg);
                continue;
            }

            if (!msg.Contents.OfType<FunctionCallContent>().Any())
            {
                // Pure text message (final response, orchestrator signal) — keep as-is.
                result.Add(msg);
                continue;
            }

            // Intermediate tool-calling message: truncate each content item individually.
            bool anyTruncated = false;
            var rebuilt = new List<AIContent>(msg.Contents.Count);
            foreach (var content in msg.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent trc:
                        // Truncate verbose reasoning text. ProtectedData (the opaque blob the
                        // provider needs for round-trip extended thinking) is preserved intact.
                        if (!string.IsNullOrEmpty(trc.Text) && trc.Text.Length > MaxIntermediateAssistantTextChars)
                        {
                            rebuilt.Add(new TextReasoningContent(
                                trc.Text[..MaxIntermediateAssistantTextChars] + "[reasoning omitted]")
                            {
                                ProtectedData = trc.ProtectedData
                            });
                            anyTruncated = true;
                        }
                        else
                        {
                            rebuilt.Add(content);
                        }
                        break;

                    case TextContent tc:
                        if (!string.IsNullOrEmpty(tc.Text) && tc.Text.Length > MaxIntermediateAssistantTextChars)
                        {
                            rebuilt.Add(new TextContent(
                                tc.Text[..MaxIntermediateAssistantTextChars] + "[text omitted]"));
                            anyTruncated = true;
                        }
                        else
                        {
                            rebuilt.Add(content);
                        }
                        break;

                    case FunctionCallContent fc:
                        // Truncate large argument values. The call ID and function name are
                        // always preserved; only bulk string payloads (file contents, scripts)
                        // are replaced with a size annotation.
                        if (fc.Arguments?.Any(kv => IsLargeArgValue(kv.Value)) == true)
                        {
                            var truncatedArgs = new AIFunctionArguments(
                                fc.Arguments.ToDictionary(
                                    kv => kv.Key,
                                    kv => IsLargeArgValue(kv.Value)
                                        ? TruncateArgValue(kv.Value)
                                        : kv.Value));
                            rebuilt.Add(new FunctionCallContent(
                                fc.CallId ?? fc.Name ?? string.Empty,
                                fc.Name ?? string.Empty,
                                truncatedArgs));
                            anyTruncated = true;
                        }
                        else
                        {
                            rebuilt.Add(content);
                        }
                        break;

                    default:
                        rebuilt.Add(content);
                        break;
                }
            }

            result.Add(anyTruncated
                ? new ChatMessage(msg.Role, rebuilt) { AuthorName = msg.AuthorName }
                : msg);
        }
        return result;
    }

    private static bool IsLargeArgValue(object? value) => value switch
    {
        string s                                                                   => s.Length > MaxIntermediateArgValueChars,
        System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String
            => (je.GetString()?.Length ?? 0) > MaxIntermediateArgValueChars,
        _ => false
    };

    private static object? TruncateArgValue(object? value) => value switch
    {
        string s                                                                   => $"[{s.Length:N0} chars — omitted from intermediate context]",
        System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String
            => $"[{je.GetString()?.Length ?? 0:N0} chars — omitted from intermediate context]",
        _ => value
    };

    private static IEnumerable<ChatMessage> KeepLastToolPairs(
        IEnumerable<ChatMessage> messages,
        int maxPairs)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();

        // Collect indices of ChatRole.Tool messages in order (oldest → newest).
        var toolIndices = new List<int>(list.Count);
        for (int i = 0; i < list.Count; i++)
            if (list[i].Role == ChatRole.Tool) toolIndices.Add(i);

        if (toolIndices.Count <= maxPairs) return list;

        var result = new List<ChatMessage>(list);
        const string Placeholder = "[result omitted — sliding window]";
        int cutoff = toolIndices.Count - maxPairs;
        for (int k = 0; k < cutoff; k++)
        {
            int idx = toolIndices[k];
            var old = result[idx];
            var trimmed = old.Contents
                .OfType<FunctionResultContent>()
                .Select(fr => (AIContent)new FunctionResultContent(fr.CallId, Placeholder))
                .ToList<AIContent>();
            result[idx] = new ChatMessage(old.Role,
                trimmed.Count > 0 ? trimmed : [new TextContent(Placeholder)]);
        }
        return result;
    }

    /// <summary>
    /// Trims accumulated in-turn tool-result messages when total character count exceeds
    /// <paramref name="maxChars"/>. Oldest <see cref="ChatRole.Tool"/> result messages are
    /// replaced with a compact placeholder (preserving the <c>CallId</c> so the provider
    /// sees a structurally valid conversation). Non-tool messages are never removed.
    /// </summary>
    private static IEnumerable<ChatMessage> TrimInTurnContext(
        IEnumerable<ChatMessage> messages,
        int maxChars)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();

        // Count chars across all messages.
        int total = 0;
        foreach (var m in list)
            foreach (var c in m.Contents)
                total += EstimateContentChars(c);

        if (total <= maxChars) return list;

        // Collect indices of ChatRole.Tool messages that can be trimmed (oldest first).
        var trimCandidates = new Queue<int>();
        for (int i = 0; i < list.Count; i++)
            if (list[i].Role == ChatRole.Tool) trimCandidates.Enqueue(i);

        // Phase 1: replace oldest tool results with a tiny placeholder until under budget.
        var result = new List<ChatMessage>(list);
        const string Placeholder = "[result omitted — in-turn context trimmed]";
        while (total > maxChars && trimCandidates.Count > 0)
        {
            int idx = trimCandidates.Dequeue();
            var old = result[idx];
            int oldChars = old.Contents.Sum(c => EstimateContentChars(c));

            // Rebuild as same-role message with placeholder text per FunctionResultContent,
            // preserving CallId so the message chain stays valid for strict providers.
            var trimmedContents = old.Contents
                .OfType<FunctionResultContent>()
                .Select(fr => (AIContent)new FunctionResultContent(fr.CallId, Placeholder))
                .ToList<AIContent>();

            if (trimmedContents.Count == 0)
                trimmedContents = [new TextContent(Placeholder)];

            result[idx] = new ChatMessage(old.Role, trimmedContents);
            int newChars = result[idx].Contents.Sum(c => EstimateContentChars(c));
            total -= oldChars - newChars;
        }

        // Phase 2: if still over budget because individual retained results are larger than
        // maxChars (e.g. a single read_file of a large file), truncate their content
        // proportionally. Phase 1 cannot help when the last N messages alone exceed the budget.
        if (total > maxChars)
        {
            var remainingToolIndices = new List<int>();
            int nonToolChars = 0;
            for (int i = 0; i < result.Count; i++)
            {
                if (result[i].Role == ChatRole.Tool)
                    remainingToolIndices.Add(i);
                else
                    nonToolChars += result[i].Contents.Sum(c => EstimateContentChars(c));
            }

            if (remainingToolIndices.Count > 0)
            {
                int toolBudget    = Math.Max(maxChars - nonToolChars, 0);
                int perResultMax  = Math.Max(toolBudget / remainingToolIndices.Count, 200);
                const string TruncSuffix = "\n[...truncated — in-turn budget exceeded]";

                foreach (int idx in remainingToolIndices)
                {
                    var old     = result[idx];
                    bool changed = false;
                    var rebuilt  = new List<AIContent>(old.Contents.Count);
                    foreach (var content in old.Contents)
                    {
                        if (content is FunctionResultContent fr &&
                            fr.Result is string s && s.Length > perResultMax)
                        {
                            rebuilt.Add(new FunctionResultContent(
                                fr.CallId!, s[..perResultMax] + TruncSuffix));
                            changed = true;
                        }
                        else
                        {
                            rebuilt.Add(content);
                        }
                    }
                    if (changed)
                        result[idx] = new ChatMessage(old.Role, rebuilt);
                }
            }
        }

        return result;
    }

    // Number of adaptive-trim stages before giving up and propagating the exception.
    // Stage 1: truncate all tool results to 4 000 chars (~1 000 tokens each)
    // Stage 2: truncate to 500 chars — still useful for agent reasoning
    // Stage 3: drop all tool messages entirely (text-only nuclear option)
    private const int AdaptiveContextTrimMaxRetries = 3;

    // Produces a trimmed copy of messages for the given retry stage.
    private static List<ChatMessage> AdaptiveTrimMessages(
        IReadOnlyList<ChatMessage> messages,
        int stage)
    {
        int maxResultChars = stage switch
        {
            1 => 4_000,
            2 =>   500,
            _ =>     0, // stage 3+: nuclear — drop all tool content
        };

        return maxResultChars > 0
            ? TrimToolResultsToChars(messages, maxResultChars)
            : DropAllToolContent(messages);
    }

    // Truncates FunctionResultContent strings in ChatRole.Tool messages.
    // Consumed read_file results (where a later write/patch targeted the same path) are capped
    // at ConsumedReadCapChars regardless of maxChars — their content is stale anyway.
    // All other results are capped at maxChars.
    private const int ConsumedReadCapChars = 500;

    private static List<ChatMessage> TrimToolResultsToChars(
        IReadOnlyList<ChatMessage> messages,
        int maxChars)
    {
        if (!messages.Any(m => m.Role == ChatRole.Tool))
            return messages as List<ChatMessage> ?? messages.ToList();

        var consumedReadIds = ContextWindowFilter.BuildConsumedReadCallIds(messages);

        var result = new List<ChatMessage>(messages.Count);
        foreach (var msg in messages)
        {
            if (msg.Role != ChatRole.Tool) { result.Add(msg); continue; }

            bool changed = false;
            var newContents = new List<AIContent>(msg.Contents.Count);
            foreach (var content in msg.Contents)
            {
                if (content is FunctionResultContent fr && fr.Result is string s)
                {
                    string? replacement = null;

                    if (consumedReadIds.Contains(fr.CallId ?? string.Empty) &&
                        s.Length > ConsumedReadCapChars)
                    {
                        replacement = s[..ConsumedReadCapChars] +
                            $"\n[...{s.Length - ConsumedReadCapChars:N0} chars elided — " +
                            $"file was written or patched later this session; " +
                            $"call read_file again if current content is needed]";
                    }
                    else if (s.Length > maxChars)
                    {
                        replacement = s[..maxChars] +
                            $"\n[...context-trimmed — {s.Length - maxChars:N0} chars removed to fit model limit...]";
                    }

                    if (replacement is not null)
                    {
                        newContents.Add(new FunctionResultContent(fr.CallId!, replacement));
                        changed = true;
                    }
                    else
                    {
                        newContents.Add(content);
                    }
                }
                else
                {
                    newContents.Add(content);
                }
            }
            result.Add(changed ? new ChatMessage(ChatRole.Tool, newContents) : msg);
        }
        return result;
    }

    // Drops all ChatRole.Tool messages and strips FunctionCallContent from assistant messages.
    // Equivalent to ContextWindowConfig.TextOnly filtering — structurally valid for all providers.
    private static List<ChatMessage> DropAllToolContent(IReadOnlyList<ChatMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count);
        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.Tool) continue;

            if (msg.Role == ChatRole.Assistant)
            {
                var textContents = msg.Contents
                    .OfType<TextContent>()
                    .Where(t => !string.IsNullOrEmpty(t.Text))
                    .ToList<AIContent>();
                if (textContents.Count > 0)
                    result.Add(new ChatMessage(ChatRole.Assistant, textContents) { AuthorName = msg.AuthorName });
                continue;
            }

            result.Add(msg);
        }
        return result;
    }

    // Returns true when the exception should trigger an adaptive-trim retry.
    // Covers both our own pre-flight throws and provider-level ContextExceeded signals.
    private static bool IsContextLimitException(Exception ex) =>
        ProviderErrorClassifier.Classify(ex) == FailoverReason.ContextExceeded ||
        (ex is InvalidOperationException &&
         (ex.Message.Contains("Context budget exceeded",   StringComparison.OrdinalIgnoreCase) ||
          ex.Message.Contains("Estimated request payload", StringComparison.OrdinalIgnoreCase)));

    // Proactively trims messages before streaming when explicit limits are configured.
    // Without limits we have no target and skip trimming entirely — the caller sees the error.
    private static IEnumerable<ChatMessage> ProactivelyTrimIfNeeded(
        string agentName,
        IEnumerable<ChatMessage> messages,
        int maxContextChars,
        long maxPayloadBytes,
        int toolSchemaChars,
        ILogger? logger = null)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        for (int stage = 0; stage <= AdaptiveContextTrimMaxRetries; stage++)
        {
            IReadOnlyList<ChatMessage> ctx = stage == 0
                ? list
                : AdaptiveTrimMessages(list, stage);

            int msgChars   = ctx.Sum(m => m.Contents.Sum(EstimateContentChars));
            int totalChars = msgChars + toolSchemaChars;

            bool contextOk = maxContextChars == 0 || totalChars <= maxContextChars;
            bool payloadOk = maxPayloadBytes  == 0 || (long)(totalChars * 1.2) + 2048 <= maxPayloadBytes;

            if (contextOk && payloadOk) return ctx;

            if (stage < AdaptiveContextTrimMaxRetries)
                logger?.LogWarning(
                    "[context-trim] {Agent} streaming pre-trim stage {Stage}: ~{Tokens:N0} tokens — reducing tool results",
                    agentName, stage + 1, totalChars / 4);
        }

        return DropAllToolContent(list);
    }

    private static int EstimateContentChars(AIContent content) => content switch
    {
        TextContent t           => t.Text?.Length ?? 0,
        FunctionResultContent r => r.Result is string s ? s.Length : r.Result?.ToString()?.Length ?? 0,
        FunctionCallContent c   => (c.Name?.Length ?? 0) + (c.Arguments?.Values.Sum(v =>
                                      v is System.Text.Json.JsonElement je ? je.GetRawText().Length
                                      : v?.ToString()?.Length ?? 0) ?? 0),
        _                       => 0,
    };

    /// <summary>
    /// Estimates the token count of <paramref name="messages"/> (plus tool schema overhead)
    /// using a conservative 4-chars-per-token ratio and throws if it exceeds
    /// <paramref name="maxChars"/>. Runs before every inner LLM call so the provider never
    /// sees an oversized request.
    /// </summary>
    private static void EnforceContextBudget(
        string agentName,
        IEnumerable<ChatMessage> messages,
        int maxChars,
        int toolSchemaChars = 0)
    {
        int msgChars = 0;
        foreach (var msg in messages)
            foreach (var content in msg.Contents)
                msgChars += EstimateContentChars(content);

        var totalChars = msgChars + toolSchemaChars;
        if (totalChars <= maxChars) return;

        var estimated     = totalChars / 4;
        var schemaTokens  = toolSchemaChars / 4;
        var limit         = maxChars / 4;
        throw new InvalidOperationException(
            $"[{agentName}] Context budget exceeded: ~{estimated:N0} estimated tokens in this " +
            $"request (includes ~{schemaTokens:N0} tool-schema tokens; MaxContextTokens limit: {limit:N0}). " +
            $"Reduce file read scope, lower ReadFileSizeLimit, or raise MaxContextTokens if the model " +
            $"supports a larger context window.");
    }

    /// <summary>
    /// Estimates the serialized JSON payload size for the outgoing request and throws if it
    /// exceeds <paramref name="maxBytes"/>. Prevents HTTP 413 errors from upstream proxies
    /// (e.g. nginx <c>client_max_body_size</c>) before the round-trip is attempted.
    ///
    /// <para>Estimate: content chars × 1.2 (JSON escaping/structure overhead) + tool schema
    /// chars × 1.1 + 2 KB base overhead for request envelope fields.</para>
    /// </summary>
    private static void EnforcePayloadLimit(
        string agentName,
        IEnumerable<ChatMessage> messages,
        int toolSchemaChars,
        long maxBytes)
    {
        int msgChars = 0;
        foreach (var msg in messages)
            foreach (var content in msg.Contents)
                msgChars += EstimateContentChars(content);

        long estimatedBytes = (long)(msgChars * 1.2) + (long)(toolSchemaChars * 1.1) + 2048;
        if (estimatedBytes <= maxBytes) return;

        throw new InvalidOperationException(
            $"[{agentName}] Estimated request payload ({estimatedBytes / 1024:N0} KB) would exceed " +
            $"MaxPayloadBytes ({maxBytes / 1024:N0} KB). Reduce context size, lower MaxToolResultChars, " +
            $"or increase MaxPayloadBytes if the proxy allows larger bodies.");
    }

    /// <summary>
    /// Estimates the character footprint of all tool schemas passed with this agent's
    /// requests. Computed once at agent build time — tools are fixed for an agent's lifetime.
    /// Uses <c>JsonSchema.GetRawText()</c> for accuracy, matching how the REPL estimates
    /// tool token usage.
    /// </summary>
    private static int EstimateToolSchemaChars(IList<AITool>? tools)
    {
        if (tools is null || tools.Count == 0) return 0;
        int total = 0;
        foreach (var tool in tools)
        {
            if (tool is not AIFunction fn) continue;
            total += fn.Name?.Length ?? 0;
            total += fn.Description?.Length ?? 0;
            try { total += fn.JsonSchema.GetRawText().Length; }
            catch { total += 200; } // fallback if schema serialization fails
        }
        // Add per-tool structural overhead (field names, brackets, quotes).
        total += tools.Count * 50;
        return total;
    }

    /// <summary>
    /// Returns true when the most recently completed tool-call batch (the last assistant
    /// message before the current middleware re-entry) contains a <c>handoff</c> call.
    /// Scans backward, skipping <see cref="ChatRole.Tool"/> result messages, and stops at
    /// the first non-tool role to avoid matching handoff calls from earlier turns.
    /// </summary>
    private static bool HandoffWasInvoked(IEnumerable<ChatMessage> messages)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var msg = list[i];
            if (msg.Role == ChatRole.Tool) continue;
            if (msg.Role == ChatRole.Assistant)
                return msg.Contents.OfType<FunctionCallContent>()
                    .Any(fc => string.Equals(fc.Name, HandoffPlugin.FunctionName,
                        StringComparison.OrdinalIgnoreCase));
            break; // User message = turn boundary; no handoff in this batch.
        }
        return false;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmptyStreamingResponse()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static ChatOptions MergeOptions(
        IEnumerable<ChatMessage> messages,
        ChatOptions? request,
        ChatOptions defaults)
    {
        // ToolMode (e.g. RequireAny) must only fire on the *first* LLM call of a turn —
        // i.e. before any tool has been invoked. Once the context contains a tool-result
        // message the agent is already inside the tool loop, and forcing RequireAny again
        // would prevent it from ever emitting a final text response.
        // This mirrors SK's FunctionChoice.Required semantics.
        var lastRole = messages.LastOrDefault()?.Role;
        var effectiveToolMode = lastRole == ChatRole.Tool ? null : defaults.ToolMode;

        // Tools: prefer what the caller supplied; fall back to the agent's own list stored
        // in defaults. This ensures the tools array is always present in the request when
        // the agent has plugins registered, even if the inner FunctionInvokingChatClient
        // does not populate ChatOptions.Tools itself.
        var mergedTools = request?.Tools ?? defaults.Tools;

        // Only set ToolMode when there are tools to use. Sending tool_choice without a
        // tools array causes Bedrock (via LiteLLM) to reject the request with HTTP 400.
        var mergedToolMode = mergedTools?.Count > 0
            ? (request?.ToolMode ?? effectiveToolMode)
            : null;

        var merged = new ChatOptions
        {
            Temperature     = request?.Temperature     ?? defaults.Temperature,
            MaxOutputTokens = request?.MaxOutputTokens ?? defaults.MaxOutputTokens,
            TopP            = request?.TopP,
            StopSequences   = request?.StopSequences,
            Tools           = mergedTools,
            ToolMode        = mergedToolMode,
        };
        return merged;
    }

    private static ChatOptions? BuildChatOptions(AgentConfig config, ModelConfig resolved, List<AIFunction> tools)
    {
        ChatToolMode toolMode = config.FunctionChoice.ToLowerInvariant() switch
        {
            "required" => ChatToolMode.RequireAny,
            "none"     => ChatToolMode.None,
            _          => ChatToolMode.Auto,
        };

        // Only create options when there is something non-default to configure.
        bool hasToolMode    = toolMode != ChatToolMode.Auto;
        bool hasTemperature = resolved.Temperature is not null;
        bool hasMaxTokens   = resolved.MaxTokens > 0;
        bool hasTools       = tools.Count > 0;

        if (!hasToolMode && !hasTemperature && !hasMaxTokens && !hasTools)
            return null;

        var options = new ChatOptions();

        if (hasTools)
            options.Tools = tools.Cast<AITool>().ToList();

        if (hasTemperature)
            options.Temperature = (float)resolved.Temperature!.Value;

        if (hasMaxTokens)
            options.MaxOutputTokens = resolved.MaxTokens;

        if (hasToolMode)
            options.ToolMode = toolMode;

        return options;
    }
}
