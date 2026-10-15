using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

public sealed class ReplMentionsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fuseraft-mentions-").FullName;

    public ReplMentionsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "a.py"), "print(1)\n");
        File.WriteAllText(Path.Combine(_root, "shot.png"), "x");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Parse_finds_existing_paths_and_strips_trailing_punctuation()
    {
        Assert.Equal(["src/a.py", "src"], ReplMentions.Parse("why does @src/a.py fail? see @src/.", _root));
    }

    [Fact]
    public void Parse_leaves_out_images_missing_paths_emails_and_paths_outside()
    {
        Assert.Empty(ReplMentions.Parse("@shot.png @missing.txt me@src/a.py @../etc", _root));
    }

    [Fact]
    public async Task Attach_reads_through_the_session_tools_and_reports_each()
    {
        var calls = new List<string>();
        List<AIFunction> tools =
        [
            AIFunctionFactory.Create((string path) => { calls.Add("read " + path); return "1 | print(1)"; }, "read_file"),
            AIFunctionFactory.Create((string directory) => { calls.Add("list " + directory); return "[DENIED] nope"; }, "list_files"),
        ];
        var reports = new List<(string, bool)>();

        var note = await ReplMentions.AttachAsync("@src/a.py and @src", _root, tools, (what, ok) => reports.Add((what, ok)), default);

        Assert.Equal(["read src/a.py", "list src"], calls);
        Assert.Contains("<file path=\"src/a.py\">\n1 | print(1)\n</file>", note);
        Assert.Equal(("@src/a.py", true), reports[0]);
        Assert.Contains("[DENIED]", reports[1].Item1);
        Assert.False(reports[1].Item2);
    }

    [Fact]
    public async Task Attach_without_the_tool_attaches_nothing()
    {
        var reports = new List<(string, bool)>();

        var note = await ReplMentions.AttachAsync("@src/a.py", _root, [], (what, ok) => reports.Add((what, ok)), default);

        Assert.Null(note);
        Assert.Contains("isn't available", Assert.Single(reports).Item1);
    }
}
