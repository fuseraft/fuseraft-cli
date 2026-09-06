using System.Runtime.CompilerServices;
using AgentGovernance;
using AgentGovernance.Hypervisor;
using AgentGovernance.Trust;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Infrastructure.Agents;

/// <summary>
/// Composes the chat-client middleware chain (in-turn compaction, adaptive context-trim retry,
/// pre-flight budget/payload enforcement, telemetry events) and the governance middleware ring
/// (<see cref="ChangeTracker"/>/<see cref="SandboxEnforcementFilter"/>) around a constructed
/// agent. Extracted from <see cref="AgentFactory"/> — single-caller-only from <c>Create</c>,
/// built on top of <see cref="AgentContextCompactionFilters"/> for the always-on per-turn
/// filter pipeline.
/// </summary>
internal sealed class AgentMiddlewareBuilder(
    ILogger logger,
    ChangeTracker? changeTracker,
    SecurityConfig? securityConfig,
    GovernanceKernel? governanceKernel,
    AdaptiveTrimTracker? adaptiveTrimTracker = null)
{
    /// <summary>
    /// Composes the context-trim and adaptive-retry middleware layer around
    /// <paramref name="chatClient"/>. Handles in-turn deduplication, window trimming,
    /// handoff detection, pre-flight budget/payload enforcement, and ContextExceeded retries
    /// for both non-streaming and streaming paths.
    /// </summary>
    public IChatClient BuildMiddlewareChain(
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
                    // Drop superseded writes/reads/shells, truncate intermediate reasoning, then
                    // cap the sliding tool-pair window and char budget — see
                    // AgentContextCompactionFilters.ApplyInTurnFilters for the full rationale.
                    messages = await AgentContextCompactionFilters.ApplyInTurnFilters(
                        messages, maxInTurnToolPairs, maxInTurnChars, ct);

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
                            logger.LogWarning(
                                "[context-trim] {Agent} stage {Stage}/{Max}: {Error} — reducing tool results and retrying",
                                config.Name, attempt + 1, AdaptiveContextTrimMaxRetries,
                                ex.Message[..Math.Min(ex.Message.Length, 120)].Replace('\n', ' '));
                            // Surviving this call by truncating content doesn't shrink the
                            // persisted history — flag it so CompactionCoordinator forces a
                            // real compaction before the next turn hits the same wall.
                            adaptiveTrimTracker?.RecordTrim(config.Name);
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
            messages = await AgentContextCompactionFilters.ApplyInTurnFilters(
                messages, maxInTurnToolPairs, maxInTurnChars, ct);
            if (hasHandoff && HandoffWasInvoked(messages))
                yield break;

            var merged = chatOptions is not null ? MergeOptions(messages, options, chatOptions) : options;

            // Pre-trim proactively when limits are known — cheap and always safe up front.
            if (maxContextChars > 0 || maxPayloadBytes > 0)
                messages = ProactivelyTrimIfNeeded(
                    config.Name, messages, maxContextChars, maxPayloadBytes, toolSchemaChars, logger);

            if (emitter is not null)
                _ = emitter.EmitAsync(EventTypes.ModelCall,
                    agent: config.Name, turn: null,
                    payload: new { model = config.Model.ModelId, streaming = true });

            var baseMsg = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

            // Reactive adaptive-trim retry — same stages as the non-streaming path above,
            // but only viable before the first update reaches the caller. A context-limit
            // rejection is a request-validation failure the provider raises before emitting
            // any tokens, so it always surfaces on the *first* MoveNextAsync — once any
            // update has already been yielded (and displayed/consumed), a later mid-stream
            // failure can no longer be retried without producing garbled duplicate output,
            // so it propagates as a normal error instead, same as the non-streaming path
            // once its own retries are exhausted.
            for (int attempt = 0; ; attempt++)
            {
                var ctxMsgs = attempt == 0 ? (IEnumerable<ChatMessage>)baseMsg : AdaptiveTrimMessages(baseMsg, attempt);
                var enumerator = inner.GetStreamingResponseAsync(ctxMsgs, merged, ct).GetAsyncEnumerator(ct);
                try
                {
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync();
                    }
                    catch (Exception ex) when (attempt < AdaptiveContextTrimMaxRetries && IsContextLimitException(ex))
                    {
                        logger.LogWarning(
                            "[context-trim] {Agent} stage {Stage}/{Max} (streaming): {Error} — reducing tool results and retrying",
                            config.Name, attempt + 1, AdaptiveContextTrimMaxRetries,
                            ex.Message[..Math.Min(ex.Message.Length, 120)].Replace('\n', ' '));
                        // Same reasoning as the non-streaming path: surviving via truncation
                        // doesn't shrink the persisted history, so flag it for a forced
                        // real compaction before the next turn.
                        adaptiveTrimTracker?.RecordTrim(config.Name);
                        continue;
                    }

                    if (!moved) yield break;
                    yield return enumerator.Current;

                    while (await enumerator.MoveNextAsync())
                        yield return enumerator.Current;
                    yield break;
                }
                finally
                {
                    await enumerator.DisposeAsync();
                }
            }
        }
    }

    /// <summary>
    /// Wraps <paramref name="effectiveClient"/> with a <see cref="FunctionInvokingChatClient"/>
    /// (capped at <see cref="AgentConfig.MaxToolCallsPerTurn"/> iterations) and, when a
    /// <see cref="AgentSkillsProvider"/> is present, an outer AIContextProvider layer so
    /// skill tools are visible to the function-invoker.
    /// </summary>
    public static IChatClient BuildEventEmitMiddleware(
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
    public AIAgent BuildGovernanceMiddleware(AIAgent baseAgent, AgentConfig config)
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
    internal static List<ChatMessage> AdaptiveTrimMessages(
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
                if (content is FunctionResultContent fr && ExtractResultText(fr.Result) is { } s)
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

    // FunctionResultContent.Result is object? — a plain string only when the framework kept the
    // raw CLR return value. It commonly arrives as a JsonElement instead (e.g. after any JSON
    // round-trip, such as checkpoint persistence), which `is string` misses entirely, silently
    // turning stages 1–2 of adaptive trim into no-ops (only stage 3's unconditional drop still
    // worked). Mirrors the fallback AgentContextCompactionFilters.EstimateContentChars already
    // uses to *measure* this same content correctly — this applies it when *truncating* too.
    private static string? ExtractResultText(object? resultValue) => resultValue switch
    {
        null => null,
        string s => s,
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } je => je.GetString(),
        _ => resultValue.ToString(),
    };

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
                    agentName, stage + 1, TokenEstimator.EstimateTokens(totalChars));
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
            est_tokens           = TokenEstimator.EstimateTokens(grandTotal),
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

        var estimated     = TokenEstimator.EstimateTokens(totalChars);
        var schemaTokens  = TokenEstimator.EstimateTokens(toolSchemaChars);
        var limit         = TokenEstimator.EstimateTokens(maxChars);
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
    public static int EstimateToolSchemaChars(IList<AITool>? tools)
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

    public static ChatOptions? BuildChatOptions(AgentConfig config, ModelConfig resolved, List<AIFunction> tools)
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
