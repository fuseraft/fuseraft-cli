using Microsoft.Extensions.AI;
using fuseraft.Core.SubAgents;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Covers how <see cref="SubAgentPlugin"/> binds user-defined agents to a tool pool and a model,
/// and — the part with security weight — that the session's tool gate reaches every sub-agent run,
/// built-in and custom alike.
/// </summary>
public sealed class SubAgentPluginCustomAgentTests
{
    private static AIFunction Tool(string name) => AIFunctionFactory.Create(() => "ok", name);

    // Like the real explorer pool, this includes shell_run — which is NOT read-only.
    private static readonly AIFunction[] ReadOnly = [Tool("read_file"), Tool("list_files"), Tool("search_content"), Tool("shell_run")];
    private static readonly AIFunction[] Write    = [Tool("read_file"), Tool("write_file"), Tool("shell_run"), Tool("git_commit")];

    private static SubAgentDefinition Def(
        string name = "reviewer", string description = "Reviews code.", string body = "You are a reviewer.",
        string? model = null, string[]? tools = null, int max = 12) =>
        new(name, description, body, model, tools, max, $"/agents/{name}.md", "project");

    /// <summary>Records what it was asked, replies with fixed text, never calls a tool.</summary>
    private sealed class RecordingClient(string reply = "report") : IChatClient
    {
        public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];
        public object? GetService(Type t, object? k) => null;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> msgs, ChatOptions? opts, CancellationToken ct)
        {
            Calls.Add((msgs.ToList(), opts));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> msgs, ChatOptions? opts, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Calls.Add((msgs.ToList(), opts));
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
        }

        public void Dispose() { }

        public string[] LastToolNames =>
            Calls[^1].Options?.Tools?.Select(t => t.Name).ToArray() ?? [];
        public string LastSystemPrompt => Calls[^1].Messages.First(m => m.Role == ChatRole.System).Text!;
    }

    private static SubAgentPlugin Plugin(
        RecordingClient client, SubAgentDefinition[] defs, Func<string, IChatClient?>? clientFactory = null) =>
        new(client, ReadOnly, delegateTools: Write, customAgents: defs, customAgentClientFactory: clientFactory);

    // ── Tool binding ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DefaultTools_AreTheReadOnlyExplorerSet_NeverTheWriteSet()
    {
        var client = new RecordingClient();
        var p = Plugin(client, [Def()]);

        await p.RunAgentAsync("reviewer", "review it");

        Assert.Equal(["list_files", "read_file", "search_content"], client.LastToolNames.Order());
    }

    [Fact]
    public async Task DefaultTools_LeaveOutShellRun_BecauseAReadOnlyAgentMustBeReadOnly()
    {
        // Regression: the explorer pool contains shell_run, so a "read-only" default silently included it.
        var client = new RecordingClient();
        var p = Plugin(client, [Def()]);

        await p.RunAgentAsync("reviewer", "review it");

        Assert.DoesNotContain("shell_run", client.LastToolNames);
        Assert.DoesNotContain("shell_run", client.LastSystemPrompt);
    }

    [Fact]
    public async Task ShellRun_IsStillAvailable_WhenTheAgentNamesItExplicitly()
    {
        var client = new RecordingClient();
        var p = Plugin(client, [Def(tools: ["read_file", "shell_run"])]);

        await p.RunAgentAsync("reviewer", "x");

        Assert.Equal(["read_file", "shell_run"], client.LastToolNames.Order());
    }

    [Fact]
    public async Task NamedTools_ResolveAgainstBothPools()
    {
        var client = new RecordingClient();
        var p = Plugin(client, [Def(tools: ["read_file", "write_file"])]);

        await p.RunAgentAsync("reviewer", "x");

        Assert.Equal(["read_file", "write_file"], client.LastToolNames.Order());
    }

    [Fact]
    public async Task Wildcard_GrantsTheWholeUnion_WithoutDuplicates()
    {
        var client = new RecordingClient();
        var p = Plugin(client, [Def(tools: ["*"])]);

        await p.RunAgentAsync("reviewer", "x");

        var names = client.LastToolNames;
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.Contains("shell_run", names);
        Assert.Contains("search_content", names);   // from the read-only pool
    }

    [Fact]
    public async Task UnknownToolNames_AreDroppedWithAProblem_NotSilently()
    {
        var client = new RecordingClient();
        var p = Plugin(client, [Def(tools: ["read_file", "launch_missiles"])]);

        Assert.Contains(p.CustomAgentProblems, x => x.Contains("launch_missiles") && x.Contains("reviewer"));
        await p.RunAgentAsync("reviewer", "x");
        Assert.Equal(["read_file"], client.LastToolNames);
    }

    [Fact]
    public async Task ExplicitlyNoTools_SendsNoToolsAtAll_NotAnEmptyArray()
    {
        // Some providers reject `tools: []` outright.
        var client = new RecordingClient();
        var p = Plugin(client, [Def(tools: [])]);

        await p.RunAgentAsync("reviewer", "x");

        Assert.Null(client.Calls[^1].Options?.Tools);
        Assert.Contains("(none", client.LastSystemPrompt);
    }

    [Fact]
    public async Task CustomAgents_NeverGetThePluginsOwnTools_SoTheyCannotSpawnMoreAgents()
    {
        var client = new RecordingClient();
        var p = Plugin(client, [Def(tools: ["*"])]);

        await p.RunAgentAsync("reviewer", "x");

        Assert.DoesNotContain(client.LastToolNames, n => n.StartsWith("sub_agent_"));
    }

    // ── Prompt & options ────────────────────────────────────────────────────────

    [Fact]
    public async Task SystemPrompt_IsTheDefinitionsInstructions_PlusRuntimeContext()
    {
        var client = new RecordingClient();
        var p = Plugin(client, [Def(body: "You only ever answer in haiku.")]);

        await p.RunAgentAsync("reviewer", "the task text");

        Assert.StartsWith("You only ever answer in haiku.", client.LastSystemPrompt);
        Assert.Contains("'reviewer' sub-agent", client.LastSystemPrompt);
        Assert.Contains("read_file", client.LastSystemPrompt);
        Assert.Equal("the task text", client.Calls[^1].Messages.Last().Text);
    }

    // ── Model selection ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentWithAModel_RunsOnThatClient_NotTheDefault()
    {
        var def = new RecordingClient("from default");
        var own = new RecordingClient("from own model");
        string? asked = null;
        var p = Plugin(def, [Def(model: "cheap-model")], m => { asked = m; return own; });

        var result = await p.RunAgentAsync("reviewer", "x");

        Assert.Equal("cheap-model", asked);
        Assert.Equal("from own model", result);
        Assert.Empty(def.Calls);
    }

    [Fact]
    public async Task AgentWithoutAModel_UsesTheDefaultClient_AndNeverAsksTheFactory()
    {
        var def = new RecordingClient("from default");
        var asked = false;
        var p = Plugin(def, [Def()], _ => { asked = true; return new RecordingClient(); });

        Assert.Equal("from default", await p.RunAgentAsync("reviewer", "x"));
        Assert.False(asked);
    }

    [Fact]
    public async Task ModelThatCannotBeBuilt_FallsBackToTheDefault_WithAProblem()
    {
        var def = new RecordingClient("from default");
        var p = Plugin(def, [Def(model: "no-such-model")], _ => throw new InvalidOperationException("no provider serves it"));

        Assert.Contains(p.CustomAgentProblems, x => x.Contains("no-such-model") && x.Contains("no provider serves it"));
        Assert.Equal("from default", await p.RunAgentAsync("reviewer", "x"));
    }

    [Fact]
    public void ModelTheFactoryDeclines_IsReportedAndDoesNotAppearAsTheAgentsModel()
    {
        var p = Plugin(new RecordingClient(), [Def(model: "ghost")], _ => null);

        Assert.Contains(p.CustomAgentProblems, x => x.Contains("ghost"));
        Assert.Null(p.CustomAgents.Single().Model);
    }

    [Fact]
    public void ModelsAreOnlyBuiltForAgentsThatNameOne()
    {
        var built = new List<string>();
        var p = Plugin(new RecordingClient(),
            [Def("a"), Def("b", model: "m1"), Def("c", model: "m2")],
            m => { built.Add(m); return new RecordingClient(); });

        _ = p.CustomAgents;   // forces binding

        Assert.Equal(["m1", "m2"], built.Order());
    }

    // ── The tool the parent model sees ──────────────────────────────────────────

    [Fact]
    public void NoCustomAgents_MeansNoToolAndNoSchemaCost()
    {
        Assert.Null(Plugin(new RecordingClient(), []).BuildRunAgentTool());
    }

    [Fact]
    public void RunAgentTool_IsNamed_AndItsDescriptionListsEveryAgent()
    {
        var p = Plugin(new RecordingClient(), [
            Def("reviewer", "Reviews diffs."),
            Def("doc-writer", "Writes docs.\nSecond line."),
        ]);

        var tool = p.BuildRunAgentTool()!;

        Assert.Equal("sub_agent_run", tool.Name);
        Assert.Contains("- reviewer: Reviews diffs.", tool.Description);
        Assert.Contains("- doc-writer: Writes docs. Second line.", tool.Description);   // newlines flattened
    }

    [Fact]
    public void RunAgentTool_TruncatesAbsurdlyLongDescriptions()
    {
        var p = Plugin(new RecordingClient(), [Def(description: new string('d', 5000))]);

        Assert.True(p.BuildRunAgentTool()!.Description.Length < 900);
    }

    [Fact]
    public async Task RunAgentTool_Invokes_TheNamedAgent()
    {
        var client = new RecordingClient("the report");
        var tool = Plugin(client, [Def()]).BuildRunAgentTool()!;

        var result = await tool.InvokeAsync(new AIFunctionArguments { ["agent"] = "reviewer", ["task"] = "look at foo.cs" });

        Assert.Equal("the report", result?.ToString());
        Assert.Equal("look at foo.cs", client.Calls[^1].Messages.Last().Text);
    }

    [Fact]
    public async Task UnknownAgent_ExplainsWhatIsAvailable_InsteadOfThrowing()
    {
        var p = Plugin(new RecordingClient(), [Def("reviewer"), Def("doc-writer")]);

        var result = await p.RunAgentAsync("nope", "x");

        Assert.Contains("Unknown agent 'nope'", result);
        Assert.Contains("reviewer", result);
        Assert.Contains("doc-writer", result);
    }

    [Fact]
    public async Task AgentLookup_IsCaseAndWhitespaceInsensitive()
    {
        var p = Plugin(new RecordingClient("ok"), [Def("reviewer")]);

        Assert.Equal("ok", await p.RunAgentAsync("  ReViewer ", "x"));
    }

    [Fact]
    public async Task Streaming_DeliversChunks_ToTheCallback()
    {
        var p = Plugin(new RecordingClient("streamed text"), [Def()]);
        var seen = new List<string>();

        var (result, _, _) = await p.RunAgentStreamingAsync("reviewer", "x", c => { seen.Add(c); return Task.CompletedTask; });

        Assert.Equal("streamed text", result);
        Assert.Equal("streamed text", string.Concat(seen));
    }

    // ── The session tool gate ───────────────────────────────────────────────────

    [Fact]
    public async Task ToolGate_RemovesToolsFromACustomAgentsRun()
    {
        var client = new RecordingClient();
        var p = Plugin(client, [Def(tools: ["read_file", "shell_run", "git_commit"])]);
        p.ToolGate = name => name is not ("shell_run" or "git_commit");   // e.g. /safe-mode

        await p.RunAgentAsync("reviewer", "x");

        Assert.Equal(["read_file"], client.LastToolNames);
        Assert.DoesNotContain("shell_run", client.LastSystemPrompt);   // the prompt must not advertise what it cannot use
    }

    [Fact]
    public async Task ToolGate_IsReadAtRunTime_SoLaterToggleTakesEffect()
    {
        var client = new RecordingClient();
        var p = Plugin(client, [Def(tools: ["read_file", "shell_run"])]);
        var safeMode = false;
        p.ToolGate = name => !(safeMode && name == "shell_run");

        await p.RunAgentAsync("reviewer", "x");
        Assert.Contains("shell_run", client.LastToolNames);

        safeMode = true;
        await p.RunAgentAsync("reviewer", "x");
        Assert.DoesNotContain("shell_run", client.LastToolNames);
    }

    [Fact]
    public async Task ToolGate_AlsoRestrainsTheBuiltInDelegateSubAgent()
    {
        // Regression: /safe-mode closed Shell/Git/Http for the parent but sub_agent_delegate still handed
        // its sub-agent shell_run and git_commit, because nothing consulted the session's gate.
        var client = new RecordingClient();
        var p = Plugin(client, []);
        p.ToolGate = name => name is not ("shell_run" or "git_commit");

        await p.DelegateAsync("do something");

        Assert.Equal(["read_file", "write_file"], client.LastToolNames.Order());
    }

    [Fact]
    public async Task ToolGate_AlsoRestrainsExplore()
    {
        var client = new RecordingClient();
        var p = Plugin(client, []);
        p.ToolGate = name => name == "read_file";

        await p.ExploreAsync("where is x");

        Assert.Equal(["read_file"], client.LastToolNames);
    }

    [Fact]
    public async Task NoGate_LeavesEverythingAvailable()
    {
        var client = new RecordingClient();
        var p = Plugin(client, []);

        await p.DelegateAsync("do something");

        Assert.Equal(4, client.LastToolNames.Length);
    }
}
