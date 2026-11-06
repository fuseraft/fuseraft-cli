using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using fuseraft.Core.Images;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Owns stdin for the life of a JSON-bridge REPL session (<c>fuseraft repl --vscode</c>). A
/// single background loop is the only thing that ever reads from it once <see cref="Start"/> is
/// called; everything else consumes lines through <see cref="ReadInputAsync"/>,
/// <see cref="ReadApprovalResponseAsync"/> and the like instead of touching the underlying reader directly.
///
/// This exists because of how the "Stop" button has to work on Windows. There's no way to
/// deliver a real SIGINT to a child process there, so the extension sends the interrupt as an
/// in-band <c>{"type":"interrupt"}</c> stdin line instead (see ReplPanelProvider.ts). The old
/// design (<c>ReplJsonBridge.ReadInput</c>) only read stdin from inside the main turn loop, once
/// per turn boundary — so an interrupt line written while a turn was mid-stream (the main loop
/// blocked awaiting <c>ExecuteAsync</c>, not calling ReadInput) just sat unread in the pipe until
/// the turn finished on its own. By then <c>ctx.ActiveCts</c> was already null and the interrupt
/// was silently a no-op: clicking Stop mid-response did nothing. Routing every stdin line through
/// this always-running pump lets an interrupt be acted on the instant it arrives, regardless of
/// what the main loop is awaiting.
///
/// Approval and question answers travel on their own channel, read only while a prompt is open, so
/// a message sent meanwhile can never be taken for an answer, and an answer can never become a message.
/// </summary>
public sealed class ReplStdinPump
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _answers = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextReader _input;
    private readonly Func<CancellationTokenSource?> _getActiveCts;
    private Task? _pumpTask;
    // True while the main loop waits in ReadMessageAsync/ReadInputAsync — i.e. nothing is running.
    private volatile bool _awaitingInput;

    // Put on the answer channel by an interrupt: a prompt still open when the turn stops is denied or dismissed.
    private const string Interrupted = "";

    internal ReplStdinPump(TextReader input, Func<CancellationTokenSource?> getActiveCts)
    {
        _input        = input;
        _getActiveCts = getActiveCts;
    }

    /// <summary>Completes when the input closes, even while lines it read are still unconsumed.</summary>
    internal Task Closed => _closed.Task;

    /// <summary>Idempotent — a second call is a no-op.</summary>
    internal void Start() => _pumpTask ??= Task.Run(PumpLoopAsync);

    /// <summary>
    /// Takes a message the user sends while the REPL is busy, as the full-screen REPL's prompt does; set by
    /// <c>ReplTurn</c> for the session. Only plain text joins: a <c>/command</c>, <c>!command</c> or <c>$skill</c>
    /// waits for the turn to end, a message with images starts the next turn, and approval and question answers
    /// go to the prompt waiting for them.
    /// </summary>
    internal volatile Func<string, bool>? Interject;

    /// <summary>
    /// Runs a <c>/command</c> that arrives while the REPL is busy (a turn, a whole <c>/goal</c> run, another command)
    /// if it needn't wait for that to finish (see <c>ReplCommands.RunMidTurn</c>).
    /// </summary>
    internal volatile Func<string, string?>? RunNow;

    private async Task PumpLoopAsync()
    {
        while (true)
        {
            string? line;
            try   { line = await _input.ReadLineAsync(); }
            catch { line = null; }

            if (line is null)
            {
                _lines.Writer.TryComplete();
                _answers.Writer.TryComplete();
                _closed.TrySetResult();
                return;
            }

            if (IsInterruptLine(line))
            {
                // Stop pressed again on a turn that's still stopping: kill the commands its tools
                // are waiting on, as a second Ctrl+C does.
                var c = _getActiveCts();
                if (c is not null && !c.IsCancellationRequested) c.Cancel();
                else if (c is not null) fuseraft.Infrastructure.Plugins.ProcessHelper.KillRunning();
                _answers.Writer.TryWrite(Interrupted);
                continue; // handled here — never queued as input
            }

            if (AnswerType(line) is not null)
            {
                _answers.Writer.TryWrite(line);
                continue;
            }

            if (!_awaitingInput && RunNow is { } runNow && CommandText(line) is { } command
                && runNow(command) is not null)
                continue;

            if (!_awaitingInput && Interject is { } interject && InterjectionText(line) is { } text && interject(text))
                continue;

            _lines.Writer.TryWrite(line);
        }
    }

    /// <summary>The text of a <c>user_input</c> line that may join a running turn, or null.</summary>
    internal static string? InterjectionText(string line) =>
        UserText(line) is { Length: > 0 } text && text[0] is not ('/' or '!' or '$') ? text : null;

    /// <summary>The text of a <c>user_input</c> line that is a one-line /command, or null.</summary>
    internal static string? CommandText(string line) =>
        UserText(line) is { Length: > 0 } text && text[0] == '/' && !text.Contains('\n') ? text : null;

    // A user_input line's trimmed text; null for anything else, or a message that carries images.
    private static string? UserText(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) || type.GetString() != "user_input" ||
                !root.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String)
                return null;
            if (root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
                return null;
            return t.GetString()!.Trim();
        }
        catch (JsonException) { return null; }
    }

    // "approval_response" or "question_response" for an answer line; null for anything else.
    private static string? AnswerType(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("type", out var t) &&
                   t.GetString() is "approval_response" or "question_response" ? t.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    // Opens a prompt: drops answers left from earlier ones (sent after their prompt had closed), asks via
    // `ask`, and waits for an answer of `type`. Interrupted when the turn was stopped; null once input closes.
    private async Task<string?> ReadAnswerAsync(string type, Action? ask, CancellationToken ct)
    {
        while (_answers.Reader.TryRead(out _)) { }
        ask?.Invoke();
        while (await _answers.Reader.WaitToReadAsync(ct))
            if (_answers.Reader.TryRead(out var line) && (line == Interrupted || AnswerType(line) == type))
                return line;
        return null;
    }

    internal static bool IsInterruptLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("type", out var t) && t.GetString() is "interrupt";
        }
        catch { return false; }
    }

    /// <summary>One webview message: its text plus any images that came with it (already validated).</summary>
    internal sealed record BridgeInput(string? Text, IReadOnlyList<DataContent> Images, IReadOnlyList<string> Errors);

    /// <summary>Like <see cref="ReadInputAsync"/> but keeps attached images; null once stdin is closed.</summary>
    internal async Task<BridgeInput?> ReadMessageAsync() =>
        await ReadLineAsync() is { } line ? ParseMessage(line) : null;

    private async Task<string?> ReadLineAsync()
    {
        _awaitingInput = true;
        try
        {
            while (await _lines.Reader.WaitToReadAsync())
                if (_lines.Reader.TryRead(out var line))
                    return line;
            return null;
        }
        finally { _awaitingInput = false; }
    }

    /// <summary>
    /// <c>{"text":"…","images":[{"data":"&lt;base64&gt;","name":"shot.png"}]}</c>. The declared media type is
    /// ignored: the decoded bytes are sniffed exactly as a file's would be, so the webview cannot smuggle
    /// a non-image (or an over-limit payload) through to the model by labelling it <c>image/png</c>.
    /// </summary>
    internal static BridgeInput ParseMessage(string line)
    {
        var text   = ExtractText(line);
        var images = new List<DataContent>();
        var errors = new List<string>();

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("images", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in arr.EnumerateArray())
                {
                    index++;
                    var name = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString() ?? $"image-{index}" : $"image-{index}";

                    if (images.Count >= ImageAttachments.MaxImagesPerMessage)
                    {
                        errors.Add($"at most {ImageAttachments.MaxImagesPerMessage} images per message");
                        break;
                    }
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.String)
                    {
                        errors.Add($"{name}: missing image data");
                        continue;
                    }

                    var b64 = d.GetString() ?? string.Empty;
                    // A data: URL is what FileReader.readAsDataURL produces; accept it as well as bare base64.
                    var comma = b64.IndexOf(',');
                    if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0) b64 = b64[(comma + 1)..];

                    // Reject before decoding what would decode to more than the limit (base64 is 4 chars per 3 bytes).
                    if (b64.Length / 4L * 3 > ImageAttachments.MaxImageBytes + 3)
                    {
                        errors.Add($"{name}: over the {ImageAttachments.MaxImageBytes / 1024 / 1024} MB limit");
                        continue;
                    }

                    byte[] bytes;
                    try   { bytes = Convert.FromBase64String(b64); }
                    catch (FormatException) { errors.Add($"{name}: the image data is not valid base64"); continue; }

                    if (ImageAttachments.TryCreate(bytes, name, out var img, out var err)) images.Add(img!);
                    else errors.Add(err!);
                }
            }
        }
        catch (JsonException) { /* not JSON: the whole line is the text, no images */ }

        return new BridgeInput(text, images, errors);
    }

    /// <summary>Returns the next non-interrupt line's "text" field, or null once stdin is closed.</summary>
    internal async Task<string?> ReadInputAsync() =>
        await ReadLineAsync() is { } line ? ExtractText(line) : null;

    internal static string? ExtractText(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("text", out var text)) return text.GetString();
        }
        catch { }
        return line;
    }

    /// <summary>
    /// Asks with <paramref name="ask"/> (emitting the <c>approval_request</c> event, see
    /// <see cref="fuseraft.Cli.JsonBridgeHumanApprovalService"/>) and blocks for the answer, e.g.
    /// <c>{"type":"approval_response","approved":true}</c>. Messages sent meanwhile wait their turn as
    /// input. Anything other than a well-formed approval with <c>approved:true</c> — a malformed answer,
    /// Stop, or stdin closing because the panel/process went away — denies the command rather than
    /// risking a false approval.
    /// </summary>
    internal async Task<bool> ReadApprovalResponseAsync(Action? ask = null) => await TryReadApprovalResponseAsync(ask) ?? false;

    /// <summary>Like <see cref="ReadApprovalResponseAsync"/>, but null when stdin closed before an answer arrived.</summary>
    internal async Task<bool?> TryReadApprovalResponseAsync(Action? ask = null) =>
        await ReadAnswerAsync("approval_response", ask, CancellationToken.None) is { } line ? ExtractApproval(line) : null;

    /// <summary>
    /// Asks with <paramref name="ask"/> (emitting a <c>question</c> event) and blocks for the answer, e.g.
    /// <c>{"type":"question_response","answer":"SQLite"}</c>. Anything but a non-empty string answer —
    /// <c>"answer":null</c> (skipped), a malformed answer, Stop, or stdin closing — dismisses the question.
    /// </summary>
    internal async Task<string?> ReadQuestionResponseAsync(CancellationToken ct, Action? ask = null) =>
        await ReadAnswerAsync("question_response", ask, ct) is { } line ? ExtractQuestionAnswer(line) : null;

    internal static string? ExtractQuestionAnswer(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("type", out var typeEl) &&
                typeEl.GetString() is "question_response" &&
                doc.RootElement.TryGetProperty("answer", out var answerEl) &&
                answerEl.ValueKind == JsonValueKind.String &&
                answerEl.GetString()?.Trim() is { Length: > 0 } answer)
                return answer;
        }
        catch { }
        return null;
    }

    internal static bool ExtractApproval(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("type", out var typeEl) &&
                typeEl.GetString() is "approval_response" &&
                doc.RootElement.TryGetProperty("approved", out var approvedEl) &&
                approvedEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return approvedEl.GetBoolean();
        }
        catch { }
        return false;
    }
}
