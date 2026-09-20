using System.Text.Json;
using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core.Images;
using fuseraft.Infrastructure.Agents;

namespace FuseraftCli.Tests;

/// <summary>
/// Covers <see cref="ImageAttachments"/> and the bridge/estimator/hint edges of image input. The property
/// that runs through all of it: the file's <em>bytes</em> decide whether something is an image, nothing
/// fails silently, and only a bounded number of pictures ride along in context.
/// </summary>
public sealed class ImageAttachmentsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fuseraft-img-" + Guid.NewGuid().ToString("N"));

    public ImageAttachmentsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    // Real magic numbers followed by filler — sniffing only ever reads the header.
    internal static byte[] Png(int extra = 32)  => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[extra]];
    internal static byte[] Jpeg(int extra = 32) => [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[extra]];
    internal static byte[] Gif(string v = "89a") => [.. "GIF"u8, .. System.Text.Encoding.ASCII.GetBytes(v), .. new byte[16]];
    internal static byte[] Webp() => [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, .. new byte[16]];

    private string Write(string name, byte[] bytes)
    {
        var p = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private static DataContent Img(string name = "a.png", byte[]? bytes = null)
    {
        ImageAttachments.TryCreate(bytes ?? Png(), name, out var img, out _);
        return img!;
    }

    // ── Sniffing ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("png", "image/png")]
    [InlineData("jpeg", "image/jpeg")]
    [InlineData("gif87", "image/gif")]
    [InlineData("gif89", "image/gif")]
    [InlineData("webp", "image/webp")]
    public void Sniff_RecognisesTheFourFormats(string kind, string expected)
    {
        byte[] bytes = kind switch { "png" => Png(), "jpeg" => Jpeg(), "gif87" => Gif("87a"), "gif89" => Gif("89a"), _ => Webp() };

        Assert.Equal(expected, ImageAttachments.SniffMediaType(bytes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("just some text")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("%PDF-1.7")]
    public void Sniff_RejectsEverythingElse(string content) =>
        Assert.Null(ImageAttachments.SniffMediaType(System.Text.Encoding.UTF8.GetBytes(content)));

    [Fact]
    public void Sniff_RiffThatIsNotWebp_IsRejected()
    {
        // WAV and AVI share the RIFF container.
        Assert.Null(ImageAttachments.SniffMediaType([.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8]));
    }

    [Fact]
    public void Sniff_TruncatedHeaders_AreRejectedNotThrown()
    {
        Assert.Null(ImageAttachments.SniffMediaType([0x89, 0x50]));
        Assert.Null(ImageAttachments.SniffMediaType([0xFF, 0xD8]));
        Assert.Null(ImageAttachments.SniffMediaType([.. "RIFF"u8, 0, 0]));
    }

    // ── Loading ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TryLoad_ReadsARealImage_AndRemembersItsName()
    {
        var path = Write("shots/login.png", Png());

        Assert.True(ImageAttachments.TryLoad(path, _dir, out var img, out var err));

        Assert.Null(err);
        Assert.Equal("image/png", img!.MediaType);
        Assert.Equal("login.png", img.AdditionalProperties![ImageAttachments.NameProperty]);
        Assert.Equal(Png().Length, img.Data.Length);
    }

    [Fact]
    public void TryLoad_ResolvesRelativePathsAgainstTheGivenCwd()
    {
        Write("rel.png", Png());

        Assert.True(ImageAttachments.TryLoad("rel.png", _dir, out var img, out _));
        Assert.NotNull(img);
    }

    [Fact]
    public void TryLoad_TrustsTheBytesNotTheExtension()
    {
        var notAnImage = Write("innocent.png", "this is plainly text"u8.ToArray());
        var wrongExt   = Write("photo.txt", Jpeg());

        Assert.False(ImageAttachments.TryLoad(notAnImage, _dir, out _, out var err));
        Assert.Contains("not a PNG, JPEG, GIF or WebP", err);
        Assert.True(ImageAttachments.TryLoad(wrongExt, _dir, out var img, out _));   // valid bytes: accepted whatever the name
        Assert.Equal("image/jpeg", img!.MediaType);
    }

    [Fact]
    public void TryLoad_MissingEmptyAndOversized_EachSayWhy()
    {
        Assert.False(ImageAttachments.TryLoad("nope.png", _dir, out _, out var missing));
        Assert.Contains("no such file", missing);

        var empty = Write("empty.png", []);
        Assert.False(ImageAttachments.TryLoad(empty, _dir, out _, out var e1));
        Assert.Contains("empty", e1);

        var big = Write("big.png", Png((int)ImageAttachments.MaxImageBytes + 1));
        Assert.False(ImageAttachments.TryLoad(big, _dir, out _, out var e2));
        Assert.Contains("over the 20 MB limit", e2);
    }

    [Fact]
    public void TryCreate_EnforcesTheSizeLimitItself_NotOnlyTheFileLoader()
    {
        // The bridge hands raw decoded bytes straight to TryCreate, so the limit must live there too.
        var overLimit = Png((int)ImageAttachments.MaxImageBytes);   // header + MaxImageBytes of filler

        Assert.False(ImageAttachments.TryCreate(overLimit, "big.png", out var img, out var err));
        Assert.Null(img);
        Assert.Contains("over the 20 MB limit", err);
        Assert.True(ImageAttachments.TryCreate(Png(1024), "ok.png", out _, out _));
    }

    // ── /image parsing ──────────────────────────────────────────────────────────

    [Fact]
    public void ImageCommand_OnePathAndAMessage()
    {
        Write("a.png", Png());

        var r = ImageAttachments.ParseImageCommand("a.png why is the button misaligned?", _dir);

        Assert.Empty(r.Errors);
        Assert.Single(r.Images);
        Assert.Equal("why is the button misaligned?", r.Text);
    }

    [Fact]
    public void ImageCommand_ManyPaths_ThenMessage_StopsAtTheFirstNonImageWord()
    {
        Write("a.png", Png()); Write("b.jpg", Jpeg());

        var r = ImageAttachments.ParseImageCommand("a.png b.jpg compare these two", _dir);

        Assert.Equal(2, r.Images.Count);
        Assert.Equal("compare these two", r.Text);
    }

    [Fact]
    public void ImageCommand_QuotedPathsMayContainSpaces()
    {
        Write("Screen Shot 1.png", Png());

        var r = ImageAttachments.ParseImageCommand("\"Screen Shot 1.png\" what is this", _dir);

        Assert.Empty(r.Errors);
        Assert.Equal("Screen Shot 1.png", r.Images[0].AdditionalProperties![ImageAttachments.NameProperty]);
        Assert.Equal("what is this", r.Text);
    }

    [Fact]
    public void ImageCommand_NoMessage_GetsADefaultOne()
    {
        Write("a.png", Png()); Write("b.png", Png());

        Assert.Equal("Describe this image.",   ImageAttachments.ParseImageCommand("a.png", _dir).Text);
        Assert.Equal("Describe these images.", ImageAttachments.ParseImageCommand("a.png b.png", _dir).Text);
    }

    [Fact]
    public void ImageCommand_MissingFile_IsAnError_AndIsNotSentAsWords()
    {
        // Regression: a failed first path used to leave the loop treating the next *word* as another path.
        var r = ImageAttachments.ParseImageCommand("typo.png what do you see here", _dir);

        Assert.Empty(r.Images);
        var e = Assert.Single(r.Errors);
        Assert.Contains("typo.png", e);
    }

    [Fact]
    public void ImageCommand_FirstTokenMustBeAPath_EvenWithoutAnImageExtension()
    {
        var r = ImageAttachments.ParseImageCommand("describe everything", _dir);

        Assert.Empty(r.Images);
        Assert.NotEmpty(r.Errors);
    }

    [Fact]
    public void ImageCommand_NoArguments_ExplainsUsage()
    {
        var r = ImageAttachments.ParseImageCommand("", _dir);

        Assert.Contains(r.Errors, e => e.Contains("path of an image"));
    }

    [Fact]
    public void ImageCommand_TooManyImages_IsCapped()
    {
        var names = Enumerable.Range(0, ImageAttachments.MaxImagesPerMessage + 2).Select(i => $"i{i}.png").ToList();
        foreach (var n in names) Write(n, Png());

        var r = ImageAttachments.ParseImageCommand(string.Join(' ', names), _dir);

        Assert.Equal(ImageAttachments.MaxImagesPerMessage, r.Images.Count);
        Assert.Contains(r.Errors, e => e.Contains("at most"));
    }

    // ── Inline @references ──────────────────────────────────────────────────────

    [Fact]
    public void Inline_AttachesAnExistingImage_AndKeepsTheTokenInTheText()
    {
        Write("shot.png", Png());

        var r = ImageAttachments.ExtractInlineReferences("what is wrong in @shot.png ?", _dir);

        Assert.Single(r.Images);
        Assert.Equal("what is wrong in @shot.png ?", r.Text);
    }

    [Theory]
    [InlineData("ping @someone about the release")]
    [InlineData("see @missing.png for details")]                 // looks like an attachment, but no such file
    [InlineData("mail bob@example.com")]                          // '@' not at a word start
    [InlineData("the @notes.txt file")]                           // not an image extension
    [InlineData("no mention at all")]
    public void Inline_LeavesLookalikesAlone_WithoutErrors(string text)
    {
        Write("notes.txt", "hi"u8.ToArray());

        var r = ImageAttachments.ExtractInlineReferences(text, _dir);

        Assert.Empty(r.Images);
        Assert.Empty(r.Errors);
        Assert.Equal(text, r.Text);
    }

    [Theory]
    [InlineData("look at @shot.png.")]
    [InlineData("look at @shot.png,")]
    [InlineData("(see @shot.png)")]
    [InlineData("really @shot.png?")]
    public void Inline_TrailingPunctuationIsNotPartOfTheName(string text)
    {
        Write("shot.png", Png());

        Assert.Single(ImageAttachments.ExtractInlineReferences(text, _dir).Images);
    }

    [Fact]
    public void Inline_QuotedPathWithSpaces_AndTheSameFileTwice_AttachesOnce()
    {
        Write("My Shot.png", Png());

        var r = ImageAttachments.ExtractInlineReferences("@\"My Shot.png\" versus @'My Shot.png'", _dir);

        Assert.Single(r.Images);
    }

    [Fact]
    public void Inline_AnExistingFileThatIsNotReallyAnImage_ReportsAnError_NotSilence()
    {
        Write("fake.png", "text pretending"u8.ToArray());

        var r = ImageAttachments.ExtractInlineReferences("check @fake.png", _dir);

        Assert.Empty(r.Images);
        Assert.Contains(r.Errors, e => e.Contains("fake.png") && e.Contains("not a PNG"));
    }

    // ── Context budgeting ───────────────────────────────────────────────────────

    private static ChatMessage UserWith(string text, params DataContent[] images) =>
        new(ChatRole.User, [new TextContent(text), .. images]);

    [Fact]
    public void StripOlderImages_KeepsTheMostRecentN_AcrossMessages()
    {
        var history = new List<ChatMessage>
        {
            UserWith("one",   Img("1.png")),
            UserWith("two",   Img("2.png"), Img("3.png")),
            UserWith("three", Img("4.png")),
        };

        var dropped = ImageAttachments.StripOlderImages(history, keep: 2);

        Assert.Equal(2, dropped);
        Assert.Equal(0, ImageAttachments.CountImages(history[0]));
        Assert.Equal(1, ImageAttachments.CountImages(history[1]));                  // first of "two" replaced, second kept
        Assert.Equal(1, ImageAttachments.CountImages(history[2]));
        Assert.Contains("1.png", history[0].Text);
        Assert.Contains("2.png", history[1].Text);
    }

    [Fact]
    public void StripOlderImages_BelowTheLimit_ChangesNothing_AndIsIdempotent()
    {
        var history = new List<ChatMessage> { UserWith("a", Img()), UserWith("b", Img()) };

        Assert.Equal(0, ImageAttachments.StripOlderImages(history, keep: 4));
        Assert.Equal(2, history.Sum(ImageAttachments.CountImages));

        ImageAttachments.StripOlderImages(history, keep: 1);
        Assert.Equal(0, ImageAttachments.StripOlderImages(history, keep: 1));        // nothing left to strip
    }

    [Fact]
    public void StripOlderImages_PlaceholderReadsAsSeparateLine_NotGluedToTheSentence()
    {
        var history = new List<ChatMessage> { UserWith("what is wrong here?", Img("bug.png")), UserWith("and here", Img("b2.png")) };

        ImageAttachments.StripOlderImages(history, keep: 1);

        Assert.StartsWith("what is wrong here?\n[image omitted from context: bug.png, image/png,", history[0].Text);
        Assert.Equal("what is wrong here?", ImageAttachments.UserText(history[0]));   // the user's own words, untouched
    }

    [Fact]
    public void Estimator_CountsImages_AndOnlyImages()
    {
        Assert.Equal(ImageAttachments.EstimatedCharsPerImage, AgentContextCompactionFilters.EstimateContentChars(Img()));
        Assert.Equal(0, AgentContextCompactionFilters.EstimateContentChars(new DataContent(new byte[100], "application/pdf")));
        Assert.True(ImageAttachments.EstimatedCharsPerImage > 1_000);   // not accidentally ~zero
    }

    // ── Turn plumbing ───────────────────────────────────────────────────────────

    [Fact]
    public void BuildUserMessage_TextFirst_ThenImagesInOrder()
    {
        var a = Img("a.png"); var b = Img("b.png");

        var m = ReplTurn.BuildUserMessage("compare", [a, b]);

        Assert.Equal(ChatRole.User, m.Role);
        Assert.Equal(3, m.Contents.Count);
        Assert.Equal("compare", ((TextContent)m.Contents[0]).Text);
        Assert.Same(a, m.Contents[1]);
        Assert.Same(b, m.Contents[2]);
    }

    [Fact]
    public void BuildUserMessage_WithoutImages_IsPlainText()
    {
        var m = ReplTurn.BuildUserMessage("hi", null);

        Assert.Single(m.Contents);
        Assert.Equal("hi", m.Text);
    }

    [Theory]
    [InlineData(400, true,  true)]
    [InlineData(415, true,  true)]
    [InlineData(422, true,  true)]
    [InlineData(400, false, false)]   // no images in the message: not our hint to give
    [InlineData(401, true,  false)]   // auth
    [InlineData(403, true,  false)]
    [InlineData(429, true,  false)]   // rate limit
    [InlineData(500, true,  false)]   // server-side
    public void ImageRejectionHint_OnlyForClientRejectionsOfMessagesThatCarriedImages(int status, bool hadImages, bool expectHint)
    {
        var ex = new HttpRequestException("failed", null, (System.Net.HttpStatusCode)status);

        Assert.Equal(expectHint, ReplTurn.BuildImageRejectionHint(ex, hadImages) is not null);
    }

    [Fact]
    public void ImageRejectionHint_AlsoFiresWhenTheProviderNamesImages_AndLooksThroughInnerExceptions()
    {
        var ex = new InvalidOperationException("wrapper", new InvalidOperationException("this model does not support image input"));

        Assert.NotNull(ReplTurn.BuildImageRejectionHint(ex, messageHadImages: true));
        Assert.Null(ReplTurn.BuildImageRejectionHint(new InvalidOperationException("boom"), messageHadImages: true));
    }

    // ── VS Code bridge ──────────────────────────────────────────────────────────

    private static string Line(object payload) => JsonSerializer.Serialize(payload);

    [Fact]
    public void Bridge_DecodesImages_AndSniffsTheBytes()
    {
        var line = Line(new { text = "what is this", images = new[] { new { name = "s.png", data = Convert.ToBase64String(Png()) } } });

        var m = ReplStdinPump.ParseMessage(line);

        Assert.Equal("what is this", m.Text);
        Assert.Empty(m.Errors);
        var img = Assert.Single(m.Images);
        Assert.Equal("image/png", img.MediaType);
        Assert.Equal("s.png", img.AdditionalProperties![ImageAttachments.NameProperty]);
    }

    [Fact]
    public void Bridge_AcceptsADataUrl_AsFileReaderProducesOne()
    {
        var line = Line(new { text = "x", images = new[] { new { name = "a.jpg", data = "data:image/jpeg;base64," + Convert.ToBase64String(Jpeg()) } } });

        Assert.Equal("image/jpeg", Assert.Single(ReplStdinPump.ParseMessage(line).Images).MediaType);
    }

    [Fact]
    public void Bridge_IgnoresTheDeclaredMediaType_BytesDecide()
    {
        var line = Line(new { text = "x", images = new[] { new { name = "evil.png", mediaType = "image/png", data = Convert.ToBase64String("MZ-not-an-image"u8.ToArray()) } } });

        var m = ReplStdinPump.ParseMessage(line);

        Assert.Empty(m.Images);
        Assert.Contains(m.Errors, e => e.Contains("evil.png") && e.Contains("not a PNG"));
    }

    [Fact]
    public void Bridge_BadBase64_MissingData_AndTooMany_AreReportedNotThrown()
    {
        var many = Enumerable.Range(0, ImageAttachments.MaxImagesPerMessage + 1)
            .Select(i => new { name = $"i{i}.png", data = Convert.ToBase64String(Png()) }).ToArray();

        var bad  = ReplStdinPump.ParseMessage(Line(new { text = "x", images = new object[] { new { name = "b.png", data = "!!!not base64!!!" }, new { name = "c.png" } } }));
        var lots = ReplStdinPump.ParseMessage(Line(new { text = "x", images = many }));

        Assert.Empty(bad.Images);
        Assert.Contains(bad.Errors, e => e.Contains("not valid base64"));
        Assert.Contains(bad.Errors, e => e.Contains("missing image data"));
        Assert.Equal(ImageAttachments.MaxImagesPerMessage, lots.Images.Count);
        Assert.Contains(lots.Errors, e => e.Contains("at most"));
    }

    [Fact]
    public void Bridge_OversizedPayload_IsRejectedBeforeDecoding()
    {
        var huge = new string('A', (int)(ImageAttachments.MaxImageBytes * 4 / 3) + 4096);   // decodes to > the limit

        var m = ReplStdinPump.ParseMessage(Line(new { text = "x", images = new[] { new { name = "huge.png", data = huge } } }));

        Assert.Empty(m.Images);
        Assert.Contains(m.Errors, e => e.Contains("huge.png") && e.Contains("limit"));
    }

    [Theory]
    [InlineData("plain text, not json")]
    [InlineData("{\"text\":\"just words\"}")]
    [InlineData("{\"text\":\"x\",\"images\":\"not-an-array\"}")]
    public void Bridge_MessagesWithoutImages_AreUnaffected(string line)
    {
        var m = ReplStdinPump.ParseMessage(line);

        Assert.Empty(m.Images);
        Assert.Empty(m.Errors);
        Assert.False(string.IsNullOrEmpty(m.Text));
    }

    // ── Replay ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Replay_ShowsThatImagesWereAttached()
    {
        var t = ReplReplay.BuildTranscript([
            new ChatMessage(ChatRole.System, "sys"),
            UserWith("what is this?", Img(), Img()),
            new ChatMessage(ChatRole.Assistant, "A diagram."),
        ]);

        var turn = Assert.Single(t.Turns);
        Assert.Equal("what is this?\n[+2 images]", turn.User);
    }

    [Fact]
    public void Replay_ImageOnlyTextTurns_AreUnchanged()
    {
        var t = ReplReplay.BuildTranscript([new ChatMessage(ChatRole.User, "hello"), new ChatMessage(ChatRole.Assistant, "hi")]);

        Assert.Equal("hello", Assert.Single(t.Turns).User);
    }
}
