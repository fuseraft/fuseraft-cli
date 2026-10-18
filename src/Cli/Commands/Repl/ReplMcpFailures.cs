using Microsoft.Extensions.AI;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Puts a failed MCP call to the model in words. A call that throws — the server's process exited, its connection
/// dropped, it answered with a protocol error — otherwise reaches the model as the invocation loop's bare
/// "Error: Function failed.", which hides the exception's text, so a model whose server had gone kept calling its
/// tools. A failure now comes back as an <c>[ERROR]</c> result with that text, and when the server is no longer
/// running, it says so and that the user can reconnect it.
/// </summary>
internal static class ReplMcpFailures
{
    public static List<AIFunction> Wrap(string server, IEnumerable<AIFunction> tools, Func<bool> isRunning) =>
        [.. tools.Select(t => (AIFunction)new ReportingTool(t, server, isRunning))];

    internal static string Describe(string server, string tool, string error, bool running) => running
        ? $"[ERROR] MCP tool '{tool}' on '{server}' failed: {error}"
        : $"[ERROR] MCP server '{server}' is no longer running ({error}), so none of its tools will work. " +
          $"Don't call them again; tell the user, who can reconnect it with /mcp reconnect {server}.";

    private sealed class ReportingTool(AIFunction inner, string server, Func<bool> isRunning) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            // Not sent at all once the session has ended: depending on the transport, a request to a server
            // that's gone may never be answered.
            if (!isRunning())
                return Describe(server, Name, "its session has ended", running: false);
            try
            {
                return await base.InvokeCoreAsync(arguments, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                return Describe(server, Name, ex.Message, isRunning());
            }
        }
    }
}
