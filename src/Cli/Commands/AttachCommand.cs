using System.ComponentModel;
using System.Net.Sockets;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public sealed class AttachSettings : CommandSettings
{
    [CommandOption("--socket")]
    [Description("Path to the daemon's attach socket. Defaults to ~/.fuseraft/run/<project-hash>.sock.")]
    public string? Socket { get; set; }
}

/// <summary>
/// Thin client for a running <c>fuseraft serve</c> daemon: connects to its Unix domain socket,
/// sends whatever you type as a task, and renders whatever comes back — live progress for
/// *any* task currently running (not just your own — the daemon broadcasts it to every attached
/// connection), approval prompts for mutating tool calls on tasks you dispatched (which you
/// answer right here), and a final result. Bare-bones rendering (no rich REPL-style formatting);
/// see <see cref="fuseraft.Cli.Serve.ServeSocketListener"/> for the wire protocol.
///
/// <para>
/// Two concurrent loops share one <see cref="ReplStdinPump"/>-wrapped <c>Console.In</c> (the same
/// pattern <c>ReplCommand</c>'s VS Code bridge uses, and for the same reason — a single reader
/// must own stdin so two logical consumers never race to read it): a background loop reads and
/// renders every socket line, including live progress that can arrive while this connection is
/// sitting idle at its own prompt; the foreground loop reads the console for the next task,
/// dispatches it, then waits for that specific dispatch's "result" before prompting again — it
/// never reads the console again until then, so it can never contend with the background loop's
/// own console read for an approval-prompt answer.
/// </para>
/// </summary>
public sealed class AttachCommand : AsyncCommand<AttachSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, AttachSettings settings, CancellationToken cancellationToken)
    {
        var projectSlug = FuseraftPaths.ProjectSlug(Directory.GetCurrentDirectory());
        var socketPath  = settings.Socket ?? FuseraftPaths.DaemonSocketPath(projectSlug);

        if (!File.Exists(socketPath))
        {
            AnsiConsole.MarkupLine($"[red]✗ No daemon socket found at:[/] {Markup.Escape(socketPath)}");
            AnsiConsole.MarkupLine("[dim]Start one with `fuseraft serve`.[/]");
            return 1;
        }

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Could not connect:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[dim]Attached → {Markup.Escape(socketPath)}. Type a task and press Enter. Ctrl+C to detach.[/]");

        await using var stream = new NetworkStream(socket, ownsSocket: true);
        using var reader = new StreamReader(stream);
        await using var writer = new StreamWriter(stream) { AutoFlush = true };

        // Owns Console.In for the whole session — see class doc comment.
        var consolePump = new ReplStdinPump(Console.In, getActiveCts: () => null);
        consolePump.Start();

        // At most one task is ever pending from this connection at a time (the foreground loop
        // below never dispatches a second one before the first resolves), so a single shared slot
        // — not a per-session map — is enough to hand the result back from the reader loop.
        TaskCompletionSource<bool>? pendingResult = null;

        var readerLoop = Task.Run(async () =>
        {
            while (true)
            {
                string? line;
                try { line = await reader.ReadLineAsync(cancellationToken); }
                catch { line = null; }

                if (line is null)
                {
                    AnsiConsole.MarkupLine("\n[dim]Daemon closed the connection.[/]");
                    pendingResult?.TrySetResult(true);
                    return;
                }

                await RenderServerLineAsync(line, writer, consolePump, pendingResult);
            }
        }, cancellationToken);

        while (true)
        {
            AnsiConsole.Markup("[bold]>[/] ");
            var task = await consolePump.ReadInputAsync();
            if (task is null) break; // stdin closed
            if (string.IsNullOrWhiteSpace(task)) continue;

            pendingResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try { await writer.WriteLineAsync(task); }
            catch { break; } // connection already gone

            await pendingResult.Task;
            if (readerLoop.IsCompleted) break; // connection dropped while we were waiting
        }

        return 0;
    }

    /// <summary>Renders one JSONL line from the daemon; resolves <paramref name="pendingResult"/> on "result".</summary>
    private static async Task RenderServerLineAsync(
        string line, TextWriter writer, ReplStdinPump consolePump, TaskCompletionSource<bool>? pendingResult)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(line).RootElement; }
        catch (JsonException)
        {
            AnsiConsole.MarkupLine(Markup.Escape(line));
            return;
        }

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "queued":
            {
                var position = GetNullableInt(root, "position");
                var suffix = position is null or 0 ? "running next" : $"{position} ahead";
                AnsiConsole.MarkupLine($"[dim]queued → session {Markup.Escape(GetString(root, "sessionId"))} ({suffix})[/]");
                break;
            }

            case "auto_dispatched":
                AnsiConsole.MarkupLine(
                    $"[cyan]⚙ daemon self-dispatched[/] from {Markup.Escape(GetString(root, "objectiveId"))}: " +
                    $"{Markup.Escape(GetString(root, "task"))} [dim](session {Markup.Escape(GetString(root, "sessionId"))})[/]");
                break;

            case "agent_starting":
                AnsiConsole.MarkupLine($"[dim]→ {Markup.Escape(GetString(root, "agent"))} is working...[/]");
                break;

            case "tool_calling":
            {
                var args = GetString(root, "argsSummary");
                var suffix = args.Length > 0 ? $"({args})" : "()";
                AnsiConsole.MarkupLine($"[dim]  {Markup.Escape(GetString(root, "agent"))} → {Markup.Escape(GetString(root, "tool"))}{Markup.Escape(suffix)}[/]");
                break;
            }

            case "token_budget_warning":
                AnsiConsole.MarkupLine(
                    $"[yellow]⚠ {Markup.Escape(GetString(root, "agent"))} approaching context budget " +
                    $"({GetNullableInt(root, "inputTokens")}/{GetNullableInt(root, "warnThreshold")} tokens)[/]");
                break;

            case "approval_request":
                AnsiConsole.MarkupLine($"[yellow]Approval requested[/] ({Markup.Escape(GetString(root, "kind"))}):");
                AnsiConsole.MarkupLine(Markup.Escape(line));
                AnsiConsole.Markup("[bold]Approve? (y/N):[/] ");
                var answer = await consolePump.ReadInputAsync();
                var approved = answer?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true;
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "approval_response", approved }));
                break;

            case "result":
                var succeeded = root.TryGetProperty("succeeded", out var s) && s.ValueKind == JsonValueKind.True;
                AnsiConsole.MarkupLine(succeeded
                    ? "[green]✓ done[/]"
                    : $"[red]✗ failed:[/] {Markup.Escape(GetString(root, "errorMessage"))}");
                if (GetString(root, "lastMessage") is { Length: > 0 } lastMessage)
                    AnsiConsole.MarkupLine(Markup.Escape(lastMessage));
                pendingResult?.TrySetResult(true);
                break;

            case "error":
                AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(GetString(root, "text"))}");
                break;

            default:
                AnsiConsole.MarkupLine(Markup.Escape(line));
                break;
        }
    }

    private static string GetString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int? GetNullableInt(JsonElement root, string property) =>
        root.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}
