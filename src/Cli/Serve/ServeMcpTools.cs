using System.ComponentModel;

namespace fuseraft.Cli.Serve;

/// <summary>
/// The daemon's MCP-facing tool surface — how another agent dispatches work into a running
/// <c>fuseraft serve</c> process and polls for the outcome. Plain <c>[Description]</c>-annotated
/// methods, wrapped via <c>AIFunctionFactory.Create</c> then <c>McpServerTool.Create(AIFunction)</c>
/// in <see cref="ServeHost"/> — the identical pipeline every other fuseraft plugin tool goes
/// through (see <c>PluginRegistry.GetFunctionsFromObject</c>), not a bespoke MCP-only convention.
///
/// <para>
/// Dispatch is fire-and-forget: fuseraft's orchestrators process one task at a time against one
/// shared conversation history (see <see cref="ServeTaskQueue"/>), so a long-running task cannot
/// hold an MCP call open — <c>dispatch_task</c> returns immediately with a session ID and the
/// caller polls <c>get_status</c>/<c>get_result</c>.
/// </para>
/// </summary>
public sealed class ServeMcpTools(ServeTaskQueue queue)
{
    [Description("Dispatch a new task into the running fuseraft agent team. Returns immediately " +
                 "with a session ID — poll get_status/get_result for progress and the final result.")]
    public object DispatchTask(
        [Description("The task to run, in natural language.")] string task,
        [Description("Optional objective ID (e.g. OBJ-0001, from `fuseraft objective list`) to link this task's progress against.")]
        string? objectiveId = null)
    {
        var sessionId = queue.Enqueue(task, objectiveId);
        return new { sessionId, status = "queued", position = queue.GetQueuePosition(sessionId) };
    }

    [Description("Check the status of a task previously dispatched with dispatch_task.")]
    public object GetStatus(
        [Description("Session ID returned by dispatch_task.")] string sessionId)
    {
        var record = queue.TryGet(sessionId);
        if (record is null)
            return new { status = "unknown", error = $"No task found for session '{sessionId}'." };

        return new
        {
            status = record.Status.ToString().ToLowerInvariant(),
            position = queue.GetQueuePosition(sessionId),
            turnCount = record.TurnCount,
            lastAgent = record.LastAgent,
            lastMessage = record.LastMessageExcerpt,
        };
    }

    [Description("Retrieve the final result of a task dispatched with dispatch_task. Returns " +
                 "status \"queued\" or \"running\" if the task has not finished yet — call " +
                 "get_status first, or poll this again.")]
    public object GetResult(
        [Description("Session ID returned by dispatch_task.")] string sessionId)
    {
        var record = queue.TryGet(sessionId);
        if (record is null)
            return new { status = "unknown", error = $"No task found for session '{sessionId}'." };

        if (record.Status is ServeTaskStatus.Queued or ServeTaskStatus.Running)
            return new { status = record.Status.ToString().ToLowerInvariant() };

        return new
        {
            status = record.Status.ToString().ToLowerInvariant(),
            succeeded = record.Succeeded,
            errorMessage = record.ErrorMessage,
            messages = record.Messages?.Select(m => new
            {
                agent = m.AgentName,
                role = m.Role,
                content = m.Content,
            }),
        };
    }
}
