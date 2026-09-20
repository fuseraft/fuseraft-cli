using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace fuseraft.Core.Images;

/// <summary>Outcome of pulling image attachments out of user input.</summary>
/// <param name="Text">The message text to send (image path tokens removed for <c>/image</c>, left in place for inline <c>@refs</c>).</param>
/// <param name="Images">Loaded, validated images in the order they were named.</param>
/// <param name="Errors">Human-readable reasons anything was rejected; when non-empty for <c>/image</c>, nothing should be sent.</param>
public sealed record AttachmentParse(string Text, IReadOnlyList<DataContent> Images, IReadOnlyList<string> Errors);

/// <summary>
/// Loading, validating and budgeting image attachments for chat messages. The rules that matter:
/// the file's <em>bytes</em> decide whether it is an image (magic numbers, never the extension); a
/// bounded number of recent images stay in context so a long session does not resend — and pay for —
/// every screenshot it ever saw; and anything that cannot be attached says why instead of silently vanishing.
/// </summary>
public static class ImageAttachments
{
    /// <summary>Hard cap per image. Providers reject far less than this (Anthropic: 5 MB); this only stops runaway files.</summary>
    public const long MaxImageBytes = 20 * 1024 * 1024;

    public const int MaxImagesPerMessage = 8;

    /// <summary>How many of the most recent images stay attached across a session; older ones become text placeholders.</summary>
    public const int KeepRecentImages = 4;

    /// <summary>
    /// What one image counts for in context budgeting (~1,600 tokens at <see cref="TokenEstimator.CharsPerToken"/>).
    /// Providers price images by pixel area, not bytes; this is the middle of the range, and without <em>some</em>
    /// figure every image would count as zero and the budget would silently underestimate a screenshot-heavy session.
    /// </summary>
    public const int EstimatedCharsPerImage = 1_600 * TokenEstimator.CharsPerToken;

    /// <summary>Cached content-addressed id of the image in <see cref="ReplImageStore"/> (set on first persist).</summary>
    public const string IdProperty = "fuseraft.image_id";

    /// <summary>The file name the user attached it by, for placeholders and display.</summary>
    public const string NameProperty = "fuseraft.image_name";

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    public static bool HasImageExtension(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Media type from the leading bytes, or <c>null</c> when it is not a format every major provider accepts.</summary>
    public static string? SniffMediaType(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 &&
            b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A)
            return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return "image/jpeg";
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8' && (b[4] == '7' || b[4] == '9') && b[5] == 'a')
            return "image/gif";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' &&
            b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
            return "image/webp";
        return null;
    }

    // -------------------------------------------------------------------------
    // Loading
    // -------------------------------------------------------------------------

    /// <summary>Resolves <c>~</c> and relative paths against <paramref name="cwd"/>.</summary>
    public static string ResolvePath(string path, string cwd)
    {
        var p = path.Trim();
        if (p == "~" || p.StartsWith("~/", StringComparison.Ordinal) || p.StartsWith("~\\", StringComparison.Ordinal))
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Length > 2 ? p[2..] : "");
        return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(cwd, p));
    }

    /// <summary>Validates <paramref name="bytes"/> as an image and wraps it. <paramref name="name"/> is only for messages.</summary>
    public static bool TryCreate(byte[] bytes, string name, out DataContent? image, out string? error)
    {
        image = null;
        if (bytes.Length == 0) { error = $"{name}: the image is empty"; return false; }
        if (bytes.Length > MaxImageBytes)
        {
            error = $"{name}: {bytes.Length / 1024 / 1024} MB is over the {MaxImageBytes / 1024 / 1024} MB limit";
            return false;
        }

        var mediaType = SniffMediaType(bytes);
        if (mediaType is null)
        {
            error = $"{name}: not a PNG, JPEG, GIF or WebP image (the file's contents do not match, whatever its extension says)";
            return false;
        }

        image = new DataContent(bytes, mediaType)
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { [NameProperty] = name },
        };
        error = null;
        return true;
    }

    public static bool TryLoad(string path, string cwd, out DataContent? image, out string? error)
    {
        image = null;
        var name = Path.GetFileName(path);
        string full;
        try { full = ResolvePath(path, cwd); }
        catch (Exception ex) { error = $"{name}: invalid path ({ex.Message})"; return false; }

        if (!File.Exists(full)) { error = $"{path}: no such file"; return false; }

        try
        {
            var length = new FileInfo(full).Length;
            if (length > MaxImageBytes)
            {
                error = $"{name}: {length / 1024 / 1024} MB is over the {MaxImageBytes / 1024 / 1024} MB limit";
                return false;
            }
            return TryCreate(File.ReadAllBytes(full), name, out image, out error);
        }
        catch (Exception ex)
        {
            error = $"{name}: could not be read ({ex.Message})";
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Parsing user input
    // -------------------------------------------------------------------------

    /// <summary>
    /// <c>/image &lt;path&gt;… [message]</c>: leading tokens that name image files are attached, the rest is
    /// the message. Paths may be quoted to hold spaces. The first token must be an image path, and a token
    /// with an image extension that cannot be loaded is an error — this is an explicit request, so a typo
    /// must not degrade into sending the words alone.
    /// </summary>
    public static AttachmentParse ParseImageCommand(string arg, string cwd)
    {
        var images = new List<DataContent>();
        var errors = new List<string>();
        var rest   = (arg ?? string.Empty).Trim();
        var pos    = 0;
        var named  = 0;   // paths consumed so far, whether or not they loaded

        while (pos < rest.Length)
        {
            var (token, next) = NextToken(rest, pos);
            if (token is null) break;
            if (named > 0 && !HasImageExtension(token)) break;   // first non-image word starts the message

            if (named >= MaxImagesPerMessage)
            {
                errors.Add($"at most {MaxImagesPerMessage} images per message");
                break;
            }
            if (TryLoad(token, cwd, out var img, out var err)) images.Add(img!);
            else errors.Add(err!);
            named++;
            pos = next;
        }

        if (images.Count == 0 && errors.Count == 0)
            errors.Add("give the path of an image to attach, e.g. /image screenshot.png what is wrong here?");

        var message = rest[Math.Min(pos, rest.Length)..].Trim();
        if (message.Length == 0 && errors.Count == 0)
            message = images.Count == 1 ? "Describe this image." : "Describe these images.";
        return new AttachmentParse(message, images, errors);
    }

    private static readonly Regex InlineRef = new(
        "(?<=^|\\s)@(?:\"(?<q>[^\"]+)\"|'(?<s>[^']+)'|(?<p>\\S+))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// <c>@path/to/shot.png</c> mentions inside an ordinary message. Only tokens that name an <em>existing</em>
    /// image file are attached (the token stays in the text so the model can refer to the file by name);
    /// anything else that merely looks like a mention — <c>@someone</c>, <c>@missing.png</c> — is left alone.
    /// </summary>
    public static AttachmentParse ExtractInlineReferences(string text, string cwd)
    {
        var images = new List<DataContent>();
        var errors = new List<string>();
        if (string.IsNullOrEmpty(text) || !text.Contains('@')) return new AttachmentParse(text ?? string.Empty, images, errors);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in InlineRef.Matches(text))
        {
            var quoted = m.Groups["q"].Success || m.Groups["s"].Success;
            var raw    = m.Groups["q"].Success ? m.Groups["q"].Value
                       : m.Groups["s"].Success ? m.Groups["s"].Value
                       : m.Groups["p"].Value;
            // Sentence punctuation glued to a bare token ("see @shot.png.") is not part of the name.
            if (!quoted) raw = raw.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
            if (!HasImageExtension(raw)) continue;

            string full;
            try { full = ResolvePath(raw, cwd); }
            catch { continue; }
            if (!File.Exists(full) || !seen.Add(full)) continue;

            if (images.Count >= MaxImagesPerMessage)
            {
                errors.Add($"only the first {MaxImagesPerMessage} images were attached");
                break;
            }
            if (TryLoad(raw, cwd, out var img, out var err)) images.Add(img!);
            else errors.Add(err!);
        }
        return new AttachmentParse(text, images, errors);
    }

    // Splits one whitespace-delimited token from `s` starting at `from`, honouring "double" and 'single' quotes.
    private static (string? Token, int Next) NextToken(string s, int from)
    {
        var i = from;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (i >= s.Length) return (null, s.Length);

        if (s[i] is '"' or '\'')
        {
            var quote = s[i];
            var close = s.IndexOf(quote, i + 1);
            if (close > i) return (s[(i + 1)..close], close + 1);
        }

        var start = i;
        while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
        return (s[start..i], i);
    }

    // -------------------------------------------------------------------------
    // In-history handling
    // -------------------------------------------------------------------------

    public static bool IsImage(AIContent c) =>
        c is DataContent dc && dc.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public static int CountImages(ChatMessage m) => m.Contents.Count(IsImage);

    public static string Placeholder(DataContent image)
    {
        var name = image.AdditionalProperties is { } p && p.TryGetValue(NameProperty, out var n) && n is string s && s.Length > 0
            ? s : "image";
        var kb = Math.Max(1, image.Data.Length / 1024);
        return $"[image omitted from context: {name}, {image.MediaType}, {kb} KB]";
    }

    /// <summary>
    /// Keeps only the <paramref name="keep"/> most recent images across <paramref name="history"/>; every older one
    /// becomes a one-line text placeholder in place. Bounds what each later turn resends (and the snapshot's size)
    /// without touching the surrounding text, so the conversation still reads correctly. Returns how many were replaced.
    /// </summary>
    public static int StripOlderImages(IReadOnlyList<ChatMessage> history, int keep = KeepRecentImages)
    {
        var slots = new List<(ChatMessage Message, int Index)>();
        foreach (var m in history)
            for (var i = 0; i < m.Contents.Count; i++)
                if (IsImage(m.Contents[i])) slots.Add((m, i));

        var excess = slots.Count - Math.Max(0, keep);
        for (var k = 0; k < excess; k++)
        {
            var (m, i) = slots[k];
            // Leading newline: ChatMessage.Text concatenates text parts with no separator, so /history,
            // /save and replay would otherwise glue the placeholder onto the end of the user's sentence.
            m.Contents[i] = new TextContent("\n" + Placeholder((DataContent)m.Contents[i]));
        }
        return Math.Max(0, excess);
    }

    /// <summary>The user's own words in a message that may also carry images or image placeholders.</summary>
    public static string UserText(ChatMessage m) =>
        m.Contents.OfType<TextContent>().FirstOrDefault()?.Text ?? m.Text ?? string.Empty;

    /// <summary>Short user-facing summary such as <c>📎 shot.png (image/png, 240 KB)</c>.</summary>
    public static string Describe(DataContent image)
    {
        var name = image.AdditionalProperties is { } p && p.TryGetValue(NameProperty, out var n) && n is string s && s.Length > 0
            ? s : "image";
        return $"{name} ({image.MediaType}, {Math.Max(1, image.Data.Length / 1024)} KB)";
    }

    /// <summary>The text an image contributes when a transcript is rendered as plain text.</summary>
    public static string TranscriptMarker(int count) =>
        count switch { 0 => string.Empty, 1 => "[+1 image]", _ => $"[+{count} images]" };
}
