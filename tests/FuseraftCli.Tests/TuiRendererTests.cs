using fuseraft.Cli.Tui;

namespace FuseraftCli.Tests;

public sealed class TuiRendererTests
{
    private readonly Transcript _transcript = new();
    private readonly TuiRenderer _renderer;

    private readonly ManualClock _clock = new();

    public TuiRendererTests() => _renderer = new TuiRenderer(_transcript, () => 80, _clock);

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private List<string> Texts() =>
        [.. _transcript.Lines().Select(line => string.Concat(line.Select(s => s.Text))).Where(l => l.Length > 0)];

    [Fact]
    public void A_reply_streams_as_markdown_and_each_message_starts_with_the_mark()
    {
        _renderer.Text("Here is **bold");
        _renderer.Text("** text\n");
        _renderer.ToolStarted("c1", "read_file", "src/a.cs");
        _renderer.Text("More.");
        _renderer.End(turn: true);
        _renderer.Text("Next turn.");

        Assert.Equal(["◆ Here is bold text", "  ↳ read_file src/a.cs · stopped", "◆ More.", "◆ Next turn."], Texts());
    }

    [Fact]
    public void A_failed_call_shows_its_first_line_and_every_result_is_kept()
    {
        _renderer.ToolStarted("c1", "shell_run", "make");
        _renderer.ToolFinished("c1", "shell_run", ok: false, "[EXIT 2] no rule\nmore");
        _renderer.ToolStarted("c2", "read_file", "a.txt");
        _renderer.ToolFinished("c2", "read_file", ok: true, "");

        Assert.Contains("    [EXIT 2] no rule", Texts());
        Assert.Equal([("shell_run make (failed)", "[EXIT 2] no rule\nmore"), ("read_file a.txt", "(no output)")],
            _renderer.Outputs.Select(o => (o.Title, o.Text)));
    }

    [Fact]
    public void A_kept_output_shows_the_arguments_beside_the_result()
    {
        _renderer.ToolStarted("c1", "query_database", "hr", arguments: "dbName: hr\nsql:\n  SELECT *\n  FROM t");
        _renderer.ToolFinished("c1", "query_database", ok: true, "[]");

        Assert.Equal("Arguments\n  dbName: hr\n  sql:\n    SELECT *\n    FROM t\n\nResult\n  []", _renderer.Outputs.Single().Text);
    }

    [Fact]
    public void An_mcp_result_shows_its_text_not_its_envelope()
    {
        _renderer.ToolStarted("c1", "query_database", "hr");
        _renderer.ToolFinished("c1", "query_database", ok: false,
            """{"content":[{"type":"text","text":"Only SELECT queries are permitted."}],"isError":true,"_meta":{}}""");

        Assert.Contains("    Only SELECT queries are permitted.", Texts());
        Assert.Equal("Only SELECT queries are permitted.", _renderer.Outputs.Single().Text);
    }

    [Fact]
    public void An_mcp_servers_json_answer_is_indented_for_the_viewer()
    {
        _renderer.ToolStarted("c1", "query_database", "crm");
        _renderer.ToolFinished("c1", "query_database", ok: true,
            """{"content":[{"type":"text","text":"{\"columns\":[\"ID\"],\"rows\":[{\"ID\":1,\"NAME\":\"Zoë\"}]}"}],"isError":false}""");

        Assert.Equal(
            """
            {
              "columns": [
                "ID"
              ],
              "rows": [
                {
                  "ID": 1,
                  "NAME": "Zoë"
                }
              ]
            }
            """.ReplaceLineEndings("\n"),
            _renderer.Outputs.Single().Text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_json_file_a_tool_read_is_shown_as_it_is()
    {
        _renderer.ToolStarted("c1", "read_file", "a.json");
        _renderer.ToolFinished("c1", "read_file", ok: true, """{"a":1}""");

        Assert.Equal("""{"a":1}""", _renderer.Outputs.Single().Text);
    }

    [Fact]
    public void FormatArguments_puts_multi_line_values_beneath_their_name()
    {
        var args = new Dictionary<string, object?>
        {
            ["dbName"]     = "hr",
            ["sql"]        = "SELECT 1\nFROM t",
            ["parameters"] = System.Text.Json.JsonDocument.Parse("""{"name":"Zoë"}""").RootElement,
            ["limit"]      = 5,
        };

        Assert.Equal("dbName: hr\nsql:\n  SELECT 1\n  FROM t\nparameters: {\"name\":\"Zoë\"}\nlimit: 5", TuiRenderer.FormatArguments(args));
        Assert.Equal("", TuiRenderer.FormatArguments(null));
    }

    [Fact]
    public void Thinking_folds_into_one_line_when_the_model_moves_on()
    {
        _renderer.Thinking("Let me look ");
        _renderer.Thinking("at the parser first.");
        _renderer.Text("Found it.");

        Assert.Equal(["  ∴ thought · 7 words · Ctrl+O", "◆ Found it."], Texts());
        Assert.Equal("Let me look at the parser first.", _renderer.Outputs.Single(o => o.Title == "thinking").Text);
    }

    [Fact]
    public void A_changed_file_shows_the_lines_it_added_and_removed()
    {
        _renderer.FileChanged("src/a.cs", 4, 1);
        _renderer.FileChanged("new.txt", null, null);

        Assert.Equal(["  ✎ src/a.cs +4 -1", "  ✎ new.txt"], Texts());
    }

    [Fact]
    public void A_subagent_call_is_nested_and_tagged()
    {
        _renderer.ToolStarted("x", "grep_file", "Parse", agent: "explore");

        Assert.Equal(["    [explore] ↳ grep_file Parse"], Texts());
    }

    [Fact]
    public void Summarize_prefers_the_argument_that_says_most()
    {
        Assert.Equal("dotnet test", TuiRenderer.Summarize(new Dictionary<string, object?> { ["timeout"] = 30, ["command"] = "dotnet test" }));
        Assert.Equal("first line", TuiRenderer.Summarize(new Dictionary<string, object?> { ["x"] = "first\nline" }));
        Assert.Equal("Foo|Bar", TuiRenderer.Summarize(new Dictionary<string, object?> { ["directory"] = "src", ["query"] = "Foo|Bar", ["filePattern"] = "*.ts" }));
        Assert.Equal("Parse", TuiRenderer.Summarize(new Dictionary<string, object?> { ["path"] = "src/Foo.cs", ["symbol"] = "Parse" }));
        Assert.Equal("src/Foo.cs", TuiRenderer.Summarize(new Dictionary<string, object?> { ["path"] = "src/Foo.cs", ["startLine"] = 10 }));
        Assert.Equal("", TuiRenderer.Summarize(null));
    }

    [Fact]
    public void A_message_that_starts_blank_gets_the_mark_on_its_first_line_of_text()
    {
        _renderer.Text("\n\n");
        _renderer.Text("Hi");
        _renderer.Text(" there\nsecond line\n");

        Assert.Equal(["◆ Hi there", "second line"], Texts());
    }

    [Fact]
    public void A_call_counts_up_while_it_runs_and_says_how_it_ended()
    {
        _renderer.ToolStarted("c1", "shell_run", "dotnet test");
        _renderer.ToolStarted("c2", "read_file", "a.txt");
        _renderer.ToolFinished("c2", "read_file", ok: true, "text");
        _clock.Now += TimeSpan.FromSeconds(1);
        _renderer.Tick();

        Assert.Equal(["  ↳ shell_run dotnet test", "  ↳ read_file a.txt ✓"], Texts());

        _clock.Now += TimeSpan.FromSeconds(4);
        _renderer.Tick();
        Assert.Equal("  ↳ shell_run dotnet test · 5s", Texts()[0]);

        _clock.Now += TimeSpan.FromSeconds(9);
        _renderer.ToolFinished("c1", "shell_run", ok: false, "[EXIT 1] 2 failed");
        Assert.Equal(["  ↳ shell_run dotnet test ✗ 14s", "  ↳ read_file a.txt ✓", "    [EXIT 1] 2 failed"], Texts());
    }

    [Fact]
    public void A_call_still_running_when_the_turn_ends_was_stopped()
    {
        _renderer.ToolStarted("c1", "shell_run", "sleep 60");
        _renderer.End(turn: true);
        _renderer.ToolFinished("c1", "shell_run", ok: true, "");

        Assert.Equal(["  ↳ shell_run sleep 60 · stopped"], Texts());
    }

    [Fact]
    public void A_call_line_is_redrawn_in_place_while_a_reply_streams_below_it()
    {
        _renderer.ToolStarted("c1", "read_file", "a.txt");
        _renderer.Text("Reading ");
        _renderer.ToolFinished("c1", "read_file", ok: true, "x");
        _renderer.Text("it.");

        Assert.Equal(["  ↳ read_file a.txt ✓", "◆ Reading it."], Texts());
    }
}
