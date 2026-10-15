using fuseraft.Cli.Tui;

namespace FuseraftCli.Tests;

public sealed class PromptCompletionTests
{
    private readonly PromptCompletion _completion = new() { Skills = () => ["review", "release-notes"] };

    [Fact]
    public void Completes_slash_commands_with_their_help_text()
    {
        var (start, items) = _completion.Suggest("/hi");

        Assert.Equal(0, start);
        Assert.Contains(items, s => s.Text == "/history");
        var hitl = Assert.Single(items, s => s.Text == "/hitl");
        Assert.Contains("HITL", hitl.Note);
    }

    [Fact]
    public void A_complete_command_suggests_nothing()
    {
        Assert.Empty(_completion.Suggest("/help").Items);
    }

    [Fact]
    public void Completes_subcommands_from_after_the_space()
    {
        var (start, items) = _completion.Suggest("/hitl o");

        Assert.Equal(6, start);
        Assert.Equal(["off", "on"], items.Select(s => s.Text));
    }

    [Fact]
    public void Only_the_first_argument_is_completed()
    {
        Assert.Empty(_completion.Suggest("/mcp add x").Items);
    }

    [Fact]
    public void Completes_skills_after_a_dollar()
    {
        var (_, items) = _completion.Suggest("$re");

        Assert.Equal(["$review", "$release-notes"], items.Select(s => s.Text));
    }

    [Fact]
    public void Plain_text_and_multi_line_drafts_get_nothing()
    {
        Assert.Empty(_completion.Suggest("fix the bug").Items);
        Assert.Empty(_completion.Suggest("line one\n/he").Items);
    }

    [Fact]
    public async Task Completes_paths_after_an_at()
    {
        var root = Directory.CreateTempSubdirectory("fuseraft-at-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "parser.py"), "");
            var completion = new PromptCompletion { Files = new FileIndex(root, _ => false).Warm() };
            for (var i = 0; i < 100 && completion.Suggest("@").Items.Count == 0; i++)
                await Task.Delay(20);

            var (start, items) = completion.Suggest("why does @pars");

            Assert.Equal(9, start);
            Assert.Equal(["@src/parser.py"], items.Select(s => s.Text));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Completes_the_words_screen_commands_take()
    {
        Assert.Contains(_completion.Suggest("/theme r").Items, s => s.Text == "rust");
        Assert.Equal(["top"], _completion.Suggest("/dock t").Items.Select(s => s.Text));
        Assert.Equal(["code"], _completion.Suggest("/copy ").Items.Select(s => s.Text));
    }
}
