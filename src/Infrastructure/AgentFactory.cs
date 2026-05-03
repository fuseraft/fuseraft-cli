using System.Collections.Concurrent;
using A2A;
using AgentGovernance;
using AgentGovernance.Audit;
using AgentGovernance.Hypervisor;
using AgentGovernance.Trust;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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
    ILoggerFactory? loggerFactory = null)
{
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
            var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(remoteCfg.TimeoutSeconds) };
            var resolver   = new A2ACardResolver(new Uri(remoteUrl), httpClient);
            var remoteAgent = resolver.GetAIAgentAsync(httpClient, loggerFactory: loggerFactory)
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

        var hasHandoff = config.Plugins.Any(p =>
            p.Equals(HandoffPlugin.PluginName, StringComparison.OrdinalIgnoreCase));

        // Wrap the chat client when options merging, budget enforcement, or handoff
        // termination is needed.
        var effectiveClient = chatOptions is not null || maxContextChars > 0 || maxInTurnChars > 0 || hasHandoff
            ? chatClient.AsBuilder()
                .Use(
                    getResponseFunc: (messages, options, inner, ct) =>
                    {
                        if (maxInTurnChars > 0)
                            messages = TrimInTurnContext(messages, maxInTurnChars);
                        if (maxContextChars > 0)
                            EnforceContextBudget(config.Name, messages, maxContextChars);
                        // Stop the FunctionInvokingChatClient loop immediately after handoff —
                        // no follow-up LLM call is made, so the agent cannot call more tools.
                        if (hasHandoff && HandoffWasInvoked(messages))
                            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));
                        var merged = chatOptions is not null ? MergeOptions(messages, options, chatOptions) : options;
                        return inner.GetResponseAsync(messages, merged, ct);
                    },
                    getStreamingResponseFunc: (messages, options, inner, ct) =>
                    {
                        if (maxInTurnChars > 0)
                            messages = TrimInTurnContext(messages, maxInTurnChars);
                        if (maxContextChars > 0)
                            EnforceContextBudget(config.Name, messages, maxContextChars);
                        if (hasHandoff && HandoffWasInvoked(messages))
                            return EmptyStreamingResponse();
                        var merged = chatOptions is not null ? MergeOptions(messages, options, chatOptions) : options;
                        return inner.GetStreamingResponseAsync(messages, merged, ct);
                    })
                .Build()
            : chatClient;

        // Pre-configure FunctionInvokingChatClient so ChatClientAgent reuses our instance
        // (it only adds its own when none is present in the pipeline). This lets us set
        // MaximumIterationsPerRequest per agent instead of accepting the framework default (40).
        // We always set this so the limit is explicit and visible, even when using the default.
        var maxIterations = config.MaxToolCallsPerTurn > 0 ? config.MaxToolCallsPerTurn : 40;
        var functionInvokingClient = effectiveClient
            .AsBuilder()
            .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = maxIterations)
            .Build();

        // Construct the base ChatClientAgent with tools and chat options.
        ChatClientAgent baseAgent = new(
            chatClient: functionInvokingClient,
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
            agent = new SandboxEnforcementFilter(securityConfig.FileSystemSandboxPath, governanceKernel?.InjectionDetector, ring)
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

            // "Scratchpad" is per-agent — each agent gets its own file.
            if (pluginName.Equals("Scratchpad", StringComparison.OrdinalIgnoreCase))
            {
                var basePath = scratchpadConfig?.BasePath
                    ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".fuseraft", "scratchpad");
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
                        eventEmitter: eventEmitter,
                        parentAgentName: config.Name));
            }
            // "Chatroom" is per-agent (own sender name) but all agents share the same file.
            else if (pluginName.Equals("Chatroom", StringComparison.OrdinalIgnoreCase))
            {
                var chatPath = chatroomConfig?.Path ?? ".fuseraft/chatroom.jsonl";
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
                    continue; // silently skip unknown plugins in sub-agent list

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
            return await InnerFunction.InvokeAsync(arguments, cancellationToken);
        }
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

        // Replace oldest tool results with a tiny placeholder until we're under the limit.
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

        return result;
    }

    private static int EstimateContentChars(AIContent content) => content switch
    {
        TextContent t           => t.Text?.Length ?? 0,
        FunctionResultContent r => r.Result is string s ? s.Length : r.Result?.ToString()?.Length ?? 0,
        FunctionCallContent c   => (c.Name?.Length ?? 0) + (c.Arguments?.ToString()?.Length ?? 0),
        _                       => 0,
    };

    /// <summary>
    /// Estimates the token count of <paramref name="messages"/> using a conservative
    /// 4-chars-per-token ratio and throws if it exceeds <paramref name="maxChars"/>.
    /// Runs before every inner LLM call so the provider never sees an oversized request.
    /// </summary>
    private static void EnforceContextBudget(
        string agentName,
        IEnumerable<ChatMessage> messages,
        int maxChars)
    {
        int totalChars = 0;
        foreach (var msg in messages)
            foreach (var content in msg.Contents)
                totalChars += EstimateContentChars(content);

        if (totalChars <= maxChars) return;

        var estimated = totalChars / 4;
        var limit     = maxChars / 4;
        throw new InvalidOperationException(
            $"[{agentName}] Context budget exceeded: ~{estimated:N0} estimated tokens in this " +
            $"request (MaxContextTokens limit: {limit:N0}). The agent has accumulated too many " +
            $"tool-call results within this turn. Reduce file read scope, lower ReadFileSizeLimit, " +
            $"or raise MaxContextTokens if the model supports a larger context window.");
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
