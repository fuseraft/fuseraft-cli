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
        var toolSchemaChars = EstimateToolSchemaChars(chatOptions?.Tools);

        var maxPayloadBytes = resolvedModel.MaxPayloadBytes;

        var hasHandoff = config.Plugins.Any(p =>
            p.Equals(HandoffPlugin.PluginName, StringComparison.OrdinalIgnoreCase));

        // Always wrap: the adaptive context-trim retry fires on any provider rejection
        // classified as ContextExceeded, regardless of whether explicit limits are set.
        var effectiveClient = BuildMiddlewareChain(
            chatClient, config, chatOptions,
            maxContextChars, maxInTurnChars, maxInTurnToolPairs,
            toolSchemaChars, maxPayloadBytes, hasHandoff,
            emitter: eventEmitter);

        // Pre-configure FunctionInvokingChatClient and wrap the skills context provider.
        var agentChatClient = BuildEventEmitMiddleware(effectiveClient, config, skillsProvider);

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
        return BuildGovernanceMiddleware(baseAgent, config);
    }

    // Helpers

    /// <summary>
    /// Composes the context-trim and adaptive-retry middleware layer around
    /// <paramref name="chatClient"/>. Handles in-turn deduplication, window trimming,
    /// handoff detection, pre-flight budget/payload enforcement, and ContextExceeded retries
    /// for both non-streaming and streaming paths.
    /// </summary>
    private IChatClient BuildMiddlewareChain(
        IChatClient chatClient,
        AgentConfig config,
        ChatOptions? chatOptions,
        int maxContextChars,
        int maxInTurnChars,
        int maxInTurnToolPairs,
        int toolSchemaChars,
        long maxPayloadBytes,
        bool hasHandoff,
        EventEmitter? emitter = null)
    {
        // Always wrap: the adaptive context-trim retry fires on any provider rejection
        // classified as ContextExceeded, regardless of whether explicit limits are set.
        // Monotonic counter shared across all inner calls for this agent instance.
        // Lets us correlate inner_call_context events with http_reasoning events in the log.
        int innerCallSeq = 0;

        return chatClient.AsBuilder()
            .Use(
                getResponseFunc: async (messages, options, inner, ct) =>
                {
                    // Drop write_file/patch_file pairs superseded by a later write_file to
                    // the same path — the earlier write is never observable and is pure noise.
                    messages = AgentContextCompactionFilters.DropSupersededWritePairs(messages);

                    // Drop observational calls (read_file, grep_file, list_*, get_file_info, etc.)
                    // that are superseded by a later identical call — only the freshest result matters.
                    messages = AgentContextCompactionFilters.DropSupersededObservationalPairs(messages);

                    // Compress shell_run results that are superseded by a later run of the same
                    // command to a single-line outcome. Keeps the call visible (showing the
                    // attempt sequence) while eliminating the verbose output from earlier runs.
                    messages = AgentContextCompactionFilters.CompressSupersededShellPairs(messages);

                    // Strip verbose reasoning text from ALL intermediate tool-calling assistant
                    // messages before the window filter — reasoning from prior calls in the
                    // same turn is never needed again and is the primary cause of the O(N²)
                    // token growth seen with grok-build and other reasoning-heavy models.
                    messages = AgentContextCompactionFilters.TruncateIntermediateAssistantReasoning(messages);

                    if (maxInTurnToolPairs > 0)
                        messages = await AgentContextCompactionFilters.KeepLastToolPairs(messages, maxInTurnToolPairs, ct);

                    if (maxInTurnChars > 0)
                        messages = AgentContextCompactionFilters.TrimInTurnContext(messages, maxInTurnChars);

                    // Stop the FunctionInvokingChatClient loop immediately after handoff —
                    // no follow-up LLM call is made, so the agent cannot call more tools.
                    if (hasHandoff && HandoffWasInvoked(messages))
                        return new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty));

                    var merged  = chatOptions is not null ? MergeOptions(messages, options, chatOptions) : options;
                    var baseMsg = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

                    // Probe 3: emit a per-inner-call context snapshot after all trimming.
                    // Captures the exact content-type breakdown the provider will receive,
                    // making it possible to identify which content type drives token growth.
                    // Set the ambient call-seq so RawReasoningCaptureHandler can echo it into
                    // http_reasoning — enabling per-call correlation of estimated vs actual tokens.
                    // Sub-agent HTTP calls naturally see null here (they run in FunctionInvokingChatClient's
                    // execution context, captured before this middleware ran, so the value never flows to them).
                    var callSeq = Interlocked.Increment(ref innerCallSeq);
                    InnerCallId.Current.Value = callSeq;
                    if (emitter is not null)
                        _ = emitter.EmitAsync(EventTypes.InnerCallContext,
                            agent: config.Name, turn: null,
                            payload: BuildInnerCallContextPayload(
                                baseMsg, toolSchemaChars, callSeq));

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

                            if (emitter is not null)
                                _ = emitter.EmitAsync(EventTypes.ModelCall,
                                    agent: config.Name, turn: null,
                                    payload: new
                                    {
                                        model         = config.Model.ModelId,
                                        attempt,
                                        message_count = baseMsg.Count,
                                        call_seq      = callSeq,
                                    });

                            var response = await inner.GetResponseAsync(ctx, merged, ct);

                            if (emitter is not null)
                                _ = emitter.EmitAsync(EventTypes.ModelResponse,
                                    agent: config.Name, turn: null,
                                    payload: new
                                    {
                                        model         = config.Model.ModelId,
                                        finish_reason = response.FinishReason?.Value,
                                        input_tokens  = response.Usage?.InputTokenCount,
                                        output_tokens = response.Usage?.OutputTokenCount,
                                        call_seq      = callSeq,
                                    });

                            return response;
                        }
                        catch (Exception ex) when (attempt < AdaptiveContextTrimMaxRetries
                                                   && IsContextLimitException(ex))
                        {
                            _logger.LogWarning(
                                "[context-trim] {Agent} stage {Stage}/{Max}: {Error} — reducing tool results and retrying",
                                config.Name, attempt + 1, AdaptiveContextTrimMaxRetries,
                                ex.Message[..Math.Min(ex.Message.Length, 120)].Replace('\n', ' '));
                        }
                        catch (TimeoutException tex)
                        {
                            if (emitter is not null)
                                _ = emitter.EmitAsync(EventTypes.ModelTimeout,
                                    agent: config.Name, turn: null,
                                    payload: new
                                    {
                                        model    = config.Model.ModelId,
                                        attempt,
                                        call_seq = callSeq,
                                        message  = tex.Message[..Math.Min(tex.Message.Length, 200)],
                                    });
                            throw;
                        }
                        catch (Exception ex)
                        {
                            if (emitter is not null)
                                _ = emitter.EmitAsync(EventTypes.ModelError,
                                    agent: config.Name, turn: null,
                                    payload: new
                                    {
                                        model    = config.Model.ModelId,
                                        attempt,
                                        call_seq = callSeq,
                                        error    = ex.Message[..Math.Min(ex.Message.Length, 200)],
                                    });
                            throw;
                        }
                    }
                },
                getStreamingResponseFunc: (messages, options, inner, ct) =>
                    StreamWithToolPairWindowAsync(messages, options, inner, ct))
            .Build();

        // KeepLastToolPairs is async (it delegates to MAF's ToolResultCompactionStrategy),
        // so the streaming path — unlike getResponseFunc above, which is already async —
        // needs to be its own async iterator rather than a synchronous lambda that returns
        // inner.GetStreamingResponseAsync(...) directly.
        async IAsyncEnumerable<ChatResponseUpdate> StreamWithToolPairWindowAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            IChatClient inner,
            [EnumeratorCancellation] CancellationToken ct)
        {
            messages = AgentContextCompactionFilters.DropSupersededWritePairs(messages);
            messages = AgentContextCompactionFilters.DropSupersededObservationalPairs(messages);
            messages = AgentContextCompactionFilters.CompressSupersededShellPairs(messages);
            messages = AgentContextCompactionFilters.TruncateIntermediateAssistantReasoning(messages);

            if (maxInTurnToolPairs > 0)
                messages = await AgentContextCompactionFilters.KeepLastToolPairs(messages, maxInTurnToolPairs, ct);

            if (maxInTurnChars > 0)
                messages = AgentContextCompactionFilters.TrimInTurnContext(messages, maxInTurnChars);
            if (hasHandoff && HandoffWasInvoked(messages))
                yield break;

            var merged = chatOptions is not null ? MergeOptions(messages, options, chatOptions) : options;

            // Cannot retry mid-stream — pre-trim proactively when limits are known.
            // Without configured limits we have no target, so trimming is skipped and
            // a provider rejection surfaces as a normal error for the user to see.
            if (maxContextChars > 0 || maxPayloadBytes > 0)
                messages = ProactivelyTrimIfNeeded(
                    config.Name, messages, maxContextChars, maxPayloadBytes, toolSchemaChars, _logger);

            if (emitter is not null)
                _ = emitter.EmitAsync(EventTypes.ModelCall,
                    agent: config.Name, turn: null,
                    payload: new { model = config.Model.ModelId, streaming = true });

            await foreach (var update in inner.GetStreamingResponseAsync(messages, merged, ct))
                yield return update;
        }
    }

    /// <summary>
    /// Wraps <paramref name="effectiveClient"/> with a <see cref="FunctionInvokingChatClient"/>
    /// (capped at <see cref="AgentConfig.MaxToolCallsPerTurn"/> iterations) and, when a
    /// <see cref="AgentSkillsProvider"/> is present, an outer AIContextProvider layer so
    /// skill tools are visible to the function-invoker.
    /// </summary>
    private static IChatClient BuildEventEmitMiddleware(
        IChatClient effectiveClient,
        AgentConfig config,
        AgentSkillsProvider? skillsProvider)
    {
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

        return agentChatClient;
    }

    /// <summary>
    /// Applies the governance middleware ring: wraps <paramref name="baseAgent"/> with
    /// <see cref="ChangeTracker"/> (outermost, for full auditability) and then with
    /// <see cref="SandboxEnforcementFilter"/> when a filesystem sandbox is configured.
    /// </summary>
    private AIAgent BuildGovernanceMiddleware(AIAgent baseAgent, AgentConfig config)
    {
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

        return agent;
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

            int msgChars   = ctx.Sum(m => m.Contents.Sum(AgentContextCompactionFilters.EstimateContentChars));
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

    /// <summary>
    /// Builds the payload for an <c>inner_call_context</c> event — a per-inner-API-call
    /// snapshot of the message list after all trimming. Emitted before every
    /// <c>inner.GetResponseAsync</c> call so growth across rounds is directly observable.
    /// </summary>
    private static object BuildInnerCallContextPayload(
        IReadOnlyList<ChatMessage> messages, int toolSchemaChars, int seq)
    {
        int userMsgs = 0, assistantMsgs = 0, toolMsgs = 0;
        int textChars = 0, reasoningTextChars = 0, reasoningProtectedDataChars = 0;
        int fnCallArgChars = 0, fnResultChars = 0;
        int protectedDataBlobs = 0;

        foreach (var msg in messages)
        {
            if      (msg.Role == ChatRole.User)      userMsgs++;
            else if (msg.Role == ChatRole.Assistant) assistantMsgs++;
            else if (msg.Role == ChatRole.Tool)      toolMsgs++;

            foreach (var content in msg.Contents)
            {
                switch (content)
                {
                    case TextContent tc:
                        textChars += tc.Text?.Length ?? 0;
                        break;
                    case TextReasoningContent trc:
                        reasoningTextChars += trc.Text?.Length ?? 0;
                        var pdLen = trc.ProtectedData?.Length ?? 0;
                        reasoningProtectedDataChars += pdLen;
                        if (pdLen > 0) protectedDataBlobs++;
                        break;
                    case FunctionCallContent fc:
                        fnCallArgChars += fc.Arguments?.Values.Sum(v =>
                            v is System.Text.Json.JsonElement je
                                ? je.GetRawText().Length
                                : v?.ToString()?.Length ?? 0) ?? 0;
                        break;
                    case FunctionResultContent fr:
                        fnResultChars += fr.Result is string s ? s.Length : fr.Result?.ToString()?.Length ?? 0;
                        break;
                }
            }
        }

        int contentTotal = textChars + reasoningTextChars + reasoningProtectedDataChars
                         + fnCallArgChars + fnResultChars;
        int grandTotal   = contentTotal + toolSchemaChars;

        return new
        {
            seq,
            msg_counts = new { user = userMsgs, assistant = assistantMsgs, tool = toolMsgs },
            content_chars = new
            {
                text                       = textChars,
                reasoning_text             = reasoningTextChars,
                reasoning_protected_data   = reasoningProtectedDataChars,
                fn_call_args               = fnCallArgChars,
                fn_results                 = fnResultChars,
                content_total              = contentTotal,
                tool_schema_est            = toolSchemaChars,
                grand_total                = grandTotal,
            },
            protected_data_blobs = protectedDataBlobs,
            est_tokens           = grandTotal / 4,
        };
    }

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
                msgChars += AgentContextCompactionFilters.EstimateContentChars(content);

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
                msgChars += AgentContextCompactionFilters.EstimateContentChars(content);

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
