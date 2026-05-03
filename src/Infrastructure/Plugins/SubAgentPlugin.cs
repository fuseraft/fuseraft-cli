using System.ComponentModel;
using Microsoft.Extensions.AI;
using fuseraft.Orchestration;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Provides a lightweight codebase-exploration sub-agent that any pipeline agent can
/// delegate file-gathering work to.
///
/// <para>
/// When Developer (or any agent) needs to understand many files before implementing a
/// change, calling <see cref="ExploreAsync"/> is far cheaper than reading those files
/// directly: the sub-agent runs its own contained agentic loop — using only FileSystem
/// and Search tools — and returns a concise prose summary. The caller's context window
/// never sees raw file contents; it only sees the distilled answer.
/// </para>
///
/// <para>
/// The sub-agent's loop is driven by <c>FunctionInvokingChatClient</c> from
/// <c>Microsoft.Extensions.AI</c>, which handles tool calls internally and stops as soon
/// as the model produces a non-tool-call response. A hard timeout of
/// <see cref="ExploreTimeoutMinutes"/> minutes protects against runaway loops.
/// </para>
///
/// <para>
/// Per-agent instances are created in <see cref="fuseraft.Infrastructure.AgentFactory"/>
/// using the parent agent's resolved model and a sandboxed <see cref="FileSystemPlugin"/>.
/// A stub is registered in <see cref="PluginRegistry.RegisterDefaults"/> so that
/// <c>fuseraft plugins</c> can enumerate the tool name and description.
/// </para>
/// </summary>
public sealed class SubAgentPlugin(
    IChatClient? chatClient,
    IReadOnlyList<AIFunction> explorerTools,
    int maxOutputTokens = 2048,
    EventEmitter? eventEmitter = null,
    string? parentAgentName = null)
{
    private const double ExploreTimeoutMinutes = 8.0;

    private const string SystemPrompt =
        """
        You are a codebase explorer sub-agent. Your ONLY job is to answer the query you are given.
        Available tools (depending on parent configuration): read_file, list_files, grep_file,
        get_file_summary, get_file_info, search_files, search_content, search_symbol,
        shell_run, shell_get_env, shell_which, shell_get_working_directory,
        git_status, git_diff, git_log, git_show, git_branch_list.
        Use shell_run to run build commands or tests when needed to answer the query — it helps verify
        current state rather than guessing from source alone.
        Aim to answer within 15 tool calls using targeted queries.
        Do NOT implement, edit, delete, commit, or push anything. Never run mutating shell commands
        (no git add, git commit, rm, mv, write_file, patch_file, etc.).
        When you have gathered enough information, write a focused summary (under 600 words) that
        directly answers the query, then stop — do not call any further tools after your summary.
        """;

    [Description("Delegate codebase exploration to a sub-agent. Returns a concise summary.")]
    public async Task<string> ExploreAsync(
        [Description("Exploration question or task.")]
        string query)
    {
        if (chatClient is null)
            return "[SubAgent] No chat client configured — this is a stub instance. " +
                   "Ensure AgentFactory created a real SubAgentPlugin for this agent.";

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync("sub_agent_start",
                agent:   parentAgentName,
                payload: new { query = query.Length > 120 ? query[..120] + "…" : query });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(ExploreTimeoutMinutes));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, query),
        };

        var loopClient = chatClient.AsBuilder().UseFunctionInvocation().Build();

        var options = new ChatOptions
        {
            Tools           = explorerTools.Cast<AITool>().ToList(),
            ToolMode        = ChatToolMode.Auto,
            MaxOutputTokens = maxOutputTokens,
        };

        string outcome = "completed";
        try
        {
            var response = await loopClient.GetResponseAsync(messages, options, cts.Token);
            var result = string.IsNullOrWhiteSpace(response.Text)
                ? "Explorer sub-agent produced no text output."
                : response.Text;

            if (eventEmitter is not null)
                await eventEmitter.EmitAsync("sub_agent_end",
                    agent:   parentAgentName,
                    payload: new { outcome, summary_chars = result.Length });

            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = "timeout";
            if (eventEmitter is not null)
                await eventEmitter.EmitAsync("sub_agent_end",
                    agent:   parentAgentName,
                    payload: new { outcome });
            return $"Explorer sub-agent timed out after {ExploreTimeoutMinutes} minutes.";
        }
        catch (Exception ex)
        {
            outcome = "error";
            if (eventEmitter is not null)
                await eventEmitter.EmitAsync("sub_agent_end",
                    agent:   parentAgentName,
                    payload: new { outcome, error = ex.Message });
            return $"Explorer sub-agent failed: {ex.Message}";
        }
    }
}
