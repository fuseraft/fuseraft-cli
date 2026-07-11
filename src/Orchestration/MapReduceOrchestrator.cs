using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentGovernance;
using AgentGovernance.Sre;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

using fuseraft.Orchestration.Parallel;

// Disambiguate from Microsoft.Agents.AI.AgentFactory
using fuseraft.Infrastructure;
using AgentFactory = fuseraft.Infrastructure.Agents.AgentFactory;

namespace fuseraft.Orchestration;

/// <summary>
/// Map-reduce orchestrator. Activated by <c>Selection.Type: "mapreduce"</c>.
///
/// <para>
/// <b>Phase 1 — Split</b>: the <c>Splitter</c> agent decomposes the task into a JSON array
/// of work items. The orchestrator parses the first JSON object found in the splitter's
/// response and extracts the array at <see cref="MapReduceConfig.ItemsJsonPath"/>.
/// </para>
///
/// <para>
/// <b>Phase 2 — Map</b>: the <c>Mapper</c> agent is invoked once per item, in parallel
/// (bounded by <see cref="MapReduceConfig.MaxConcurrency"/>). Each invocation receives the
/// original task plus a message identifying the specific item to process. Mapper outputs
/// are isolated from each other; each mapper only sees the splitter output and its own item.
/// </para>
///
/// <para>
/// <b>Phase 3 — Reduce</b>: the <c>Reducer</c> agent receives all mapper outputs and
/// synthesises them into a final answer. The reducer sees the full shared history:
/// original task, splitter output, and all mapper outputs.
/// </para>
/// </summary>
public sealed class MapReduceOrchestrator(
    OrchestrationConfig config,
    AgentFactory agentFactory,
    ILogger<MapReduceOrchestrator> logger,
    ChangeTracker? changeTracker = null,
    EventEmitter? eventEmitter = null,
    GovernanceKernel? governanceKernel = null,
    IHumanApprovalService? humanApprovalService = null,
    IContextAssemblyPipeline? contextPipeline = null,
    RepositoryKnowledgeStore? repositoryKnowledgeStore = null) : IOrchestrator
{
    private readonly MapReduceConfig _mrConfig =
        config.Selection.MapReduce ?? new MapReduceConfig();
    private readonly IHumanApprovalService? _humanApprovalService = humanApprovalService;

    private string _sessionId = string.Empty;
    private string _task = string.Empty;

    // IOrchestrator events

    public event Action<string>? AgentStarting;
    public event Action<string, string, string?>? ToolCalling;
    public event Action<string, int, int>? TokenBudgetWarning;

    public void SetSessionId(string sessionId)
    {
        _sessionId = sessionId;
        agentFactory.SetSessionId(sessionId);
        contextPipeline?.SetSessionId(sessionId);
    }

    public async Task<OrchestrationResult> RunAsync(
        string task,
        IReadOnlyList<AgentMessage>? priorHistory = null,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<AgentMessage>();
        var start    = DateTime.UtcNow;

        try
        {
            await foreach (var msg in StreamAsync(task, priorHistory, cancellationToken).ConfigureAwait(false))
                messages.Add(msg);

            return new OrchestrationResult
            {
                SessionId         = _sessionId,
                Succeeded         = true,
                Messages          = messages,
                Duration          = DateTime.UtcNow - start,
                TerminationReason = "Completed"
            };
        }
        catch (BudgetExceededException ex)
        {
            return new OrchestrationResult
            {
                SessionId         = _sessionId,
                Succeeded         = false,
                Messages          = messages,
                Duration          = DateTime.UtcNow - start,
                TerminationReason = "TokenBudgetExceeded",
                ErrorMessage      = ex.Message
            };
        }
        catch (OperationCanceledException)
        {
            return new OrchestrationResult
            {
                SessionId         = _sessionId,
                Succeeded         = false,
                Messages          = messages,
                Duration          = DateTime.UtcNow - start,
                TerminationReason = "Cancelled",
                ErrorMessage      = "Operation was cancelled."
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[MapReduceOrchestrator] Session {SessionId} failed.", _sessionId);
            return new OrchestrationResult
            {
                SessionId         = _sessionId,
                Succeeded         = false,
                Messages          = messages,
                Duration          = DateTime.UtcNow - start,
                TerminationReason = "Error",
                ErrorMessage      = ex.Message
            };
        }
    }

    public async IAsyncEnumerable<AgentMessage> StreamAsync(
        string task,
        IReadOnlyList<AgentMessage>? priorHistory = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _task = task;

        // Build all agents once.
        var agents = config.Agents
            .Select(a => agentFactory.Create(a, onToolCalling: (agent, tool, args) => ToolCalling?.Invoke(agent, tool, args)))
            .ToDictionary(a => a.Name!, StringComparer.OrdinalIgnoreCase);

        var agentInstructions = config.Agents
            .Where(a => !string.IsNullOrWhiteSpace(a.Instructions))
            .ToDictionary(a => a.Name, a => a.Instructions, StringComparer.OrdinalIgnoreCase);

        var agentConfigs = config.Agents.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

        if (!agents.TryGetValue(_mrConfig.Splitter, out var splitter))
            throw new InvalidOperationException(
                $"MapReduce: Splitter agent '{_mrConfig.Splitter}' not found in config.");
        if (!agents.TryGetValue(_mrConfig.Mapper, out var mapper))
            throw new InvalidOperationException(
                $"MapReduce: Mapper agent '{_mrConfig.Mapper}' not found in config.");
        if (!agents.TryGetValue(_mrConfig.Reducer, out var reducer))
            throw new InvalidOperationException(
                $"MapReduce: Reducer agent '{_mrConfig.Reducer}' not found in config.");

        agentInstructions.TryGetValue(_mrConfig.Splitter, out var splitterInstr);
        agentInstructions.TryGetValue(_mrConfig.Mapper,   out var mapperInstr);
        agentInstructions.TryGetValue(_mrConfig.Reducer,  out var reducerInstr);

        int turn             = priorHistory is { Count: > 0 } ? priorHistory[^1].TurnIndex + 1 : 0;
        int cumulativeTokens = priorHistory?.Sum(m => m.Usage?.TotalTokens ?? 0) ?? 0;

        // Shared history grows through all three phases.
        var history = new List<ChatMessage>();
        if (priorHistory?.Count > 0)
        {
            foreach (var prior in priorHistory)
            {
                var role    = prior.Role == MessageRole.User ? ChatRole.User : ChatRole.Assistant;
                var content = prior.Content ?? string.Empty;
                var msg     = new ChatMessage(role, content);
                if (role == ChatRole.Assistant && prior.AgentName is not null)
                    msg.AuthorName = prior.AgentName;
                history.Add(msg);
            }
        }
        history.Add(new ChatMessage(ChatRole.User, task));

        // -----------------------------------------------------------------------
        // Phase 1: Split
        // -----------------------------------------------------------------------

        logger.LogInformation("[MapReduceOrchestrator] Phase 1/3: Split — agent '{Splitter}'.", _mrConfig.Splitter);

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync(EventTypes.PhaseStart, payload: new { phase = 1, agent = _mrConfig.Splitter });

        IReadOnlyList<string>? items = null;
        string splitterOutput        = string.Empty;
        int splitRetries             = 0;

        while (items is null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AgentStarting?.Invoke(splitter.Name ?? _mrConfig.Splitter);
            agentFactory.OnAgentTurnStarting();
            changeTracker?.BeginTurn(splitter.Name ?? _mrConfig.Splitter, turn);

            var splitContext = await AssembleContextAsync(
                splitter.Name ?? _mrConfig.Splitter, splitterInstr, history,
                agentConfigs.GetValueOrDefault(_mrConfig.Splitter), turn, cancellationToken);
            var splitResponse = await InvokeAgentAsync(splitter, splitContext, cancellationToken);
            splitterOutput = splitResponse.Text ?? string.Empty;

            var splitMsg = MakeMessage(
                splitter.Name ?? _mrConfig.Splitter,
                splitterOutput, turn++,
                OrchestratorHelpers.ExtractUsage(splitResponse),
                OrchestratorHelpers.ExtractToolCalls(splitResponse.Messages));

            cumulativeTokens += splitMsg.Usage?.TotalTokens ?? 0;
            FireTokenBudgetWarning(splitMsg);
            yield return splitMsg;

            if (config.MaxTotalTokens is { } cap && cumulativeTokens > cap)
                throw new BudgetExceededException(cumulativeTokens, cap);

            await FlushChangeTrackerAsync(splitMsg);
            await PersistObservationsAsync(splitResponse, splitter.Name ?? _mrConfig.Splitter, splitMsg.TurnIndex);

            history.Add(new ChatMessage(ChatRole.Assistant, splitterOutput)
                { AuthorName = splitter.Name ?? _mrConfig.Splitter });

            items = TryParseItems(splitterOutput, _mrConfig.ItemsJsonPath);
            if (items is null)
            {
                splitRetries++;
                if (splitRetries >= _mrConfig.MaxSplitterRetries)
                    throw new InvalidOperationException(
                        $"MapReduce: Splitter '{_mrConfig.Splitter}' failed to emit a JSON array at " +
                        $"'{_mrConfig.ItemsJsonPath}' after {_mrConfig.MaxSplitterRetries} retries. " +
                        $"Last response: {StringHelpers.Truncate(splitterOutput, 300)}");

                var correction =
                    $"SPLIT FAILED: Your response did not contain a valid JSON object with an array at '{_mrConfig.ItemsJsonPath}'. " +
                    $"Re-emit your answer as a JSON object. Example: " +
                    $"{{ \"{_mrConfig.ItemsJsonPath}\": [\"item 1\", \"item 2\"] }} " +
                    $"(attempt {splitRetries}/{_mrConfig.MaxSplitterRetries})";

                logger.LogWarning(
                    "[MapReduceOrchestrator] Splitter retry {Retry}/{Max}: no JSON array found.",
                    splitRetries, _mrConfig.MaxSplitterRetries);

                history.Add(new ChatMessage(ChatRole.User, correction));
            }
        }

        logger.LogInformation(
            "[MapReduceOrchestrator] Splitter produced {Count} item(s).", items.Count);

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync(EventTypes.PhaseEnd, payload: new { phase = 1, items = items.Count });

        if (items.Count == 0)
        {
            // No items — skip map phase and go straight to reducer with an empty note.
            history.Add(new ChatMessage(ChatRole.User,
                "The splitter produced zero work items. Provide a final answer directly."));
        }
        else
        {
            // -----------------------------------------------------------------------
            // Phase 2: Map (parallel)
            // -----------------------------------------------------------------------

            logger.LogInformation(
                "[MapReduceOrchestrator] Phase 2/3: Map — {Count} item(s), agent '{Mapper}', concurrency={Concurrency}.",
                items.Count, _mrConfig.Mapper, _mrConfig.MaxConcurrency == 0 ? "unlimited" : _mrConfig.MaxConcurrency.ToString());

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync(EventTypes.PhaseStart,
                    payload: new { phase = 2, agent = _mrConfig.Mapper, items = items.Count });

            // Build a semaphore when concurrency is bounded.
            var semaphore = _mrConfig.MaxConcurrency > 0
                ? new SemaphoreSlim(_mrConfig.MaxConcurrency)
                : null;

            // Each mapper gets a fork of the history snapshot (task + splitter output only).
            var historySnapshot = history.ToList();
            int baseTurn        = turn;

            // Run all mapper tasks; collect outputs in order.
            var mapperTasks = items.Select((item, index) => Task.Run(async () =>
            {
                if (semaphore is not null) await semaphore.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    AgentStarting?.Invoke(mapper.Name ?? _mrConfig.Mapper);

                    if (eventEmitter is not null)
                        _ = eventEmitter.EmitAsync(EventTypes.ParallelBranchStart,
                            agent: mapper.Name ?? _mrConfig.Mapper,
                            payload: new { item_index = index, item = StringHelpers.Truncate(item, 120) });

                    var mapHistory = new List<ChatMessage>(historySnapshot)
                    {
                        new(ChatRole.User,
                            $"Process item {index + 1} of {items.Count}:\n\n{item}")
                    };

                    var mapContext = await AssembleContextAsync(
                        mapper.Name ?? _mrConfig.Mapper, mapperInstr, mapHistory,
                        agentConfigs.GetValueOrDefault(_mrConfig.Mapper), baseTurn + index, cancellationToken);
                    var mapResponse = await InvokeAgentAsync(mapper, mapContext, cancellationToken);
                    var mapText     = mapResponse.Text ?? string.Empty;

                    var mapMsg = MakeMessage(
                        mapper.Name ?? _mrConfig.Mapper,
                        mapText, baseTurn + index,
                        OrchestratorHelpers.ExtractUsage(mapResponse),
                        OrchestratorHelpers.ExtractToolCalls(mapResponse.Messages));

                    await PersistObservationsAsync(mapResponse, mapper.Name ?? _mrConfig.Mapper, mapMsg.TurnIndex);

                    if (eventEmitter is not null)
                        _ = eventEmitter.EmitAsync(EventTypes.ParallelBranchEnd,
                            agent: mapper.Name ?? _mrConfig.Mapper,
                            payload: new { item_index = index });

                    return (Index: index, Msg: mapMsg, Text: mapText);
                }
                finally
                {
                    semaphore?.Release();
                }
            }, cancellationToken)).ToList();

            var mapResults = await Task.WhenAll(mapperTasks);

            // Yield mapper messages in item order and merge into shared history.
            foreach (var r in mapResults.OrderBy(r => r.Index))
            {
                cumulativeTokens += r.Msg.Usage?.TotalTokens ?? 0;
                FireTokenBudgetWarning(r.Msg);
                yield return r.Msg;

                if (config.MaxTotalTokens is { } cap2 && cumulativeTokens > cap2)
                    throw new BudgetExceededException(cumulativeTokens, cap2);

                await FlushChangeTrackerAsync(r.Msg);

                history.Add(new ChatMessage(ChatRole.Assistant,
                    $"[Item {r.Index + 1}]: {r.Text}")
                    { AuthorName = mapper.Name ?? _mrConfig.Mapper });
            }

            turn = baseTurn + items.Count;

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync(EventTypes.PhaseEnd,
                    payload: new { phase = 2, mapped = mapResults.Length });
        }

        // -----------------------------------------------------------------------
        // Phase 3: Reduce
        // -----------------------------------------------------------------------

        logger.LogInformation("[MapReduceOrchestrator] Phase 3/3: Reduce — agent '{Reducer}'.", _mrConfig.Reducer);

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync(EventTypes.PhaseStart,
                payload: new { phase = 3, agent = _mrConfig.Reducer });

        history.Add(new ChatMessage(ChatRole.User,
            "All items have been processed. Synthesise the results above into a final, cohesive answer."));

        AgentStarting?.Invoke(reducer.Name ?? _mrConfig.Reducer);
        agentFactory.OnAgentTurnStarting();
        changeTracker?.BeginTurn(reducer.Name ?? _mrConfig.Reducer, turn);

        var reduceContext = await AssembleContextAsync(
            reducer.Name ?? _mrConfig.Reducer, reducerInstr, history,
            agentConfigs.GetValueOrDefault(_mrConfig.Reducer), turn, cancellationToken);
        var reduceResponse = await InvokeAgentAsync(reducer, reduceContext, cancellationToken);
        var reduceText     = reduceResponse.Text ?? string.Empty;

        var reduceMsg = MakeMessage(
            reducer.Name ?? _mrConfig.Reducer,
            reduceText, turn++,
            OrchestratorHelpers.ExtractUsage(reduceResponse),
            OrchestratorHelpers.ExtractToolCalls(reduceResponse.Messages));

        cumulativeTokens += reduceMsg.Usage?.TotalTokens ?? 0;
        FireTokenBudgetWarning(reduceMsg);
        yield return reduceMsg;

        if (config.MaxTotalTokens is { } cap3 && cumulativeTokens > cap3)
            throw new BudgetExceededException(cumulativeTokens, cap3);

        await FlushChangeTrackerAsync(reduceMsg);
        await PersistObservationsAsync(reduceResponse, reducer.Name ?? _mrConfig.Reducer, reduceMsg.TurnIndex);

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync(EventTypes.PhaseEnd, payload: new { phase = 3 });

        logger.LogInformation(
            "[MapReduceOrchestrator] Session {SessionId} complete — {Turn} total turns, {Tokens:N0} tokens.",
            _sessionId, turn, cumulativeTokens);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    // Shared with ScatterGatherOrchestrator (and, except context assembly, AdversarialOrchestrator)
    // via FanOutHelpers — see that class's doc comment for what's shared and why.
    private Task<IEnumerable<ChatMessage>> AssembleContextAsync(
        string agentName, string? instructions, IReadOnlyList<ChatMessage> history,
        AgentConfig? agentCfg, int turn, CancellationToken ct) =>
        FanOutHelpers.AssembleContextAsync(
            contextPipeline, eventEmitter, agentName, _task, instructions, history, agentCfg, _sessionId, turn, ct);

    private Task PersistObservationsAsync(AgentResponse response, string agentName, int turn) =>
        FanOutHelpers.PersistObservationsAsync(repositoryKnowledgeStore, _sessionId, response, agentName, turn);

    private Task<AgentResponse> InvokeAgentAsync(AIAgent agent, IEnumerable<ChatMessage> context, CancellationToken ct) =>
        FanOutHelpers.InvokeAgentAsync(agent, context, governanceKernel, ct);

    private static AgentMessage MakeMessage(
        string agentName, string content, int turn,
        TokenUsage? usage, IReadOnlyList<ToolCallRecord>? toolCalls) =>
        FanOutHelpers.MakeMessage(agentName, content, turn, usage, toolCalls);

    private void FireTokenBudgetWarning(AgentMessage msg) =>
        FanOutHelpers.FireTokenBudgetWarning(
            msg, config.WarnTurnTokens, (a, i, t) => TokenBudgetWarning?.Invoke(a, i, t));

    private Task FlushChangeTrackerAsync(AgentMessage msg) =>
        FanOutHelpers.FlushChangeTrackerAsync(msg, changeTracker, logger, nameof(MapReduceOrchestrator));

    /// <summary>
    /// Searches <paramref name="text"/> for a JSON object containing the array at
    /// <paramref name="jsonPath"/>. On a parse failure the search advances past the
    /// current <c>{</c> so that valid JSON embedded after invalid text is still found.
    /// Returns null when no matching object exists in the text.
    /// </summary>
    private static IReadOnlyList<string>? TryParseItems(string text, string jsonPath)
    {
        int searchFrom = 0;
        while (searchFrom < text.Length)
        {
            int start = text.IndexOf('{', searchFrom);
            if (start < 0) return null;

            int jsonEnd = FindJsonObjectEnd(text, start);
            if (jsonEnd < 0) return null;

            var jsonSlice = text[start..(jsonEnd + 1)];
            try
            {
                using var doc = JsonDocument.Parse(jsonSlice);
                var root  = doc.RootElement;
                var parts = jsonPath.Split('.', StringSplitOptions.RemoveEmptyEntries);

                JsonElement current = root;
                foreach (var part in parts)
                {
                    if (!current.TryGetProperty(part, out current)) return null;
                }

                if (current.ValueKind != JsonValueKind.Array) return null;

                return current.EnumerateArray()
                    .Select(el => el.ValueKind == JsonValueKind.String
                        ? el.GetString() ?? el.GetRawText()
                        : el.GetRawText())
                    .ToList();
            }
            catch (JsonException)
            {
                // Not valid JSON from this position — try the next '{'.
                searchFrom = start + 1;
            }
        }
        return null;
    }

    /// <summary>
    /// Finds the index of the closing <c>}</c> that matches the <c>{</c> at
    /// <paramref name="start"/>, correctly skipping characters inside string literals
    /// (including escaped quotes).
    /// </summary>
    private static int FindJsonObjectEnd(string text, int start)
    {
        int  depth    = 0;
        bool inString = false;
        bool escaped  = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (escaped)              { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true;  continue; }
            if (c == '"')              { inString = !inString; continue; }
            if (inString)              continue;

            if      (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }

        return -1;
    }
}
