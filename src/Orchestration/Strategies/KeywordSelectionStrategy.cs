using AgentGovernance;
using AgentGovernance.Audit;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core.Exceptions;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration;
using fuseraft.Orchestration.Failure;

namespace fuseraft.Orchestration.Strategies;

/// <summary>
/// Deterministic keyword-based agent selection strategy with optional per-route
/// validation middleware.
///
/// Scans agent text responses (newest-first) for keyword substrings and routes
/// to the corresponding agent. First match wins. Falls back to <see cref="_defaultAgentName"/>
/// when no keyword matches.
///
/// When a route has an <see cref="IRoutingValidator"/> attached, the validator runs
/// before the route fires. If validation fails:
/// <list type="bullet">
///   <item>The error message is injected into the shared history as a user message so the
///       source agent sees it on its next invocation.</item>
///   <item>The source agent (the one that emitted the handoff keyword) is returned so
///       it is re-invoked and can correct the problem.</item>
/// </list>
///
/// Routes may restrict which agents are permitted to fire them via
/// <c>SourceAgents</c>. When set, a route only matches if the message containing
/// the keyword was authored by one of the listed agents.
/// </summary>
public sealed class KeywordSelectionStrategy : IAgentSelector
{
    private readonly IReadOnlyList<RouteEntry> _routes;
    private readonly string _defaultAgentName;
    private readonly EventEmitter? _eventEmitter;
    private readonly ILogger<KeywordSelectionStrategy> _logger;
    private readonly GovernanceKernel? _governance;
    private readonly IHumanApprovalService? _humanApprovalService;
    private readonly FailureHandlingConfig _failureHandling;
    private IList<ChatMessage>? _history;
    private string _sessionId = "unknown";
    private Func<string, string>? _didResolver;

    // How many agent text messages to look back through when scanning for routing keywords.
    private const int AgentMessageLookback = 3;

    // Inject a loop-warning message when the same agent has been invoked this many
    // consecutive turns without completing its task.
    private const int ConsecutiveTurnWarningThreshold = 5;

    // After this many consecutive JSON parse failures on a PreferStructuredOutput route,
    // stop injecting corrections and fall back to keyword matching.
    private const int MaxStructuredParseRetries = 2;

    // Phrases that indicate the agent believes its tools are unavailable (hallucination).
    private static readonly string[] ToolRefusalPhrases =
    [
        "tool use is disabled",
        "tools are disabled",
        "tool use disabled",
        "tool calling is disabled",
        "tool access is disabled",
        "cannot call",
        "can't call",
        "no tool access",
        "tools are not available",
        "tools are unavailable",
        "tool use is not available",
        "when tools available",
        "when tools are available",
        "once tools are available",
        "next steps when tools",
        "implement without tools",
        "implement this without tools",
        "without tools using",
        "re-enable tool",
        "re-enable tools",
        "enable tool use",
        "would run",
        "would write",
        "need the tools",
        "blocked by tool",
    ];

    /// <summary>A record bundling route configuration.</summary>
    public sealed record RouteEntry(
        string Keyword,
        string AgentName,
        IReadOnlyList<IRoutingValidator> Validators,
        IReadOnlyList<string>? SourceAgents,
        IReadOnlyList<string>? ValidatorNames = null,
        bool RequireHumanApproval = false,
        StructuredCondition? Condition = null,
        string? RecoveryAgent = null,
        bool PreferStructuredOutput = false);

    // Tracks consecutive validator failures keyed by "{sourceAgentName}::{routeIndex}".
    private (string Key, int Count, string LastError)? _validatorFailure;

    // Tracks which agent/route pairs have already had their recovery agent activated this
    // session. Recovery fires at most once per pair to prevent infinite recovery loops.
    private readonly HashSet<string> _recoveryActivated = new(StringComparer.OrdinalIgnoreCase);

    // Counts consecutive JSON parse failures keyed by "{agentName}::{routeIndex}" for
    // PreferStructuredOutput routes. Cleared on a successful parse or when the keyword
    // fallback threshold is reached.
    private readonly Dictionary<string, int> _structuredParseFailures = new(StringComparer.OrdinalIgnoreCase);

    public KeywordSelectionStrategy(
        IReadOnlyList<RouteEntry> routes,
        string defaultAgentName,
        EventEmitter? eventEmitter = null,
        ILogger<KeywordSelectionStrategy>? logger = null,
        GovernanceKernel? governanceKernel = null,
        IHumanApprovalService? humanApprovalService = null,
        FailureHandlingConfig? failureHandling = null)
    {
        _routes = routes;
        _defaultAgentName = defaultAgentName;
        _eventEmitter = eventEmitter;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<KeywordSelectionStrategy>.Instance;
        _governance = governanceKernel;
        _humanApprovalService = humanApprovalService;
        _failureHandling = failureHandling ?? new FailureHandlingConfig();
    }

    /// <summary>
    /// Provides the shared history reference used to inject correction messages when
    /// a routing validator blocks a handoff or a loop warning fires.
    /// Must be called before the orchestration loop begins.
    /// </summary>
    public void SetHistory(IList<ChatMessage> history) => _history = history;

    /// <summary>Stamps all governance audit events with this session ID for correlation.</summary>
    public void SetSessionId(string sessionId) => _sessionId = sessionId;

    /// <summary>
    /// Provides a function that resolves an agent name to its DID for audit correlation.
    /// Falls back to the bare name when the resolver returns null or is not set.
    /// </summary>
    public void SetDidResolver(Func<string, string> resolver) => _didResolver = resolver;

    public async Task<AIAgent?> SelectAsync(
        IReadOnlyList<AIAgent> agents,
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        // Scan agent text messages newest-first, skipping tool call/result messages.
        // Only Role=Assistant messages with text count toward the AgentMessageLookback limit.
        // User messages (error injections, turn-boundary markers) are scanned for
        // keywords but do not consume a lookback slot.
        int scanned = 0;
        _logger.LogDebug(
            "[Selection] Scanning history ({Count} messages) for route keywords (lookback={Lookback})",
            history.Count, AgentMessageLookback);

        for (int i = history.Count - 1; i >= 0 && scanned < AgentMessageLookback; i--)
        {
            var msg = history[i];

            if (msg.Role == ChatRole.Tool) continue;

            // For assistant messages: extract a HandoffPlugin tool call argument when present.
            // The typed argument takes priority over free-text keyword detection — it is used
            // as the effective content for route matching so all existing routing logic (source
            // agents, validators, HITL, turn-boundary) applies unchanged.
            string? toolKeyword = null;
            if (msg.Role == ChatRole.Assistant)
            {
                foreach (var item in msg.Contents)
                {
                    if (item is FunctionCallContent fc
                        && string.Equals(fc.Name, HandoffPlugin.FunctionName, StringComparison.OrdinalIgnoreCase)
                        && fc.Arguments?.TryGetValue(HandoffPlugin.ArgumentName, out var kwObj) == true
                        && kwObj?.ToString() is { Length: > 0 } kw)
                    {
                        toolKeyword = kw;
                        break;
                    }
                }
            }

            var content = toolKeyword ?? msg.Text;
            if (string.IsNullOrEmpty(content)) continue;

            // Only count agent (assistant) messages toward the lookback window.
            if (msg.Role == ChatRole.Assistant) scanned++;

            _logger.LogDebug(
                "[Selection]   i={Index} role={Role} author='{Author}' scanned={Scanned} {Source}content='{Preview}'",
                i, msg.Role.Value, msg.AuthorName ?? "(null)", scanned,
                toolKeyword is not null ? "[tool-call] " : "",
                content.Length > 80 ? content[..80].Replace('\n', ' ') + "…" : content.Replace('\n', ' '));

            foreach (var route in _routes)
            {
                // Tool call argument: exact case-insensitive equality (the model provided the
                // keyword as a typed value — no line-boundary matching needed).
                // Free text: strict per-line match (keyword must appear alone on its own line).
                bool keywordPresent = toolKeyword is not null
                    ? string.Equals(toolKeyword, route.Keyword, StringComparison.OrdinalIgnoreCase)
                    : IsKeywordOnOwnLine(content, route.Keyword);

                // Keyword gate: standard routes require the keyword; PreferStructuredOutput routes
                // can match via JSON condition alone, with the keyword as a fallback.
                if (!keywordPresent && !(route.PreferStructuredOutput && route.Condition is not null))
                    continue;

                _logger.LogDebug(
                    "[Selection]     {MatchType} matched → target='{Target}'",
                    keywordPresent ? $"Keyword '{route.Keyword}'" : "Structured-output condition",
                    route.AgentName);

                // Skip stale keywords — if a turn-boundary marker ("[fuseraft: X → target]")
                // already exists after this message, the keyword was consumed in a prior turn
                // and must not re-fire (prevents ghost-keyword re-invocations when an agent
                // finishes a turn with empty text and the selector scans back past the boundary).
                if (keywordPresent && RouteAlreadyFired(history, i, route.AgentName))
                {
                    _logger.LogDebug(
                        "[Selection]     Keyword already consumed (turn boundary found after index {Idx}) — skipping",
                        i);
                    continue;
                }

                // Source-agent restriction: if the route limits which agents may fire it,
                // skip this route when the message author is not in the permitted list.
                if (route.SourceAgents is { Count: > 0 })
                {
                    var author = msg.AuthorName;
                    bool sourceOk = !string.IsNullOrEmpty(author) &&
                        route.SourceAgents.Any(s =>
                            string.Equals(s, author, StringComparison.OrdinalIgnoreCase));

                    _logger.LogDebug(
                        "[Selection]     SourceAgents check: required=[{Required}] actual='{Author}' → {Result}",
                        string.Join(",", route.SourceAgents), author ?? "(null)",
                        sourceOk ? "PASS" : "FAIL — skipping route");

                    if (!sourceOk) continue;
                }

                // Condition check: evaluate the route's JSON predicate.
                //
                // Standard routes (PreferStructuredOutput=false): parse failure silently skips
                // the route. Multiple routes may share the same keyword with different conditions,
                // so a non-match is not an agent error — just try the next route.
                //
                // PreferStructuredOutput routes: parse failure IS an agent error when the keyword
                // is not present, because these routes expect JSON as the primary response format.
                // Inject a correction and re-invoke the source agent (up to MaxStructuredParseRetries).
                // After that, fall through to keyword matching as a safety fallback.
                if (route.Condition is not null)
                {
                    var routeIdx = 0;
                    for (int ri = 0; ri < _routes.Count; ri++) { if (ReferenceEquals(_routes[ri], route)) { routeIdx = ri; break; } }
                    var parseKey = $"{msg.AuthorName ?? "unknown"}::{routeIdx}";

                    if (!StructuredConditionEvaluator.TryExtractJson(content, out var condDoc) || condDoc is null)
                    {
                        if (route.PreferStructuredOutput && !keywordPresent && _history is not null)
                        {
                            var parseCount = _structuredParseFailures.TryGetValue(parseKey, out var pc) ? pc + 1 : 1;

                            if (parseCount <= MaxStructuredParseRetries)
                            {
                                _structuredParseFailures[parseKey] = parseCount;
                                _history.Add(new ChatMessage(ChatRole.User,
                                    $"STRUCTURED OUTPUT ({parseCount}/{MaxStructuredParseRetries}): " +
                                    $"Respond with a JSON object only — no fences, no prose. " +
                                    $"Field \"{route.Condition.Field}\" must be {DescribeCondition(route.Condition)}.\n" +
                                    $"Example: {{\"{ route.Condition.Field}\": \"<value>\"}}"));

                                _logger.LogDebug(
                                    "[Selection]     PreferStructuredOutput: response is not JSON — injecting correction (attempt {Count}/{Max})",
                                    parseCount, MaxStructuredParseRetries);

                                return FindLastSpeakingAgent(history, agents)
                                       ?? agents.FirstOrDefault(a => string.Equals(
                                           a.Name, _defaultAgentName, StringComparison.OrdinalIgnoreCase))
                                       ?? agents[0];
                            }

                            // Retries exhausted — fall through to keyword scan (do not inject again).
                            _structuredParseFailures.Remove(parseKey);
                            _logger.LogDebug(
                                "[Selection]     PreferStructuredOutput: parse retries exhausted — falling back to keyword matching");
                        }
                        else
                        {
                            _logger.LogDebug(
                                "[Selection]     Condition on route→'{Target}' skipped: response is not valid JSON",
                                route.AgentName);
                        }
                        continue;
                    }

                    // JSON parsed — clear any accumulated parse failure count for this route.
                    _structuredParseFailures.Remove(parseKey);

                    bool condMet;
                    using (condDoc)
                    {
                        condMet = StructuredConditionEvaluator.EvaluateCondition(condDoc.RootElement, route.Condition);
                    }

                    _logger.LogDebug(
                        "[Selection]     Condition Field='{Field}' on route→'{Target}': {Result}",
                        route.Condition.Field, route.AgentName, condMet ? "MATCH" : "no match — skipping route");

                    if (!condMet) continue;
                }

                var matched = agents.FirstOrDefault(
                    a => string.Equals(a.Name, route.AgentName, StringComparison.OrdinalIgnoreCase));
                if (matched is null)
                {
                    _logger.LogDebug(
                        "[Selection]     Target agent '{Target}' not found in agent pool — skipping",
                        route.AgentName);
                    continue;
                }

                // Run all validators for this route (AND semantics).
                if (route.Validators.Count > 0)
                {
                    var routeIndex = 0;
                    for (int ri = 0; ri < _routes.Count; ri++) { if (ReferenceEquals(_routes[ri], route)) { routeIndex = ri; break; } }
                    var failureKey = $"{msg.AuthorName ?? "unknown"}::{routeIndex}";

                    string? firstError = null;
                    string? failingValidatorName = null;
                    for (int vi = 0; vi < route.Validators.Count; vi++)
                    {
                        var vName = route.ValidatorNames is not null && vi < route.ValidatorNames.Count
                            ? route.ValidatorNames[vi]
                            : route.Validators[vi].GetType().Name;
                        _logger.LogDebug(
                            "[Selection]     Running validator [{Vi}/{Count}]: {ValidatorName}",
                            vi + 1, route.Validators.Count, vName);

                        var result = await route.Validators[vi].ValidateAsync(history, cancellationToken);

                        _logger.LogDebug(
                            "[Selection]     Validator {ValidatorName} → {Result}{Error}",
                            vName, result.IsValid ? "PASS" : "FAIL",
                            result.IsValid ? "" : $": {(result.ErrorMessage?.Length > 120 ? result.ErrorMessage[..120] + "…" : result.ErrorMessage)}");

                        if (!result.IsValid)
                        {
                            firstError = result.ErrorMessage ?? string.Empty;
                            failingValidatorName = vName;
                            break;
                        }
                    }

                    if (firstError is not null)
                    {
                        // Track consecutive failures for this agent/route pair.
                        var newCount = _validatorFailure?.Key == failureKey
                            ? _validatorFailure.Value.Count + 1
                            : 1;
                        _validatorFailure = (failureKey, newCount, firstError);

                        if (_eventEmitter is not null)
                            _ = _eventEmitter.EmitAsync("validation_fail",
                                agent: msg.AuthorName,
                                payload: new { validator = failingValidatorName, consecutive = newCount });

                        // Emit to governance audit chain and check rate limit.
                        if (_governance is not null)
                        {
                            var agentName = msg.AuthorName ?? _defaultAgentName;
                            var agentDid  = _didResolver?.Invoke(agentName) ?? agentName;

                            _governance.AuditEmitter.Emit(
                                GovernanceEventType.PolicyViolation,
                                agentId:   agentDid,
                                sessionId: _sessionId,
                                data: new Dictionary<string, object>
                                {
                                    ["agent_name"]  = agentName,
                                    ["validator"]   = failingValidatorName ?? "unknown",
                                    ["route"]       = route.Keyword,
                                    ["consecutive"] = newCount,
                                });

                            // Rate-limit key: tracks failures per agent+route within a 10-minute window.
                            // When the window fills, escalate immediately — something is fundamentally
                            // broken, not just a single stuck turn.
                            var defaultThreshold = Math.Min(
                                _failureHandling.InvalidTransition.Threshold,
                                _failureHandling.NoProgress.Threshold);
                            var rlKey = $"{agentDid}:{route.Keyword}:fail";
                            if (!_governance.RateLimiter.TryAcquire(rlKey,
                                    maxCalls: defaultThreshold,
                                    window:   TimeSpan.FromMinutes(10)))
                            {
                                _validatorFailure = null;
                                throw new ValidatorStuckException(
                                    agentName:           msg.AuthorName ?? _defaultAgentName,
                                    validatorName:       failingValidatorName ?? "unknown",
                                    consecutiveFailures: newCount,
                                    lastValidatorError:  firstError);
                            }
                        }

                        // Record SLO: this check was a failure (bad event).
                        _governance?.SloEngine.Get("policy-compliance")?.Record(0.0);

                        // Detect whether the agent made any tool calls since the last error injection.
                        // Only meaningful when _history is non-null (strategy is actively injecting
                        // corrections) — otherwise there was no prior injection to anchor the check,
                        // so we conservatively assume the agent did make progress.
                        bool hasToolCalls;
                        if (newCount > 1 && _history is not null)
                        {
                            hasToolCalls = false;
                            for (int j = i - 1; j >= 0; j--)
                            {
                                if (history[j].Role == ChatRole.User) break;
                                if (history[j].Role == ChatRole.Tool) { hasToolCalls = true; break; }
                            }
                        }
                        else
                        {
                            hasToolCalls = true; // first failure OR no injection capability
                        }

                        // Classify the failure type and look up the handling policy.
                        var failureType   = FailureClassifier.Classify(firstError, hasToolCalls, isFirstFailure: newCount == 1);
                        var typeConfig    = _failureHandling.GetConfig(failureType);

                        _logger.LogDebug(
                            "[Selection]   Failure classified as {FailureType} (consecutive={Count}) → action={Action} threshold={Threshold}",
                            failureType, newCount, typeConfig.Action, typeConfig.Threshold);

                        // Immediate escalation actions
                        if (typeConfig.Action == FailureAction.EscalateToHuman)
                        {
                            _validatorFailure = null;
                            throw new ValidatorStuckException(
                                agentName:           msg.AuthorName ?? _defaultAgentName,
                                validatorName:       failingValidatorName ?? "unknown",
                                consecutiveFailures: newCount,
                                lastValidatorError:  firstError);
                        }

                        // ActivateRecovery: trigger recovery agent immediately on first request,
                        // rather than waiting for >= 2 failures as in the legacy path.
                        bool recoveryRequested = typeConfig.Action == FailureAction.ActivateRecovery
                            || (newCount >= 2 && route.RecoveryAgent is not null);

                        if (recoveryRequested
                            && route.RecoveryAgent is not null
                            && !_recoveryActivated.Contains(failureKey))
                        {
                            var recoveryAgent = agents.FirstOrDefault(a =>
                                string.Equals(a.Name, route.RecoveryAgent, StringComparison.OrdinalIgnoreCase));

                            if (recoveryAgent is not null)
                            {
                                _recoveryActivated.Add(failureKey);
                                _validatorFailure = null;

                                if (_history is not null)
                                {
                                    _history.Add(new ChatMessage(ChatRole.User,
                                        $"RECOVERY ACTIVATED: '{route.RecoveryAgent}' called — '{failingValidatorName}' " +
                                        $"failed {newCount}× on route '{route.Keyword}'.\n\n" +
                                        $"  1. changes_read_latest — review what was attempted.\n" +
                                        $"  2. Fix the problem described below.\n" +
                                        $"  3. Emit '{route.Keyword}' when resolved.\n\n" +
                                        $"Failure ({failureType}): {firstError}"));
                                }

                                _logger.LogDebug(
                                    "[Selection]   FailureType={FailureType} — activating recovery agent '{Recovery}'",
                                    failureType, route.RecoveryAgent);

                                return recoveryAgent;
                            }

                            _logger.LogDebug(
                                "[Selection]   RecoveryAgent '{Recovery}' not found in agent pool — falling through",
                                route.RecoveryAgent);
                        }

                        // Threshold-based abort
                        // Reinstruct and Abort both abort after their per-type threshold.
                        // EscalateToHuman and ActivateRecovery are handled above and return early.
                        if (newCount >= typeConfig.Threshold)
                        {
                            _validatorFailure = null;
                            throw new ValidatorStuckException(
                                agentName:           msg.AuthorName ?? _defaultAgentName,
                                validatorName:       failingValidatorName ?? "unknown",
                                consecutiveFailures: newCount,
                                lastValidatorError:  firstError);
                        }

                        // Inject correction messages
                        if (_history is not null)
                        {
                            // Tool-refusal correction: inject before the error so the agent reads
                            // both notices in sequence.
                            if (content.Contains("```") ||
                                ToolRefusalPhrases.Any(p =>
                                    content.Contains(p, StringComparison.OrdinalIgnoreCase)))
                            {
                                _history.Add(new ChatMessage(ChatRole.User,
                                    "CRITICAL: Code blocks are NOT written to disk — no filesystem effect. " +
                                    "All tools available: write_file, shell_run, read_file, git_add, git_commit. " +
                                    "Next response must start with a tool call."));
                            }

                            // Compose the correction message based on failure type.
                            var correction = BuildCorrectionMessage(
                                failureType, typeConfig, newCount, firstError,
                                failingValidatorName, hasToolCalls);

                            _history.Add(new ChatMessage(ChatRole.User, correction));
                        }

                        _logger.LogDebug(
                            "[Selection]   Validator blocked route — re-routing back to source agent (consecutive={Count})",
                            newCount);

                        return FindLastSpeakingAgent(history, agents)
                               ?? agents.FirstOrDefault(a => string.Equals(
                                   a.Name, _defaultAgentName, StringComparison.OrdinalIgnoreCase))
                               ?? agents[0];
                    }

                    // All validators passed — clear any accumulated failure count.
                    if (_validatorFailure?.Key == failureKey)
                        _validatorFailure = null;

                    // Record SLO: this check was a success (good event).
                    _governance?.SloEngine.Get("policy-compliance")?.Record(1.0);
                }

                // Human approval gate: if the route requires it, prompt before firing.
                if (route.RequireHumanApproval && _humanApprovalService is not null)
                {
                    var sourceAgentName = msg.AuthorName ?? string.Empty;
                    var approved = await _humanApprovalService.PromptRouteApprovalAsync(
                        route.Keyword, sourceAgentName, route.AgentName);

                    if (!approved)
                    {
                        if (_history is not null)
                            _history.Add(new ChatMessage(ChatRole.User,
                                $"Route to {route.AgentName} was blocked by the operator. " +
                                $"Continue your work or await further instructions."));

                        _logger.LogDebug(
                            "[Selection]   Route blocked by human approval — re-routing back to source agent");

                        return FindLastSpeakingAgent(history, agents)
                               ?? agents.FirstOrDefault(a => string.Equals(
                                   a.Name, _defaultAgentName, StringComparison.OrdinalIgnoreCase))
                               ?? agents[0];
                    }
                }

                // Inject a turn-boundary marker when transitioning to a different agent.
                var sourceAgent = msg.AuthorName ?? string.Empty;
                if (_history is not null
                    && !string.Equals(sourceAgent, route.AgentName, StringComparison.OrdinalIgnoreCase))
                {
                    _history.Add(new ChatMessage(ChatRole.User,
                        $"[fuseraft: {sourceAgent} → {route.AgentName}]"));
                }

                _logger.LogDebug(
                    "[Selection]   Route fired: '{Keyword}' → {Agent}",
                    route.Keyword, matched.Name);

                return matched;
            }
        }

        var defaultAgent = agents.FirstOrDefault(
            a => string.Equals(a.Name, _defaultAgentName, StringComparison.OrdinalIgnoreCase))
            ?? agents[0];

        _logger.LogDebug(
            "[Selection]   No route matched after scanning {Scanned} agent message(s) — falling back to default agent: {Default}",
            scanned, defaultAgent.Name);

        if (_eventEmitter is not null)
            _ = _eventEmitter.EmitAsync("keyword_not_found",
                agent: FindLastSpeakingAgent(history, agents)?.Name ?? _defaultAgentName,
                payload: new { default_agent = _defaultAgentName, turns_scanned = scanned });

        // Inject tool-refusal/code-in-text correction when the most recent agent message
        // contains markdown code blocks or tool-refusal phrases. This fires in the no-keyword-matched
        // path so agents that write code in text instead of calling write_file get corrected
        // immediately rather than only when a validator blocks a handoff.
        InjectCodeBlockCorrectionIfNeeded(history);

        // If the most recently active agent is not the default agent and produced no routing
        // keyword, re-invoke it with a correction rather than falling back to default.
        // This handles the case where an agent completes all its tool work but emits an empty
        // final message — without this guard the default agent is invoked incorrectly, which
        // causes out-of-order execution and can corrupt shared state (e.g. the default agent
        // writing over files it has no business touching).
        var lastAgent = FindLastSpeakingAgent(history, agents);
        if (lastAgent is not null &&
            !string.Equals(lastAgent.Name, _defaultAgentName, StringComparison.OrdinalIgnoreCase))
        {
            if (_history is not null)
            {
                // Special case: agent wrote APPROVED but it was blocked by RequireShellPass.
                // The generic "no keyword" message won't help — the agent needs to know
                // specifically that it must call shell_run (not just mention it in text).
                var lastContent = GetLastAgentText(history);
                if (lastContent is not null &&
                    IsKeywordOnOwnLine(lastContent, "APPROVED"))
                {
                    _history.Add(new ChatMessage(ChatRole.User,
                        "APPROVED blocked: no passing shell_run tool call found this session. " +
                        "Writing 'shell_run' as text does not count — invoke the tool, " +
                        "wait for the real output, then write APPROVED."));
                }
                else
                {
                    _history.Add(new ChatMessage(ChatRole.User,
                        $"No handoff keyword emitted. " +
                        $"Work complete → emit keyword as your entire response. " +
                        $"Work remains → one tool call at a time, then end with the keyword."));
                }
            }

            _logger.LogDebug(
                "[Selection]   Last active agent '{Agent}' emitted no keyword — re-invoking with correction",
                lastAgent.Name);

            InjectLoopWarningIfNeeded(history, lastAgent);
            return lastAgent;
        }

        // Inject a loop-warning if the same agent has been selected consecutively too many times.
        InjectLoopWarningIfNeeded(history, defaultAgent);

        return defaultAgent;
    }

    // Returns true when keyword appears on its own line in content.
    // Matches an exact line as well as lines where the keyword starts the line and is
    // followed by whitespace or punctuation (e.g. "APPROVED All tests pass." or
    // "BUGS FOUND: missing field"). Also strips inline markdown formatting characters
    // (* and _) before checking so that "**BUGS FOUND**: reason" is treated identically
    // to "BUGS FOUND: reason". Prevents keywords embedded mid-sentence from misfiring.
    private static bool IsKeywordOnOwnLine(string content, string keyword)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();

            // Strip inline markdown formatting characters (* and _). Routing keywords
            // never contain these characters so stripping is always safe.
            var stripped = trimmed.Replace("*", "").Replace("_", "").Trim();

            // Exact match (keyword alone on the line).
            if (string.Equals(stripped, keyword, StringComparison.OrdinalIgnoreCase))
                return true;

            // Keyword at the start of the line followed by whitespace or punctuation.
            if (stripped.Length > keyword.Length &&
                stripped.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) &&
                (char.IsWhiteSpace(stripped[keyword.Length]) ||
                 char.IsPunctuation(stripped[keyword.Length])))
                return true;
        }
        return false;
    }

    // Returns the text of the most recent text-bearing assistant message in history.
    private static string? GetLastAgentText(IList<ChatMessage> history)
    {
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i];
            if (msg.Role == ChatRole.Tool) continue;
            if (msg.Role == ChatRole.User) return null;
            if (msg.Role == ChatRole.Assistant && !string.IsNullOrEmpty(msg.Text))
                return msg.Text;
        }
        return null;
    }

    // Returns the most recent agent in history that corresponds to a known agent in the pool.
    private static AIAgent? FindLastSpeakingAgent(
        IList<ChatMessage> history,
        IReadOnlyList<AIAgent> agents)
    {
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i];
            if (msg.Role == ChatRole.Tool || msg.Role == ChatRole.User) continue;
            if (string.IsNullOrEmpty(msg.AuthorName)) continue;

            var agent = agents.FirstOrDefault(
                a => string.Equals(a.Name, msg.AuthorName, StringComparison.OrdinalIgnoreCase));
            if (agent is not null) return agent;
        }
        return null;
    }

    /// <summary>
    /// Scans the most recent agent message in history. If it contains markdown code fences
    /// (```) or any tool-refusal phrase, injects a correction so the agent knows its
    /// code blocks have no effect on disk and that tools are available.
    /// Only fires once per agent turn: we return after examining the first non-empty
    /// assistant message we find.
    /// </summary>
    private void InjectCodeBlockCorrectionIfNeeded(IList<ChatMessage> history)
    {
        if (_history is null) return;

        for (int i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i];
            if (msg.Role == ChatRole.Tool) continue;
            if (string.IsNullOrEmpty(msg.Text)) continue;
            if (msg.Role == ChatRole.User) return;   // no agent message since last user message
            if (msg.Role != ChatRole.Assistant) continue;

            var content = msg.Text;
            if (content.Contains("```") ||
                ToolRefusalPhrases.Any(p => content.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                _history.Add(new ChatMessage(ChatRole.User,
                    "CRITICAL: Code blocks are NOT written to disk — no filesystem effect. " +
                    "All tools available: write_file, shell_run, read_file, git_add, git_commit. " +
                    "Next response must start with a tool call."));
            }
            return;  // only inspect the single most recent agent message
        }
    }

    // Returns true when a "[fuseraft: ... → targetAgentName]" turn-boundary marker
    // exists in history after keywordIndex, meaning this keyword already fired and
    // the target agent was invoked for it. Used to block ghost-keyword re-invocations
    // when an agent finishes a turn with empty text and the selector scans back past
    // the turn boundary to find a stale keyword from a prior agent.
    private static bool RouteAlreadyFired(
        IList<ChatMessage> history,
        int keywordIndex,
        string targetAgentName)
    {
        var infix = $"→ {targetAgentName}";
        for (int j = keywordIndex + 1; j < history.Count; j++)
        {
            var m = history[j];
            if (m.Role != ChatRole.User) continue;
            var text = m.Text;
            if (!string.IsNullOrEmpty(text)
                && text.StartsWith("[fuseraft:", StringComparison.Ordinal)
                && text.Contains(infix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Builds a correction message tailored to the failure type and action.
    private static string BuildCorrectionMessage(
        FailureType failureType,
        FailureTypeConfig typeConfig,
        int newCount,
        string errorMessage,
        string? validatorName,
        bool hadToolCalls)
    {
        var prefix = newCount > 1
            ? $"RETRY {newCount}/{typeConfig.Threshold} — "
            : string.Empty;

        return failureType switch
        {
            FailureType.NoProgress =>
                "NO TOOL CALLS: You re-emitted the keyword without corrective action. " +
                "Next response MUST start with a tool call — no keyword until error below is resolved.\n\n" +
                errorMessage,

            FailureType.MissingEvidence =>
                $"{prefix}MISSING ARTIFACT: Required file not on disk.\n" +
                $"  1. read_file .fuseraft/brief.json\n" +
                $"  2. write_file or create the missing artifact.\n" +
                $"  3. Verify with read_file, then retry the handoff.\n\n" +
                errorMessage,

            FailureType.ConflictingEvidence =>
                $"{prefix}EVIDENCE INCONSISTENCY: Reported evidence conflicts with the change log.\n" +
                $"  1. changes_read_latest — see what actually ran.\n" +
                $"  2. read_file each artifact you claimed to have created.\n" +
                $"  3. Re-run any referenced commands — do not trust memory.\n" +
                $"  4. Retry handoff only after verifying each piece of evidence.\n\n" +
                errorMessage,

            _ => // InvalidTransition (default)
                newCount > 1
                    ? $"RETRY {newCount}/{typeConfig.Threshold} — Previous attempt did not resolve this. Do not repeat it.\n\n" +
                      errorMessage
                    : errorMessage,
        };
    }

    // Returns a short human-readable description of the predicate in a StructuredCondition
    // so that injection messages can tell the agent exactly what value to emit.
    private static string DescribeCondition(StructuredCondition? cond)
    {
        if (cond is null) return "matching the expected predicate";
        if (cond.Is      is not null) return $"equal to \"{cond.Is}\"";
        if (cond.IsNot   is not null) return $"with any value other than \"{cond.IsNot}\"";
        if (cond.Contains is not null) return $"containing \"{cond.Contains}\"";
        if (cond.Exists.HasValue)     return cond.Exists.Value ? "that is present and non-null" : "that is absent or null";
        return "matching the expected predicate";
    }

    private void InjectLoopWarningIfNeeded(
        IList<ChatMessage> history,
        AIAgent agent)
    {
        if (_history is null) return;

        int consecutive = 0;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i];
            if (msg.Role == ChatRole.Tool) continue;
            if (string.IsNullOrEmpty(msg.Text)) continue;
            if (msg.Role == ChatRole.User) break;
            if (!string.Equals(msg.AuthorName, agent.Name, StringComparison.OrdinalIgnoreCase)) break;
            consecutive++;
        }

        if (consecutive > 0 && consecutive % ConsecutiveTurnWarningThreshold == 0)
        {
            _history.Add(new ChatMessage(ChatRole.User,
                $"LOOP WARNING: {agent.Name} — {consecutive} consecutive turns, task incomplete.\n" +
                $"  1. read_file .fuseraft/brief.json\n" +
                $"  2. changes_read_latest\n" +
                $"  3. Execute the single blocking action.\n" +
                $"  4. Emit the handoff keyword."));
        }
    }
}
