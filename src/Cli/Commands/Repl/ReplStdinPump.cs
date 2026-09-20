using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using fuseraft.Core.Images;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Owns stdin for the life of a JSON-bridge REPL session (<c>fuseraft repl --vscode</c>). A
/// single background loop is the only thing that ever reads from it once <see cref="Start"/> is
/// called; everything else consumes lines through <see cref="ReadInputAsync"/> /
/// <see cref="ReadApprovalResponseAsync"/> instead of touching the underlying reader directly.
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
/// </summary>
public sealed class ReplStdinPump
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly TextReader _input;
    private readonly Func<CancellationTokenSource?> _getActiveCts;
    private Task? _pumpTask;

    internal ReplStdinPump(TextReader input, Func<CancellationTokenSource?> getActiveCts)
    {
        _input        = input;
        _getActiveCts = getActiveCts;
    }

    /// <summary>Idempotent — a second call is a no-op.</summary>
    internal void Start() => _pumpTask ??= Task.Run(PumpLoopAsync);

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
                return;
            }

            if (IsInterruptLine(line))
            {
                var c = _getActiveCts();
                if (c is not null && !c.IsCancellationRequested) c.Cancel();
                continue; // handled here — never queued for ReadInputAsync/ReadApprovalResponseAsync
            }

            _lines.Writer.TryWrite(line);
        }
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
    internal async Task<BridgeInput?> ReadMessageAsync()
    {
        while (await _lines.Reader.WaitToReadAsync())
            if (_lines.Reader.TryRead(out var line))
                return ParseMessage(line);
        return null;
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
    internal async Task<string?> ReadInputAsync()
    {
        while (await _lines.Reader.WaitToReadAsync())
            if (_lines.Reader.TryRead(out var line))
                return ExtractText(line);
        return null;
    }

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
    /// Blocks for the webview's answer to a pending <c>approval_request</c> event (see
    /// <see cref="fuseraft.Cli.JsonBridgeHumanApprovalService"/>), e.g.
    /// <c>{"type":"approval_response","approved":true}</c>. Only ever called from within a single
    /// shell-tool-call approval gate, never concurrently with <see cref="ReadInputAsync"/> (that's
    /// only awaited between turns), so both can safely share this pump's one line channel.
    /// Anything other than a well-formed approval with <c>approved:true</c> — malformed JSON, the
    /// wrong "type", or stdin closing because the panel/process went away — denies the command
    /// rather than risking a false approval.
    /// </summary>
    internal async Task<bool> ReadApprovalResponseAsync()
    {
        while (await _lines.Reader.WaitToReadAsync())
            if (_lines.Reader.TryRead(out var line))
                return ExtractApproval(line);
        return false;
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
