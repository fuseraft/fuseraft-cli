using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using fuseraft.Orchestration;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Provides two lightweight sub-agent tools that any pipeline agent can delegate work to:
///
/// <list type="bullet">
///   <item><see cref="ExploreAsync"/> — multi-hop exploration loop for broad codebase
///     questions. Runs up to <c>maxToolCalls</c> iterations and returns a concise prose
///     summary or a bulleted file list, depending on the requested <c>format</c>.</item>
///   <item><see cref="LocateAsync"/> — tight 5-iteration loop for single-target symbol,
///     type, or file lookups. Returns a <c>path:line</c> result without filling the
///     caller's context window.</item>
/// </list>
///
/// <para>
/// Both loops use <c>FunctionInvokingChatClient</c> with an enforced
/// <c>MaximumIterationsPerRequest</c> cap. The parent agent's <see cref="CancellationToken"/>
/// is linked to a per-call timeout so interrupts propagate immediately.
/// </para>
///
/// <para>
/// When an <see cref="EventEmitter"/> is provided, each tool call inside the sub-agent loop
/// emits a <c>sub_agent_tool_call</c> event so activity is visible between
/// <c>sub_agent_start</c> and <c>sub_agent_end</c>.
/// </para>
///
/// <para>
/// Per-agent instances are created in <see cref="fuseraft.Infrastructure.AgentFactory"/>
/// using the parent agent's resolved model and a sandboxed <see cref="FileSystemPlugin"/>.
/// A stub is registered in <see cref="PluginRegistry.RegisterDefaults"/> so that
/// <c>fuseraft plugins</c> can enumerate the tool names and descriptions.
/// </para>
/// </summary>
public sealed class SubAgentPlugin(
    IChatClient? chatClient,
    IReadOnlyList<AIFunction> explorerTools,
    int maxOutputTokens = 2048,
    EventEmitter? eventEmitter = null,
    string? parentAgentName = null,
    int maxToolCalls = 0)
{
    private const double ExploreTimeoutMinutes = 8.0;
    private const int DefaultMaxToolCalls = 20;
    private const int LocateMaxToolCalls = 5;
    private const int LocateMaxOutputTokens = 512;

    // Wrap tools with event-emitting proxies so sub-agent tool activity is visible in the
    // event log between sub_agent_start and sub_agent_end.
    private readonly IReadOnlyList<AIFunction> _tools =
        eventEmitter is not null
            ? WrapWithNotifiers(explorerTools, eventEmitter, parentAgentName)
            : explorerTools;

    private readonly int _effectiveMaxToolCalls =
        maxToolCalls > 0 ? maxToolCalls : DefaultMaxToolCalls;

    // --- Public tools ---

    [Description("Broad codebase exploration. Returns a prose summary or file list. Use for multi-hop questions (e.g. 'Which files handle X?', 'What conventions does this repo use?').")]
    public Task<string> ExploreAsync(
        [Description("Exploration question or task.")]
        string query,
        [Description("Output format: 'prose' (default, narrative summary) or 'file_list' (bulleted list of relevant file paths with one-line roles).")]
        string format = "prose",
        CancellationToken cancellationToken = default)
        => RunLoopAsync(
            BuildExplorePrompt(_tools, _effectiveMaxToolCalls, format),
            query,
            _effectiveMaxToolCalls,
            maxOutputTokens,
            "explore",
            cancellationToken);

    [Description("Locate where a symbol, type, method, interface, or file is defined. Returns file path and line number. Prefer over explore for single-target lookups.")]
    public Task<string> LocateAsync(
        [Description("Symbol, type, interface, method, or filename to locate (e.g. 'IOrchestrationHook', 'AgentFactory.Create', 'EventEmitter.cs').")]
        string target,
        CancellationToken cancellationToken = default)
        => RunLoopAsync(
            BuildLocatePrompt(_tools),
            $"Locate: {target}",
            LocateMaxToolCalls,
            LocateMaxOutputTokens,
            "locate",
            cancellationToken);

    // Single-turn session diagnosis — not a model tool (no [Description]).
    // Reads the REPL conversation history, identifies where things are going wrong, and returns
    // a corrective instruction addressed to the REPL agent for injection as a user message.
    // Returns null when the diagnoser produces no output or the call fails/times out.
    public async Task<string?> DiagnoseAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        if (chatClient is null) return null;

        const string diagnosticSystem =
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

        const int msgCap = 800;
        var transcript = new StringBuilder();
        foreach (var m in history)
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

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var response = await chatClient.GetResponseAsync(messages, options, cts.Token);
            var text     = (response.Text ?? string.Empty).Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch { return null; }
    }

    // Single-turn critic review — not a model tool (no [Description]).
    // Returns (true, null) when the step is approved, (false, reason) when rejected.
    // Degrades gracefully on timeout or error so a critic failure never blocks execution.
    public async Task<(bool Approved, string? Reason)> CriticReviewAsync(
        string stepDescription,
        string? expectedTool,
        IReadOnlyList<string> toolsCalled,
        string agentResponse,
        CancellationToken cancellationToken = default)
    {
        if (chatClient is null)
            return (true, null);

        const string criticSystem =
            "You are a strict plan-step critic. You receive a step description, the tools the " +
            "agent called, and the agent's response. Judge whether the step was completed " +
            "correctly and completely.\n" +
            "If it was, respond with exactly:\nAPPROVED\n\n" +
            "Otherwise, describe the specific defect in one or two sentences. Be precise — " +
            "state what is wrong or missing, not just that something is wrong.";

        var toolsStr     = toolsCalled.Count > 0 ? string.Join(", ", toolsCalled) : "(none)";
        var expectedStr  = expectedTool is not null ? $"\nExpected tool: {expectedTool}" : string.Empty;
        var userMsg      =
            $"Step: {stepDescription}{expectedStr}\n" +
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
        catch
        {
            return (true, null);
        }
    }

    // Streaming variants — not registered as model tools (no [Description]).
    // onChunk is called for each text token as the final answer arrives.

    public Task<string> ExploreStreamingAsync(
        string query,
        Func<string, Task> onChunk,
        string format = "prose",
        CancellationToken cancellationToken = default)
        => RunLoopAsync(
            BuildExplorePrompt(_tools, _effectiveMaxToolCalls, format),
            query,
            _effectiveMaxToolCalls,
            maxOutputTokens,
            "explore",
            cancellationToken,
            onChunk);

    public Task<string> LocateStreamingAsync(
        string target,
        Func<string, Task> onChunk,
        CancellationToken cancellationToken = default)
        => RunLoopAsync(
            BuildLocatePrompt(_tools),
            $"Locate: {target}",
            LocateMaxToolCalls,
            LocateMaxOutputTokens,
            "locate",
            cancellationToken,
            onChunk);

    // --- Core loop (shared by both tools) ---

    private async Task<string> RunLoopAsync(
        string systemPrompt,
        string userQuery,
        int maxIterations,
        int outputTokens,
        string mode,
        CancellationToken cancellationToken,
        Func<string, Task>? onChunk = null)
    {
        if (chatClient is null)
            return "[SubAgent] No chat client configured — this is a stub instance. " +
                   "Ensure AgentFactory created a real SubAgentPlugin for this agent.";

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync("sub_agent_start",
                agent:   parentAgentName,
                payload: new { query = userQuery.Length > 120 ? userQuery[..120] + "…" : userQuery, mode });

        // Link the parent's CT so cancellation propagates immediately; timeout is a safety net.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(ExploreTimeoutMinutes));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User,   userQuery),
        };

        var loopClient = chatClient.AsBuilder()
            .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = maxIterations)
            .Build();

        var options = new ChatOptions
        {
            Tools           = _tools.Cast<AITool>().ToList(),
            ToolMode        = ChatToolMode.Auto,
            MaxOutputTokens = outputTokens,
        };

        string outcome = "completed";
        try
        {
            string result;
            if (onChunk is not null)
            {
                var sb = new StringBuilder();
                await foreach (var update in loopClient.GetStreamingResponseAsync(messages, options, cts.Token))
                {
                    var text = update.Text;
                    if (!string.IsNullOrEmpty(text))
                    {
                        sb.Append(text);
                        await onChunk(text);
                    }
                }
                result = sb.Length > 0 ? sb.ToString() : "Sub-agent produced no text output.";
            }
            else
            {
                var response = await loopClient.GetResponseAsync(messages, options, cts.Token);
                result = string.IsNullOrWhiteSpace(response.Text)
                    ? "Sub-agent produced no text output."
                    : response.Text;
            }

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync("sub_agent_end",
                    agent:   parentAgentName,
                    payload: new { outcome, summary_chars = result.Length, mode });

            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
            if (eventEmitter is not null)
                await eventEmitter.EmitAsync("sub_agent_end",
                    agent:   parentAgentName,
                    payload: new { outcome, mode });
            return outcome == "cancelled"
                ? "Sub-agent was cancelled."
                : $"Sub-agent timed out after {ExploreTimeoutMinutes} minutes.";
        }
        catch (Exception ex)
        {
            outcome = "error";
            if (eventEmitter is not null)
                await eventEmitter.EmitAsync("sub_agent_end",
                    agent:   parentAgentName,
                    payload: new { outcome, error = ex.Message, mode });
            return $"Sub-agent failed: {ex.Message}";
        }
    }

    // --- Prompt builders ---

    private static string BuildExplorePrompt(
        IReadOnlyList<AIFunction> tools,
        int maxToolCalls,
        string format)
    {
        var toolList = tools.Count > 0
            ? string.Join(", ", tools.Select(t => t.Name))
            : "(none configured)";
        var cwd = Directory.GetCurrentDirectory();

        var outputInstructions = format.ToLowerInvariant() == "file_list"
            ? """
              Output format — return ONLY a markdown bulleted list of relevant files:
                - relative/path/to/file.ext — one-line role description
              No prose paragraphs. Sort most-relevant first.
              """
            : "Write a focused prose summary (under 600 words) that directly answers the query, then stop.";

        return $"""
            You are a codebase explorer sub-agent. Your ONLY job is to answer the query you are given.
            Working directory: {cwd}
            Available tools: {toolList}.

            Tool selection priority (prefer earlier options when they suffice):
            1. search_symbol  — type, method, interface, or class definitions.
            2. search_files   — file discovery by name pattern.
            3. search_content — content patterns across the codebase.
            4. get_file_summary — before read_file on any file you have not confirmed is relevant.
            5. grep_file      — targeted in-file content search.
            6. read_file      — actual implementation; only when summary is insufficient.
            7. shell_run      — verify a specific hypothesis (build, test); never for browsing.

            Aim to answer within {maxToolCalls} tool calls using targeted queries.
            Do NOT implement, edit, delete, commit, or push anything.
            Never run mutating shell commands (no git add, git commit, rm, mv, write_file, etc.).
            {outputInstructions}
            """;
    }

    private static string BuildLocatePrompt(IReadOnlyList<AIFunction> tools)
    {
        var toolList = tools.Count > 0
            ? string.Join(", ", tools.Select(t => t.Name))
            : "(none configured)";
        var cwd        = Directory.GetCurrentDirectory();
        var lineToken  = "{line}"; // literal placeholder shown to the model

        return $"""
            You are a symbol-locator sub-agent. Your ONLY job is to find where a symbol, type,
            method, interface, or file is defined in the codebase.
            Working directory: {cwd}
            Available tools: {toolList}.

            Tool priority (use the cheapest that works; stop the moment you have the answer):
            1. search_symbol  — first choice for types, methods, interfaces, class names.
            2. search_files   — for filenames or path patterns.
            3. search_content / grep_file — for string patterns when search_symbol is insufficient.
            4. read_file      — only to confirm the exact line number once the file is known.

            Use at most {LocateMaxToolCalls} tool calls.
            Reply in EXACTLY this format (one line per result):
              {cwd}/relative/path/to/file.ext:{lineToken} — brief description
            If not found after exhausting available tools, reply: "Not found."
            Do NOT implement, edit, or delete anything.
            """;
    }

    // --- Tool event wrapping ---

    private static IReadOnlyList<AIFunction> WrapWithNotifiers(
        IReadOnlyList<AIFunction> tools,
        EventEmitter emitter,
        string? agentName)
        => tools.Select(t => (AIFunction)new ToolEventNotifier(t, emitter, agentName)).ToList();

    // Transparent proxy that fires a sub_agent_tool_call event the moment a tool begins
    // executing, making sub-agent activity visible between sub_agent_start and sub_agent_end.
    private sealed class ToolEventNotifier(AIFunction inner, EventEmitter emitter, string? agentName)
        : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            await emitter.EmitAsync("sub_agent_tool_call",
                agent:   agentName,
                payload: new { tool = Name, args = SummarizeArgs(arguments) });
            return await InnerFunction.InvokeAsync(arguments, cancellationToken);
        }

        private static string? SummarizeArgs(AIFunctionArguments? args)
        {
            if (args is null) return null;
            ReadOnlySpan<string> priority = ["path", "command", "script", "url", "key", "query", "message", "branch"];
            foreach (var key in priority)
            {
                var match = args.FirstOrDefault(kv =>
                    string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
                if (match.Value is not null)
                {
                    var val = match.Value.ToString() ?? string.Empty;
                    return $"{key}={System.Net.WebUtility.HtmlDecode(val.Length > 60 ? val[..60] : val)}";
                }
            }
            var first = args.FirstOrDefault();
            if (first.Value is null) return null;
            var fv = first.Value.ToString() ?? string.Empty;
            return $"{first.Key}={System.Net.WebUtility.HtmlDecode(fv.Length > 60 ? fv[..60] : fv)}";
        }
    }
}
