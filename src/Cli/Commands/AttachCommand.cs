using System.ComponentModel;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Cli.Serve;
using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public sealed class AttachSettings : CommandSettings
{
    [CommandOption("--socket")]
    [Description("Path to the daemon's attach socket. Defaults to ~/.fuseraft/run/<project-hash>.sock.")]
    public string? Socket { get; set; }

    [CommandOption("--url")]
    [Description("Attach over WebSocket instead, e.g. ws://host:port/attach (printed by `fuseraft serve`). Sends FUSERAFT_SERVE_TOKEN as a bearer token when it's set.")]
    public string? Url { get; set; }

    [CommandOption("--cert-sha256")]
    [Description("With a wss:// --url: trust the daemon's certificate only if its SHA-256 fingerprint is this (`fuseraft serve --tls-cert` prints it). For a self-signed certificate; any other certificate is refused.")]
    public string? CertSha256 { get; set; }

    [CommandOption("--client-id")]
    [Description("With --url: attach as this client (1-64 letters, digits, '-' or '_'). Attaching again with the same id within the daemon's --reconnect-grace picks up your running task and its approvals, and shows the results that finished while you were away.")]
    public string? ClientId { get; set; }

    public override ValidationResult Validate()
    {
        if (Url is not null && Socket is not null)
            return ValidationResult.Error("Use either --socket or --url, not both.");
        if (Url is not null && !(Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme is "ws" or "wss"))
            return ValidationResult.Error($"--url must be a ws:// or wss:// URL, got: {Url}");
        if (CertSha256 is not null)
        {
            if (Url is null || !Url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                return ValidationResult.Error("--cert-sha256 needs a wss:// --url.");
            if (ServeTls.NormalizeFingerprint(CertSha256) is null)
                return ValidationResult.Error($"--cert-sha256 must be a SHA-256 fingerprint (64 hex digits), got: {CertSha256}");
        }
        if (ClientId is not null)
        {
            if (Url is null) return ValidationResult.Error("--client-id needs --url; the Unix socket has no client ids.");
            if (!ServeAttachClients.IsValidId(ClientId))
                return ValidationResult.Error($"--client-id must be 1-64 letters, digits, '-' or '_', got: {ClientId}");
        }
        return ValidationResult.Success();
    }
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
    public override async Task<int> ExecuteAsync(CommandContext context, AttachSettings settings, CancellationToken cancellationToken)
    {
        var (stream, target) = settings.Url is not null
            ? await ConnectWebSocketAsync(settings.Url, settings.CertSha256, settings.ClientId, cancellationToken)
            : await ConnectSocketAsync(settings.Socket, cancellationToken);
        if (stream is null) return 1;

        AnsiConsole.MarkupLine($"[dim]Attached → {Markup.Escape(target)}. Type a task and press Enter. Ctrl+C to detach.[/]");

        await using var _ = stream;
        using var reader = new StreamReader(stream);
        await using var writer = new StreamWriter(stream) { AutoFlush = true };

        // Owns Console.In for the whole session — see class doc comment.
        var consolePump = new ReplStdinPump(Console.In, getActiveCts: () => null);
        consolePump.Start();

        // At most one task is ever pending from this connection at a time (the foreground loop
        // below never dispatches a second one before the first resolves), so a single shared slot
        // — not a per-session map — is enough to hand the result back from the reader loop.
        PendingTask? pendingResult = null;
        // The hello says whether this client id already has a task running; the prompt waits for it.
        var hello = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readOnly = false;

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
                    pendingResult?.Done.TrySetResult();
                    hello.TrySetResult();
                    return;
                }

                if (!hello.Task.IsCompleted)
                {
                    readOnly = IsReadOnly(line);
                    if (PickedUp(line) is { } sessionId)
                    {
                        AnsiConsole.MarkupLine($"[dim]Picked up your task → session {Markup.Escape(sessionId)}, still running.[/]");
                        pendingResult = new PendingTask { SessionId = sessionId };
                    }
                    hello.TrySetResult();
                }
                await RenderServerLineAsync(line, writer, consolePump, pendingResult);
            }
        }, cancellationToken);

        await Task.WhenAny(hello.Task, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
        if (pendingResult is { } pickedUp) await Task.WhenAny(pickedUp.Done.Task, readerLoop);
        if (readOnly)
        {
            AnsiConsole.MarkupLine("[dim]This token is read-only: watching the daemon's progress. Ctrl+C to detach.[/]");
            await readerLoop;
            return 0;
        }

        while (!readerLoop.IsCompleted)
        {
            AnsiConsole.Markup("[bold]>[/] ");
            // Stop waiting at the prompt when the daemon goes away, rather than on the next Enter.
            var read = consolePump.ReadInputAsync();
            if (await Task.WhenAny(read, readerLoop) == readerLoop) break;
            var task = await read;
            if (task is null) break; // stdin closed
            if (string.IsNullOrWhiteSpace(task)) continue;

            pendingResult = new PendingTask();
            try { await writer.WriteLineAsync(task); }
            catch { break; } // connection already gone

            await pendingResult.Done.Task;
            if (readerLoop.IsCompleted) break; // connection dropped while we were waiting
        }

        return 0;
    }

    private static async Task<(Stream? Stream, string Target)> ConnectSocketAsync(string? socketOverride, CancellationToken ct)
    {
        var socketPath = socketOverride ?? FuseraftPaths.DaemonSocketPath(FuseraftPaths.ProjectSlug(Directory.GetCurrentDirectory()));
        if (!File.Exists(socketPath))
        {
            AnsiConsole.MarkupLine($"[red]✗ No daemon socket found at:[/] {Markup.Escape(socketPath)}");
            AnsiConsole.MarkupLine("[dim]Start one with `fuseraft serve`.[/]");
            return (null, socketPath);
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
        }
        catch (Exception ex)
        {
            socket.Dispose();
            AnsiConsole.MarkupLine($"[red]✗ Could not connect:[/] {Markup.Escape(ex.Message)}");
            return (null, socketPath);
        }
        return (new NetworkStream(socket, ownsSocket: true), socketPath);
    }

    /// <summary>Whether a hello says this connection's token is read-only.</summary>
    internal static bool IsReadOnly(string helloLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(helloLine);
            return doc.RootElement.TryGetProperty("readOnly", out var r) && r.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Whether the connection failed because the daemon's certificate wasn't trusted, however deep that's wrapped.</summary>
    internal static bool IsTlsFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is System.Security.Authentication.AuthenticationException) return true;
        return false;
    }

    /// <summary>The session id of the running task a hello says this client already has, if any.</summary>
    internal static string? PickedUp(string helloLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(helloLine);
            var root = doc.RootElement;
            return root.TryGetProperty("type", out var t) && t.GetString() == "hello" &&
                   root.TryGetProperty("inFlight", out var inFlight) && inFlight.ValueKind == JsonValueKind.Object
                ? GetString(inFlight, "sessionId")
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static async Task<(Stream? Stream, string Target)> ConnectWebSocketAsync(
        string url, string? certSha256, string? clientId, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = ServeHttpHost.KeepAliveInterval;
        socket.Options.KeepAliveTimeout  = ServeHttpHost.KeepAliveTimeout;
        if (certSha256 is not null)
            socket.Options.RemoteCertificateValidationCallback = ServeTls.Pinned(ServeTls.NormalizeFingerprint(certSha256)!);
        if (clientId is not null)
            socket.Options.SetRequestHeader(ServeAttachClients.ClientIdHeader, clientId);
        if (Environment.GetEnvironmentVariable(ServeHttpHost.TokenVariable) is { Length: > 0 } token)
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        try
        {
            await socket.ConnectAsync(new Uri(url), ct);
        }
        catch (Exception ex)
        {
            socket.Dispose();
            AnsiConsole.MarkupLine($"[red]✗ Could not connect:[/] {Markup.Escape(ex.Message)}");
            if (IsTlsFailure(ex))
                AnsiConsole.MarkupLine(certSha256 is null
                    ? "[dim]The daemon's certificate isn't trusted here. For a self-signed one, pass the fingerprint it prints with --cert-sha256.[/]"
                    : "[dim]The daemon's certificate doesn't match --cert-sha256.[/]");
            if (ex is WebSocketException { InnerException: null } && Environment.GetEnvironmentVariable(ServeHttpHost.TokenVariable) is null or "")
                AnsiConsole.MarkupLine($"[dim]If the daemon was started with {ServeHttpHost.TokenVariable}, set it here too.[/]");
            return (null, url);
        }
        return (new WebSocketLineStream(socket, ownsSocket: true), url);
    }

    /// <summary>The task this connection waits on before prompting again; its session id comes from "queued" when it isn't known.</summary>
    private sealed class PendingTask
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? SessionId { get; set; }
    }

    /// <summary>Renders one JSONL line from the daemon; resolves <paramref name="pendingResult"/> on its "result".</summary>
    private static async Task RenderServerLineAsync(
        string line, TextWriter writer, ReplStdinPump consolePump, PendingTask? pendingResult)
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
            case "hello":
                break;

            case "queued":
            {
                var position = GetNullableInt(root, "position");
                var suffix = position is null or 0 ? "running next" : $"{position} ahead";
                if (pendingResult is { SessionId: null }) pendingResult.SessionId = GetString(root, "sessionId");
                AnsiConsole.MarkupLine($"[dim]queued → session {Markup.Escape(GetString(root, "sessionId"))} ({suffix})[/]");
                break;
            }

            case "auto_dispatched":
                AnsiConsole.MarkupLine(
                    $"[cyan]⚙ daemon self-dispatched[/] from {Markup.Escape(GetString(root, "objectiveId"))}: " +
                    $"{Markup.Escape(GetString(root, "task"))} [dim](session {Markup.Escape(GetString(root, "sessionId"))})[/]");
                break;

            case "task_resumed":
                AnsiConsole.MarkupLine($"[cyan]↻ resuming session {Markup.Escape(GetString(root, "sessionId"))} after the daemon restarted:[/] {Markup.Escape(GetString(root, "task"))}");
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
                var label = GetString(root, "task") is { Length: > 0 } finished && pendingResult?.SessionId != GetString(root, "sessionId")
                    ? $" [dim](session {Markup.Escape(GetString(root, "sessionId"))}: {Markup.Escape(finished)})[/]"
                    : "";
                AnsiConsole.MarkupLine(succeeded
                    ? $"[green]✓ done[/]{label}"
                    : $"[red]✗ failed:[/] {Markup.Escape(GetString(root, "errorMessage"))}{label}");
                if (GetString(root, "lastMessage") is { Length: > 0 } lastMessage)
                    AnsiConsole.MarkupLine(Markup.Escape(lastMessage));
                // A result that finished while this client was away isn't the one it's waiting on.
                if (pendingResult is not null && (pendingResult.SessionId is null || pendingResult.SessionId == GetString(root, "sessionId")))
                    pendingResult.Done.TrySetResult();
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
