using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using fuseraft.Core.Subagents;
using fuseraft.Infrastructure.Agents;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Provides lightweight subagent tools that any pipeline agent can delegate work to:
///
/// <list type="bullet">
///   <item><see cref="ExploreAsync"/> — multi-hop exploration loop for broad codebase
///     questions. Runs up to <c>maxToolCalls</c> iterations and returns a concise prose
///     summary or a bulleted file list, depending on the requested <c>format</c>.</item>
///   <item><see cref="LocateAsync"/> — tight 5-iteration loop for single-target symbol,
///     type, or file lookups. Returns a <c>path:line</c> result without filling the
///     caller's context window.</item>
///   <item><see cref="DelegateAsync"/> — write-capable loop for a self-contained coding
///     subtask (edit files, run shell commands, use git). Unlike Explore/Locate, this
///     subagent is only constructed with <c>delegateTools</c> — it never receives the
///     Subagent tool set itself, so it cannot recursively spawn further delegates.</item>
/// </list>
///
/// <para>
/// All loops use <c>FunctionInvokingChatClient</c> with an enforced
/// <c>MaximumIterationsPerRequest</c> cap. The parent agent's <see cref="CancellationToken"/>
/// is linked to a per-call timeout so interrupts propagate immediately.
/// </para>
///
/// <para>
/// When an <see cref="EventEmitter"/> is provided, each tool call inside the subagent loop
/// emits a <c>subagent_tool_call</c> event so activity is visible between
/// <c>subagent_start</c> and <c>subagent_end</c>.
/// </para>
///
/// <para>
/// Per-agent instances are created in <see cref="fuseraft.Infrastructure.Agents.AgentFactory"/>
/// using the parent agent's resolved model and a sandboxed <see cref="FileSystemPlugin"/>.
/// A stub is registered in <see cref="PluginRegistry.RegisterDefaults"/> so that
/// <c>fuseraft plugins</c> can enumerate the tool names and descriptions.
/// </para>
/// </summary>
public sealed class SubagentPlugin(
    IChatClient? chatClient,
    IReadOnlyList<AIFunction> explorerTools,
    int maxOutputTokens = 2048,
    EventEmitter? eventEmitter = null,
    string? parentAgentName = null,
    int maxToolCalls = 0,
    string? workspaceRoot = null,
    IReadOnlyList<AIFunction>? delegateTools = null,
    IReadOnlyList<AIFunction>? diagnosticTools = null,
    IReadOnlyList<SubagentDefinition>? customAgents = null,
    Func<string, IChatClient?>? customAgentClientFactory = null,
    int delegateMaxToolCalls = 0,
    double exploreTimeoutMinutes = 0,
    double locateTimeoutMinutes = 0,
    double delegateTimeoutMinutes = 0)
{
    // Session-introspection tools (current session metadata, saved-session list, event/log
    // file reads) withheld from the REPL agent's own default tool set — they let a caller
    // read a *different* session's full event log by ID, real cross-session data exposure
    // with no turn-to-turn value for the primary loop — but useful for /assist's diagnosis.
    private readonly IReadOnlyList<AIFunction> _diagnosticTools = diagnosticTools ?? [];
    private const double DefaultExploreTimeoutMinutes  = 8.0;
    private const double DefaultLocateTimeoutMinutes   = 2.0;
    private const double DefaultDelegateTimeoutMinutes = 15.0;
    private const int DefaultMaxToolCalls       = 20;
    private const int LocateMaxToolCalls        = 5;
    private const int LocateMaxOutputTokens     = 512;
    private const int DefaultDelegateMaxToolCalls = 40;
    private const int DelegateMaxOutputTokens   = 4096;

    // In-turn context trim applied before every inner LLM call inside RunLoopAsync's tool
    // loop — mirrors AgentFactory's sliding-window cap for regular agents (see
    // AgentFactory.cs: "O(N² ) tool-result accumulation is never desirable"). Without this,
    // the loop's own message list grows every round and FunctionInvokingChatClient resends
    // the entire thing on every iteration; a 40-round DelegateAsync run editing several
    // files can otherwise burn 7-figure cumulative input tokens for what should be a bounded
    // task. Sized smaller than AgentFactory's defaults (12 pairs / 200k chars) because these
    // are meant to stay lightweight relative to the parent agent. The pair window itself only
    // actually collapses once CompactionTriggerRatio of SubagentMaxInTurnChars is reached
    // (see ApplyInTurnFilters' triggerChars) — below that, calls resend an identical prefix.
    private const int SubagentMaxInTurnToolPairs = 10;
    private const int SubagentMaxInTurnChars     = 100_000;

    // Priority-ordered tool hints for Explore. Only tools actually present in explorerTools
    // are included — prevents instructing the model to call tools that don't exist.
    private static readonly (string Name, string Hint)[] ExploreToolPriority =
    [
        ("search_symbol",    "type, method, interface, or class definitions"),
        ("list_files",       "file discovery by name pattern"),
        ("search_content",   "content patterns across the codebase"),
        ("get_file_summary", "before read_file on any unconfirmed file"),
        ("grep_file",        "targeted in-file content search"),
        ("read_file",        "actual implementation; only when summary is insufficient"),
        ("shell_run",        "verify a specific hypothesis (build, test); never for browsing"),
    ];

    private static readonly (string Name, string Hint)[] LocateToolPriority =
    [
        ("search_symbol",  "first choice for types, methods, interfaces, class names"),
        ("list_files",     "for filenames or path patterns"),
        ("search_content", "for string patterns when search_symbol is insufficient"),
        ("grep_file",      "for string patterns when search_symbol is insufficient"),
        ("read_file",      "only to confirm the exact line number once the file is known"),
    ];

    // Wrap tools with event-emitting proxies so subagent tool activity is visible in the
    // event log between subagent_start and subagent_end.
    private readonly IReadOnlyList<AIFunction> _tools =
        eventEmitter is not null
            ? WrapWithNotifiers(explorerTools, eventEmitter, parentAgentName)
            : explorerTools;

    // Write-capable tool set for DelegateAsync. Empty (not null) when the caller didn't
    // configure one, so DelegateAsync can short-circuit with a clear message instead of
    // running a loop with zero tools.
    private readonly IReadOnlyList<AIFunction> _delegateTools =
        delegateTools is null or { Count: 0 }
            ? []
            : eventEmitter is not null
                ? WrapWithNotifiers(delegateTools, eventEmitter, parentAgentName)
                : delegateTools;

    // User-defined agents (Markdown files — see SubagentDefinitionLoader), bound to this plugin's
    // tool pool and, where they name one, their own model. Bound after _tools/_delegateTools so it
    // draws from the same event-wrapped instances. Never contains this plugin's own tools, so a
    // custom agent cannot spawn further subagents.
    // Lazy because a field initializer cannot read the other instance fields it draws from; first
    // touched at REPL startup (CustomAgents / BuildRunAgentTool), so model clients are built up front.
    private CustomAgentBinding? _customBinding;
    private CustomAgentBinding _custom =>
        _customBinding ??= CustomAgentBinding.Bind(customAgents ?? [], customAgentClientFactory, _tools, _delegateTools);

    private readonly int _effectiveMaxToolCalls =
        maxToolCalls > 0 ? maxToolCalls : DefaultMaxToolCalls;

    private readonly int _effectiveDelegateMaxToolCalls =
        delegateMaxToolCalls > 0 ? delegateMaxToolCalls : DefaultDelegateMaxToolCalls;

    private readonly double _exploreTimeoutMinutes  = exploreTimeoutMinutes  > 0 ? exploreTimeoutMinutes  : DefaultExploreTimeoutMinutes;
    private readonly double _locateTimeoutMinutes   = locateTimeoutMinutes   > 0 ? locateTimeoutMinutes   : DefaultLocateTimeoutMinutes;
    private readonly double _delegateTimeoutMinutes = delegateTimeoutMinutes > 0 ? delegateTimeoutMinutes : DefaultDelegateTimeoutMinutes;

    /// <summary>
    /// Decides, per tool name and at run time, whether a subagent may use a tool. The REPL points this
    /// at its session-wide gate (<c>/safe-mode</c>, <c>/tools restrict</c>) so a subagent can never do
    /// what the parent has been told not to — without it, /safe-mode would still leave shell and git
    /// reachable through <c>subagent_delegate</c>. <c>null</c> = no extra restriction.
    /// </summary>
    public Func<string, bool>? ToolGate { get; set; }

    /// <summary>The user-defined agents that loaded and bound successfully.</summary>
    public IReadOnlyList<CustomAgentInfo> CustomAgents => _custom.Infos;

    /// <summary>Problems found while binding user-defined agents (unknown tools, unavailable models).</summary>
    public IReadOnlyList<string> CustomAgentProblems => _custom.Problems;

    private readonly string _workspaceRoot =
        workspaceRoot ?? Directory.GetCurrentDirectory();

    // --- Public tools ---

    [Description("Broad codebase exploration. Returns a prose summary or file list. Use for multi-hop questions (e.g. 'Which files handle X?', 'What conventions does this repo use?').")]
    public async Task<string> ExploreAsync(
        [Description("Exploration question or task.")]
        string query,
        [Description("Output format: 'prose' (default, narrative summary) or 'file_list' (bulleted list of relevant file paths with one-line roles).")]
        string format = "prose",
        CancellationToken cancellationToken = default)
    {
        var (text, _, _) = await RunLoopAsync(
            _tools,
            BuildExplorePrompt(_tools, _effectiveMaxToolCalls, format, _workspaceRoot),
            query,
            _effectiveMaxToolCalls,
            maxOutputTokens,
            "explore",
            _exploreTimeoutMinutes,
            cancellationToken);
        return text;
    }

    [Description("Locate where a symbol, type, method, interface, or file is defined. Returns file path and line number. Prefer over explore for single-target lookups.")]
    public async Task<string> LocateAsync(
        [Description("Symbol, type, interface, method, or filename to locate (e.g. 'IOrchestrationHook', 'AgentFactory.Create', 'EventEmitter.cs').")]
        string target,
        CancellationToken cancellationToken = default)
    {
        var (text, _, _) = await RunLoopAsync(
            _tools,
            BuildLocatePrompt(_tools, _workspaceRoot),
            $"Locate: {target}",
            LocateMaxToolCalls,
            LocateMaxOutputTokens,
            "locate",
            _locateTimeoutMinutes,
            cancellationToken);
        return text;
    }

    /// <summary>Tool name <see cref="DelegateAsync"/> is exposed under.</summary>
    public const string DelegateToolName = "subagent_delegate";

    [Description("Delegate a self-contained coding subtask to a subagent with read/write file, shell, and git tools. Use for well-scoped work you want done without spending your own tool calls and context — e.g. 'add a null check to X and a regression test', 'rename Y across the codebase', 'run the test suite and fix any failures in Z'. The subagent works autonomously to completion and reports back a summary; it cannot ask clarifying questions mid-task, so give it a complete, unambiguous task description.")]
    public async Task<string> DelegateAsync(
        [Description("Complete, self-contained task description. Include file paths, requirements, and acceptance criteria — enough context that the subagent never needs to ask a question.")]
        string task,
        CancellationToken cancellationToken = default)
    {
        if (_delegateTools.Count == 0)
            return "[Subagent] Delegate not available — no write-capable tools were configured for this session (e.g. started with --no-tools).";

        var (text, _, _) = await RunLoopAsync(
            _delegateTools,
            BuildDelegatePrompt(_delegateTools, _workspaceRoot),
            task,
            _effectiveDelegateMaxToolCalls,
            DelegateMaxOutputTokens,
            "delegate",
            _delegateTimeoutMinutes,
            cancellationToken);
        return text;
    }

    // Single-turn session diagnosis — not a model tool (no [Description]).
    // Reads the REPL conversation history, identifies where things are going wrong, and returns
    // a corrective instruction addressed to the REPL agent for injection as a user message.
    // Returns null when the diagnoser produces no output or the call fails/times out.
    public async Task<(string? Result, int? InputTokens, int? OutputTokens)> DiagnoseAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        if (chatClient is null) return (null, null, null);

        var diagnosticSystem =
            "You are a session diagnostician. You will receive a transcript of a conversation " +
            "between a user and an AI coding assistant that has stalled or gone off track.\n\n" +
            "Identify the root cause: repeated failures, fabricated tool output, " +
            "misunderstood task, wrong approach, stuck in a loop, or anything else explaining " +
            "why progress has stalled.\n\n" +
            "Write a short, direct corrective instruction addressed TO the assistant — not to " +
            "the user. Tell it exactly what it is doing wrong and what to do differently. " +
            "Be specific and concrete. Reference file paths or symbols where relevant.\n\n" +
            "Output ONLY the corrective instruction. No preamble, no diagnosis header, " +
            "no explanation to the user — just the message to inject.";
        if (_diagnosticTools.Count > 0)
            diagnosticSystem +=
                "\n\nThe transcript below is truncated. If it doesn't give you enough to go on, " +
                "call the available session tools first (e.g. read the event log for the full " +
                "tool-call history) before writing the corrective instruction.";

        const int msgCap = 800;
        var transcript = new StringBuilder();
        foreach (var m in history.TakeLast(40))
        {
            var role    = m.Role == ChatRole.System    ? "system"
                        : m.Role == ChatRole.User      ? "user"
                        : "assistant";
            var text    = m.Text ?? string.Empty;
            var excerpt = text.Length > msgCap ? text[..msgCap] + "…" : text;
            transcript.AppendLine($"[{role}]: {excerpt}");
            transcript.AppendLine();
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, diagnosticSystem),
            new(ChatRole.User,   $"Conversation transcript:\n\n{transcript}"),
        };
        var options = new ChatOptions { MaxOutputTokens = 512 };
        if (_diagnosticTools.Count > 0)
            options.Tools = [.. _diagnosticTools];

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var response  = await chatClient.GetResponseAsync(messages, options, cts.Token);
            var text      = (response.Text ?? string.Empty).Trim();
            var inputTok  = (int?)response.Usage?.InputTokenCount;
            var outputTok = (int?)response.Usage?.OutputTokenCount;
            return (string.IsNullOrEmpty(text) ? null : text, inputTok, outputTok);
        }
        catch (Exception ex)
        {
            if (eventEmitter is not null)
                try { await eventEmitter.EmitAsync(EventTypes.SubagentEnd, agent: parentAgentName,
                    payload: new { outcome = "error", error = ex.Message, mode = "diagnose" }); } catch { }
            return (null, null, null);
        }
    }

    // Single-turn critic review — not a model tool (no [Description]).
    // Used both for /execute plan steps (taskDescription = step description, expectedTool set,
    // originalUserRequest = the /plan <task> text so the critic has the real ask, not just the
    // plan's own per-step paraphrase) and free-form REPL turns under adversarial mode
    // (taskDescription = user input, expectedTool null, originalUserRequest omitted since
    // taskDescription already is the user's own words).
    // Returns (true, null) when approved, (false, reason) when rejected.
    // Degrades gracefully on timeout or error so a critic failure never blocks execution.
    public async Task<(bool Approved, string? Reason)> CriticReviewAsync(
        string taskDescription,
        string? expectedTool,
        IReadOnlyList<string> toolsCalled,
        string agentResponse,
        string? originalUserRequest = null,
        CancellationToken cancellationToken = default)
    {
        if (chatClient is null)
            return (true, null);

        const string criticSystem =
            "You are a strict critic reviewing an AI assistant's response. You receive the " +
            "user's original request, the specific task or step being judged, the tools the " +
            "agent called, and the agent's response. Judge all of the following:\n" +
            "1. Correct — fully accurate, grounded in the tool output actually returned " +
            "(not fabricated, guessed, or assumed).\n" +
            "2. Complete — addresses everything the task/step asked for; nothing silently skipped.\n" +
            "3. Right-sized for the user's original request — doesn't leave out something the " +
            "request implied, and doesn't add unrequested scope: extra deliverables, files, or " +
            "changes beyond what was actually asked. Do NOT count verification actions that " +
            "confirm the requested change worked (e.g. re-reading a file just written, checking " +
            "a command's exit code) as scope creep — those are expected diligence, not padding.\n" +
            "If all three hold, respond with exactly:\nAPPROVED\n\n" +
            "Otherwise, describe the specific defect in one or two sentences. Be precise — " +
            "state what is wrong, missing, or out of scope — not just that something is wrong.";

        var toolsStr     = toolsCalled.Count > 0 ? string.Join(", ", toolsCalled) : "(none)";
        var expectedStr  = expectedTool is not null ? $"\nExpected tool: {expectedTool}" : string.Empty;
        var requestStr   = !string.IsNullOrWhiteSpace(originalUserRequest) &&
                            !originalUserRequest.Equals(taskDescription, StringComparison.Ordinal)
            ? $"User's original request: {originalUserRequest}\n"
            : string.Empty;
        var userMsg      =
            $"{requestStr}Task: {taskDescription}{expectedStr}\n" +
            $"Tools called: {toolsStr}\n\n" +
            $"Agent response:\n{agentResponse}";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, criticSystem),
            new(ChatRole.User,   userMsg),
        };
        var options = new ChatOptions { MaxOutputTokens = 256 };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var response = await chatClient.GetResponseAsync(messages, options, cts.Token);
            var text     = (response.Text ?? string.Empty).Trim();
            return text.StartsWith("APPROVED", StringComparison.OrdinalIgnoreCase)
                ? (true, null)
                : (false, string.IsNullOrEmpty(text) ? "Critic returned no feedback." : text);
        }
        catch (Exception ex)
        {
            if (eventEmitter is not null)
                try { await eventEmitter.EmitAsync(EventTypes.SubagentEnd, agent: parentAgentName,
                    payload: new { outcome = "error", error = ex.Message, mode = "critic" }); } catch { }
            return (true, null);
        }
    }

    // Streaming variants — not registered as model tools (no [Description]).
    // onChunk is called for each text token as the final answer arrives. Unlike the
    // model-tool variants above, these return the real token usage alongside the result
    // text so callers (REPL /explore, /locate, /delegate) can roll it into session cost tracking.

    public Task<(string Result, int? InputTokens, int? OutputTokens)> ExploreStreamingAsync(
        string query,
        Func<string, Task> onChunk,
        string format = "prose",
        CancellationToken cancellationToken = default)
        => RunLoopAsync(
            _tools,
            BuildExplorePrompt(_tools, _effectiveMaxToolCalls, format, _workspaceRoot),
            query,
            _effectiveMaxToolCalls,
            maxOutputTokens,
            "explore",
            _exploreTimeoutMinutes,
            cancellationToken,
            onChunk);

    public Task<(string Result, int? InputTokens, int? OutputTokens)> LocateStreamingAsync(
        string target,
        Func<string, Task> onChunk,
        CancellationToken cancellationToken = default)
        => RunLoopAsync(
            _tools,
            BuildLocatePrompt(_tools, _workspaceRoot),
            $"Locate: {target}",
            LocateMaxToolCalls,
            LocateMaxOutputTokens,
            "locate",
            _locateTimeoutMinutes,
            cancellationToken,
            onChunk);

    public Task<(string Result, int? InputTokens, int? OutputTokens)> DelegateStreamingAsync(
        string task,
        Func<string, Task> onChunk,
        CancellationToken cancellationToken = default)
        => _delegateTools.Count == 0
            ? Task.FromResult<(string, int?, int?)>((
                "[Subagent] Delegate not available — no write-capable tools were configured for this session (e.g. started with --no-tools).",
                null, null))
            : RunLoopAsync(
                _delegateTools,
                BuildDelegatePrompt(_delegateTools, _workspaceRoot),
                task,
                _effectiveDelegateMaxToolCalls,
                DelegateMaxOutputTokens,
                "delegate",
                _delegateTimeoutMinutes,
                cancellationToken,
                onChunk);

    // --- User-defined agents ---

    /// <summary>
    /// The <c>subagent_run</c> model tool, or <c>null</c> when no user-defined agents exist (so an
    /// unused feature costs no tool-schema tokens). Built by hand rather than by reflection because
    /// its description enumerates the available agents.
    /// </summary>
    public AIFunction? BuildRunAgentTool()
    {
        if (_custom.Agents.Count == 0) return null;

        return AIFunctionFactory.Create(
            ([Description("Name of the subagent to run — one of the names listed in this tool's description.")] string agent,
             [Description("Complete, self-contained task. Include file paths, requirements and acceptance criteria — the subagent cannot ask a clarifying question.")] string task,
             CancellationToken cancellationToken) => RunAgentAsync(agent, task, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name        = RunAgentToolName,
                Description = BuildRunAgentDescription(),
            });
    }

    /// <summary>Name of the model tool <see cref="BuildRunAgentTool"/> returns.</summary>
    public const string RunAgentToolName = "subagent_run";

    internal string BuildRunAgentDescription()
    {
        var sb = new StringBuilder(
            "Run a user-defined specialist subagent on a self-contained task and get back its report. " +
            "Prefer one of these over doing the work yourself when its description fits. " +
            "The subagent cannot ask questions, so give it a complete task.\n\nAvailable agents:\n");
        foreach (var a in _custom.Agents)
        {
            var d = a.Def.Description;
            if (d.Length > 300) d = d[..300] + "…";
            sb.Append("- ").Append(a.Def.Name).Append(": ").Append(d.ReplaceLineEndings(" ")).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    public async Task<string> RunAgentAsync(string agent, string task, CancellationToken cancellationToken = default)
    {
        var (text, _, _) = await RunAgentStreamingAsync(agent, task, onChunk: null, cancellationToken);
        return text;
    }

    /// <summary>Runs a user-defined agent by name; streams text to <paramref name="onChunk"/> when given.</summary>
    public Task<(string Result, int? InputTokens, int? OutputTokens)> RunAgentStreamingAsync(
        string agent,
        string task,
        Func<string, Task>? onChunk,
        CancellationToken cancellationToken = default)
    {
        var bound = _custom.Find(agent);
        if (bound is null)
        {
            var known = _custom.Agents.Count > 0 ? string.Join(", ", _custom.Agents.Select(a => a.Def.Name)) : "(none defined)";
            return Task.FromResult<(string, int?, int?)>(($"[Subagent] Unknown agent '{agent}'. Available: {known}.", null, null));
        }

        var tools = Gate(bound.Tools);
        return RunLoopAsync(
            tools,
            BuildCustomAgentPrompt(bound.Def, tools, _workspaceRoot),
            task,
            bound.Def.MaxIterations,
            DelegateMaxOutputTokens,
            $"agent:{bound.Def.Name}",
            _delegateTimeoutMinutes,
            cancellationToken,
            onChunk,
            clientOverride: bound.Client);
    }

    private IReadOnlyList<AIFunction> Gate(IReadOnlyList<AIFunction> tools) =>
        ToolGate is { } gate ? [.. tools.Where(t => gate(t.Name))] : tools;

    private static string BuildCustomAgentPrompt(SubagentDefinition def, IReadOnlyList<AIFunction> tools, string cwd)
    {
        var toolList = tools.Count > 0 ? string.Join(", ", tools.Select(t => t.Name)) : "(none — reason from the task text alone)";
        return $"""
            {def.Instructions}

            ---
            Runtime context: you are the '{def.Name}' subagent, invoked by another assistant to complete
            one task and report back. You cannot ask the caller a clarifying question — make the most
            reasonable interpretation of any ambiguity and proceed.
            Working directory: {cwd}
            Available tools: {toolList}
            Skip .fuseraft/ — it is fuseraft-cli runtime metadata, not application code.
            Avoid destructive or irreversible actions unless the task explicitly asks for them.
            When finished, reply with a concise report: what you found or changed (with file paths),
            commands run and their outcome, and anything the caller must follow up on. Summarize rather
            than pasting full file contents or command output.
            """;
    }

    /// <summary>What <c>/agents</c> shows for one bound agent.</summary>
    public sealed record CustomAgentInfo(SubagentDefinition Definition, IReadOnlyList<string> ToolNames, string? Model);

    private sealed record BoundAgent(SubagentDefinition Def, IReadOnlyList<AIFunction> Tools, IChatClient? Client);

    private sealed class CustomAgentBinding
    {
        public List<BoundAgent>      Agents   { get; } = [];
        public List<CustomAgentInfo> Infos    { get; } = [];
        public List<string>          Problems { get; } = [];

        public BoundAgent? Find(string name) =>
            Agents.FirstOrDefault(a => a.Def.Name.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase));

        public static CustomAgentBinding Bind(
            IReadOnlyList<SubagentDefinition> defs,
            Func<string, IChatClient?>? clientFactory,
            IReadOnlyList<AIFunction> readOnlyPool,
            IReadOnlyList<AIFunction> writePool)
        {
            var result = new CustomAgentBinding();

            // Name → tool. The write-capable set first so a name present in both keeps one instance.
            var byName = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in writePool.Concat(readOnlyPool)) byName.TryAdd(t.Name, t);

            foreach (var def in defs)
            {
                IReadOnlyList<AIFunction> tools;
                if (def.Tools is null)
                {
                    // "Read-only" has to mean it: the explorer pool includes shell_run, which is not.
                    tools = [.. readOnlyPool.Where(t => !ExplorerToolSets.CanMutate.Contains(t.Name))];
                }
                else if (def.AllTools)
                {
                    tools = [.. byName.Values];
                }
                else
                {
                    var picked = new List<AIFunction>();
                    foreach (var name in def.Tools)
                    {
                        if (byName.TryGetValue(name, out var t)) picked.Add(t);
                        else result.Problems.Add(
                            $"agent '{def.Name}': tool '{name}' is not available in this session and was ignored");
                    }
                    tools = picked;
                }

                IChatClient? client = null;
                if (def.Model is not null && clientFactory is not null)
                {
                    try
                    {
                        client = clientFactory(def.Model);
                        if (client is null)
                            result.Problems.Add($"agent '{def.Name}': model '{def.Model}' is not available — using the session's subagent model");
                    }
                    catch (Exception ex)
                    {
                        result.Problems.Add($"agent '{def.Name}': model '{def.Model}' could not be created ({ex.Message}) — using the session's subagent model");
                    }
                }

                result.Agents.Add(new BoundAgent(def, tools, client));
                result.Infos.Add(new CustomAgentInfo(def, [.. tools.Select(t => t.Name)], client is null ? null : def.Model));
            }
            return result;
        }
    }

    // --- Core loop (shared by both tools) ---

    private async Task<(string Text, int? InputTokens, int? OutputTokens)> RunLoopAsync(
        IReadOnlyList<AIFunction> tools,
        string systemPrompt,
        string userQuery,
        int maxIterations,
        int outputTokens,
        string mode,
        double timeoutMinutes,
        CancellationToken cancellationToken,
        Func<string, Task>? onChunk = null,
        IChatClient? clientOverride = null)
    {
        var client = clientOverride ?? chatClient;
        if (client is null)
            return ("[Subagent] No chat client configured — this is a stub instance. " +
                    "Ensure AgentFactory created a real SubagentPlugin for this agent.", null, null);

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync(EventTypes.SubagentStart,
                agent:   parentAgentName,
                payload: new { query = userQuery.Length > 120 ? userQuery[..120] + "…" : userQuery, mode });

        // Link the parent's CT so cancellation propagates immediately; timeout is a safety net.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User,   userQuery),
        };

        // Trim first (inner), then wrap with the function-invocation loop (outer) — same
        // layering AgentFactory uses for regular agents: FunctionInvokingChatClient keeps
        // its own full message list for tool-call bookkeeping, but what actually goes out
        // over the wire each round is the trimmed view built fresh every call.
        var trimmedClient = client.AsBuilder()
            .Use(
                getResponseFunc: async (msgs, opts, inner, ct) =>
                {
                    // triggerChars uses the same budget as the hard char cap — the pair
                    // window engages at CompactionTriggerRatio of it, slightly before
                    // TrimInTurnContext would kick in at 100%.
                    var trimmed = await AgentContextCompactionFilters.ApplyInTurnFilters(
                        msgs, SubagentMaxInTurnToolPairs, SubagentMaxInTurnChars,
                        triggerChars: SubagentMaxInTurnChars, cancellationToken: ct);
                    return await inner.GetResponseAsync(trimmed, opts, ct);
                },
                getStreamingResponseFunc: StreamWithInTurnTrimAsync)
            .Build();

        // AgentToolLoopGuard guards against a model looping on the exact same tool call forever —
        // see its own doc comment for why this needs FunctionInvoker specifically.
        var loopGuard  = new AgentToolLoopGuard(parentAgentName, eventEmitter);
        var loopClient = trimmedClient.AsBuilder()
            .UseFunctionInvocation(configure: c =>
            {
                c.MaximumIterationsPerRequest = maxIterations;
                c.FunctionInvoker = loopGuard.InvokeAsync;
            })
            .Build();

        // Some providers reject an empty tools array outright, so a run whose tool set is empty
        // (a pure-reasoning agent, or everything gated off) sends none rather than [].
        var allowed = Gate(tools);
        var options = new ChatOptions
        {
            Tools           = allowed.Count > 0 ? allowed.Cast<AITool>().ToList() : null,
            ToolMode        = allowed.Count > 0 ? ChatToolMode.Auto : null,
            MaxOutputTokens = outputTokens,
        };

        string outcome = "completed";
        try
        {
            string result;
            int? inputTok, outputTok;
            if (onChunk is not null)
            {
                var sb = new StringBuilder();
                long streamedInputTok = 0, streamedOutputTok = 0;
                var endedOnToolCall = false;
                await foreach (var update in loopClient.GetStreamingResponseAsync(messages, options, cts.Token))
                {
                    foreach (var content in update.Contents)
                    {
                        if (content is FunctionCallContent) endedOnToolCall = true;
                        else if (content is FunctionResultContent) endedOnToolCall = false;
                    }

                    // A usage-only chunk arrives per underlying LLM call — a loop with tool
                    // round trips produces one per round trip, so sum rather than overwrite.
                    foreach (var usage in update.Contents.OfType<UsageContent>())
                    {
                        streamedInputTok  += usage.Details.InputTokenCount  ?? 0;
                        streamedOutputTok += usage.Details.OutputTokenCount ?? 0;
                    }

                    var text = update.Text;
                    if (!string.IsNullOrEmpty(text))
                    {
                        sb.Append(text);
                        await onChunk(text);
                    }
                }
                var streamed = sb.ToString();
                if (endedOnToolCall)
                {
                    outcome = IterationLimitOutcome;
                    var notice = BuildIterationLimitNotice(maxIterations);
                    // The REPL commands show only what is streamed, so the notice has to reach the user here too.
                    await onChunk($"\n\n{notice}");
                    result = AppendPartialOutput(notice, streamed);
                }
                else
                {
                    result = streamed.Length > 0 ? streamed : "Subagent produced no text output.";
                }
                inputTok  = streamedInputTok  > 0 ? (int)streamedInputTok  : null;
                outputTok = streamedOutputTok > 0 ? (int)streamedOutputTok : null;

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync(EventTypes.SubagentEnd,
                        agent:   parentAgentName,
                        payload: new { outcome, summary_chars = result.Length, mode,
                                       input_tokens = inputTok, output_tokens = outputTok });
            }
            else
            {
                var response = await loopClient.GetResponseAsync(messages, options, cts.Token);
                inputTok     = (int?)response.Usage?.InputTokenCount;
                outputTok    = (int?)response.Usage?.OutputTokenCount;
                if (response.Messages.LastOrDefault()?.Contents.OfType<FunctionCallContent>().Any() == true)
                {
                    outcome = IterationLimitOutcome;
                    result  = AppendPartialOutput(BuildIterationLimitNotice(maxIterations), response.Text);
                }
                else
                {
                    result = string.IsNullOrWhiteSpace(response.Text)
                        ? "Subagent produced no text output."
                        : response.Text;
                }

                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync(EventTypes.SubagentEnd,
                        agent:   parentAgentName,
                        payload: new { outcome, summary_chars = result.Length, mode,
                                       input_tokens = inputTok, output_tokens = outputTok });
            }

            return (result, inputTok, outputTok);
        }
        catch (OperationCanceledException)
        {
            outcome = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
            if (eventEmitter is not null)
                try { await eventEmitter.EmitAsync(EventTypes.SubagentEnd,
                    agent:   parentAgentName,
                    payload: new { outcome, mode }); } catch { }
            return (outcome == "cancelled"
                ? "Subagent was cancelled."
                : $"Subagent timed out after {timeoutMinutes} minutes.", null, null);
        }
        catch (Exception ex)
        {
            outcome = "error";
            if (eventEmitter is not null)
                try { await eventEmitter.EmitAsync(EventTypes.SubagentEnd,
                    agent:   parentAgentName,
                    payload: new { outcome, error = ex.Message, mode }); } catch { }
            return ($"Subagent failed: {ex.Message}", null, null);
        }
    }

    private const string IterationLimitOutcome = "iteration_limit";

    // FunctionInvokingChatClient stops at MaximumIterationsPerRequest without saying so: it returns the
    // last response, still ending on a tool call it never ran. Without this the caller would read an
    // unfinished run as an answer (or as "no output").
    private static string BuildIterationLimitNotice(int maxIterations) =>
        $"[Subagent stopped after {maxIterations} rounds without finishing — its work may be incomplete. " +
        "Re-run with a narrower task, or finish the remaining work yourself.]";

    private static string AppendPartialOutput(string notice, string? partial) =>
        string.IsNullOrWhiteSpace(partial) ? notice : $"{notice}\nPartial output:\n{partial}";

    // Streaming counterpart of the getResponseFunc trim above — same ApplyInTurnFilters call,
    // just shaped as an async iterator since the streaming delegate can't be a simple lambda.
    private static async IAsyncEnumerable<ChatResponseUpdate> StreamWithInTurnTrimAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IChatClient inner,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var trimmed = await AgentContextCompactionFilters.ApplyInTurnFilters(
            messages, SubagentMaxInTurnToolPairs, SubagentMaxInTurnChars,
            triggerChars: SubagentMaxInTurnChars, cancellationToken: cancellationToken);
        await foreach (var update in inner.GetStreamingResponseAsync(trimmed, options, cancellationToken))
            yield return update;
    }

    // --- Prompt builders ---

    private static string BuildExplorePrompt(
        IReadOnlyList<AIFunction> tools,
        int maxToolCalls,
        string format,
        string cwd)
    {
        var toolNames    = tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var priorityLines = ExploreToolPriority
            .Where(p => toolNames.Contains(p.Name))
            .Select((p, i) => $"{i + 1}. {p.Name}  — {p.Hint}.")
            .ToList();
        var toolPriority = priorityLines.Count > 0
            ? "Tool selection priority (prefer earlier options when they suffice):\n" +
              string.Join("\n", priorityLines)
            : $"Available tools: {(tools.Count > 0 ? string.Join(", ", tools.Select(t => t.Name)) : "(none configured)")}.";

        var outputInstructions = format.ToLowerInvariant() == "file_list"
            ? """
              Output format — return ONLY a markdown bulleted list of relevant files:
                - relative/path/to/file.ext — one-line role description
              No prose paragraphs. Sort most-relevant first.
              """
            : "Write a focused prose summary (under 600 words) that directly answers the query, then stop.";

        return $"""
            You are a codebase explorer subagent. Your ONLY job is to answer the query you are given.
            Working directory: {cwd}
            {toolPriority}

            Aim to answer within {maxToolCalls} tool calls using targeted queries.
            Do NOT implement, edit, delete, commit, or push anything.
            Never run mutating shell commands (no git add, git commit, rm, mv, write_file, etc.).
            Skip .fuseraft/ — it is fuseraft-cli runtime metadata, not application code.
            {outputInstructions}
            """;
    }

    private static string BuildLocatePrompt(IReadOnlyList<AIFunction> tools, string cwd)
    {
        var toolNames     = tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var priorityLines = LocateToolPriority
            .Where(p => toolNames.Contains(p.Name))
            .DistinctBy(p => p.Name)
            .Select((p, i) => $"{i + 1}. {p.Name}  — {p.Hint}.")
            .ToList();
        var toolPriority = priorityLines.Count > 0
            ? "Tool priority (use the cheapest that works; stop the moment you have the answer):\n" +
              string.Join("\n", priorityLines)
            : $"Available tools: {(tools.Count > 0 ? string.Join(", ", tools.Select(t => t.Name)) : "(none configured)")}.";

        var lineToken = "{line}"; // literal placeholder shown to the model

        return $"""
            You are a symbol-locator subagent. Your ONLY job is to find where a symbol, type,
            method, interface, or file is defined in the codebase.
            Working directory: {cwd}
            {toolPriority}

            Use at most {LocateMaxToolCalls} tool calls.
            Skip .fuseraft/ — it is fuseraft-cli runtime metadata, not application code.
            Reply in EXACTLY this format (one line per result):
              {cwd}/relative/path/to/file.ext:{lineToken} — brief description
            If not found after exhausting available tools, reply: "Not found."
            Do NOT implement, edit, or delete anything.
            """;
    }

    private static string BuildDelegatePrompt(IReadOnlyList<AIFunction> tools, string cwd)
    {
        var toolNames = tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toolList  = tools.Count > 0
            ? string.Join(", ", tools.Select(t => t.Name))
            : "(none configured)";

        return $"""
            You are a task-delegate subagent. You were handed a self-contained subtask by a
            parent agent that wants it completed without spending its own tool calls or context.
            Working directory: {cwd}
            Available tools: {toolList}

            Work autonomously to completion — you cannot ask the parent a clarifying question, so
            make the most reasonable interpretation of any ambiguity and proceed. Read relevant
            files before editing them. After writing or patching a file, re-read it to confirm the
            change is correct. If the task implies verification (build, tests, a specific command),
            run it and fix failures before finishing.
            Skip .fuseraft/ — it is fuseraft-cli runtime metadata, not application code.
            Avoid destructive or irreversible actions (force-push, deleting files/branches, `rm -rf`)
            and do not commit or push unless the task explicitly asks for it.
            {(toolNames.Contains("git_add") || toolNames.Contains("git_commit") ? "" : "You do not have git write access — leave any commits to the parent agent.\n")}
            When finished, reply with a concise summary: files changed (with paths), commands run
            and their outcome, and any follow-up the parent should know about. Do not paste full
            file contents or command output — summarize.
            """;
    }

    // --- Tool event wrapping ---

    private static IReadOnlyList<AIFunction> WrapWithNotifiers(
        IReadOnlyList<AIFunction> tools,
        EventEmitter emitter,
        string? agentName)
        => tools.Select(t => (AIFunction)new NotifyingAIFunction(
            t,
            agentName ?? string.Empty,
            (_, toolName, argsSummary) => emitter.EmitAsync(EventTypes.SubagentToolCall,
                agent:   agentName,
                payload: new { tool = toolName, args = argsSummary }))).ToList();
}
