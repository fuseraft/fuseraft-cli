using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

/// <summary>A reply that has stopped saying anything new is cut off; real prose, tables and code are not.</summary>
public sealed class RunawayOutputTests
{
    // The answer a live model gave before it wrote itself a question and looped (grok-4.7, 2026-11-13).
    private const string Answer =
        "The third file I read was `lib/httpserver.kiwi` (lines 1–400). You asked me to read, without changing anything " +
        "and with exactly one `read_file` call per step, five file ranges, then summarize what I read in two sentences.";

    // The loop's own phrases, in a shifting order as it produced them, so no two stretches repeat exactly.
    private static readonly string[] LoopPhrases =
    [
        "I did not use tools.", "Stop.", "Done.", "End.", "`/#`.", "Final.", "Complete.", "No more text.",
        "The answer is `/#`.", "The first line of the last read_file result is `/#`.", "No tools.", "The quote is `/#`.",
    ];

    private static string Loop(int chars)
    {
        var rng = new Random(7);
        var sb = new System.Text.StringBuilder();
        while (sb.Length < chars)
            sb.Append(LoopPhrases[rng.Next(LoopPhrases.Length)]).Append(' ');
        return sb.ToString();
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "fuseraft.sln")))
            dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }

    [Fact]
    public void ALoopingReply_IsCut_WhereTheLoopBegins()
    {
        var text = Answer + "\n\n" + Loop(14_000);

        var cut = RunawayOutput.LoopStart(text);

        Assert.NotNull(cut);
        // The whole answer stays, and nearly all of the loop goes.
        Assert.True(cut >= Answer.Length, $"cut at {cut}, inside the answer");
        Assert.True(cut <= Answer.Length + 2_000, $"cut at {cut}, well into the loop");
    }

    [Fact]
    public void AShortLoop_IsLeftAlone_UntilThereIsEnoughToJudge() =>
        Assert.Null(RunawayOutput.LoopStart(Answer + Loop(RunawayOutput.MinChars - Answer.Length - 500)));

    [Theory]
    [InlineData("docs/security.md")]
    [InlineData("docs/context-management.md")]
    [InlineData("docs/cli-reference.md")]
    public void LongProseAndTables_AreNotCut(string doc)
    {
        var text = RepoFile(doc);
        var len = Math.Min(text.Length, RunawayOutput.CeilingChars - 1);

        // Judged at every point a stream would check, not only at the end.
        for (var n = RunawayOutput.MinChars; n <= len; n += 2_000)
            Assert.Null(RunawayOutput.LoopStart(text[..n]));
    }

    [Fact]
    public void FencedCode_IsLeftOutOfTheMeasure()
    {
        var code = "```kiwi\n" + string.Concat(Enumerable.Repeat("fn f()\n  return 1\nend\n", 800)) + "```\n";

        Assert.Null(RunawayOutput.LoopStart(Answer + "\n\n" + code + "That's the whole module."));
    }

    [Fact]
    public void AnythingPastTheCeiling_IsCutThere()
    {
        var text = new string('x', RunawayOutput.CeilingChars + 10);

        Assert.Equal(RunawayOutput.CeilingChars, RunawayOutput.LoopStart(text));
    }
}
