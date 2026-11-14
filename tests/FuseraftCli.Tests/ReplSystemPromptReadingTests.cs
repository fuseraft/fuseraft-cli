using ReplPrompt = fuseraft.Cli.Commands.Repl.SystemPromptBuilder;

namespace FuseraftCli.Tests;

/// <summary>
/// The REPL prompt says which files to read whole: told only "never cold-read a large file", grok-4.7 read 46 files
/// that fit in one call in 8–80 line slices, one 275-line doc nine times.
/// </summary>
public sealed class ReplSystemPromptReadingTests
{
    private static readonly string Prompt = new ReplPrompt().AddToolGuidance(toolCount: 10).Build();

    [Fact]
    public void FilesThatFit_AreReadWhole() =>
        Assert.Contains("Read those in one call, not in pieces", Prompt);

    [Fact]
    public void PatchAnchors_ComeFromTheEarlierRead() =>
        Assert.Contains("take patch_file's oldText from that read", Prompt);

    [Fact]
    public void TheVagueLargeFileRule_IsGone() =>
        Assert.DoesNotContain("never cold-read a large file", Prompt);
}
