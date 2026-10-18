using System.IO.Pipelines;
using fuseraft.Cli.Commands.Repl;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FuseraftCli.Tests;

public sealed class ReplMcpApprovalTests : IAsyncLifetime
{
    private readonly Pipe _toServer = new(), _toClient = new();
    private McpServer _server = null!;
    private McpClient _client = null!;
    private Task _serverRun = null!;
    private readonly List<string> _ran = [];

    public async Task InitializeAsync()
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "test", Version = "1" },
            ToolCollection =
            [
                McpServerTool.Create((string sql) => { _ran.Add("query " + sql); return "rows"; },
                    new() { Name = "query", ReadOnly = true }),
                McpServerTool.Create((string title) => { _ran.Add("create " + title); return "created"; },
                    new() { Name = "create_issue" }),
                McpServerTool.Create(() => new string('x', 500), new() { Name = "dump", ReadOnly = true }),
                McpServerTool.Create(() => "{\"rows\":[" + string.Join(",", Enumerable.Range(1, 20).Select(i => $"{{\"ID\":{i}}}")) + "]}",
                    new() { Name = "rows", ReadOnly = true }),
            ],
        };
        _server = McpServer.Create(new StreamServerTransport(_toServer.Reader.AsStream(), _toClient.Writer.AsStream()), options);
        _serverRun = _server.RunAsync();
        _client = await McpClient.CreateAsync(new StreamClientTransport(_toServer.Writer.AsStream(), _toClient.Reader.AsStream()));
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }

    private async Task<Dictionary<string, AIFunction>> GatedAsync(Func<string, string, Task<bool>>? approve) =>
        ReplMcpApproval.Gate("gh", (await _client.ListToolsAsync()).Cast<AIFunction>(), approve).ToDictionary(t => t.Name);

    [Fact]
    public async Task A_read_only_tool_runs_without_asking()
    {
        var asked = 0;
        var tools = await GatedAsync((_, _) => { asked++; return Task.FromResult(false); });

        await tools["query"].InvokeAsync(new() { ["sql"] = "SELECT 1" });

        Assert.Equal(0, asked);
        Assert.Equal(["query SELECT 1"], _ran);
    }

    [Fact]
    public async Task Any_other_tool_asks_with_its_arguments_and_runs_once_allowed()
    {
        var asked = new List<(string Action, string Detail)>();
        var tools = await GatedAsync((action, detail) => { asked.Add((action, detail)); return Task.FromResult(true); });

        var result = await tools["create_issue"].InvokeAsync(new() { ["title"] = "Bug" });

        Assert.Equal([("gh.create_issue", "title: Bug")], asked);
        Assert.Equal(["create Bug"], _ran);
        Assert.Equal("created", fuseraft.Infrastructure.Mcp.McpToolResult.Display(result));
    }

    [Fact]
    public async Task A_blocked_call_never_reaches_the_server()
    {
        var tools = await GatedAsync((_, _) => Task.FromResult(false));

        var result = await tools["create_issue"].InvokeAsync(new() { ["title"] = "Bug" });

        Assert.Empty(_ran);
        Assert.Equal("[DENIED] MCP tool 'create_issue' on 'gh' blocked by user.", result?.ToString());
        Assert.True(ReplTurn.IsToolFailureText(result?.ToString()));
    }

    // Every REPL tool is wrapped (logging, offload) before safe mode looks at it.
    [Fact]
    public async Task Read_only_is_seen_through_the_wrappers_and_never_for_a_gated_tool()
    {
        var events = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        using var emitter = new fuseraft.Core.Events.EventEmitter(events);
        var store = new fuseraft.Infrastructure.Tools.ToolResultArtifactStore(null, emitter);
        var tools = (await GatedAsync((_, _) => Task.FromResult(true)))
            .ToDictionary(kv => kv.Key, kv => ReplSessionContext.WrapTool(kv.Value, emitter, store));

        Assert.True(ReplMcpApproval.IsReadOnly(tools["query"]));
        Assert.False(ReplMcpApproval.IsReadOnly(tools["create_issue"]));
        File.Delete(events);
    }

    // The SDK hands back an MCP result as its content, not a string, which the offload filter used to pass
    // through whatever its size.
    [Fact]
    public async Task An_oversized_result_is_offloaded()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fuseraft-test-artifacts-{Guid.NewGuid():N}");
        var events = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        using var emitter = new fuseraft.Core.Events.EventEmitter(events);
        var store = new fuseraft.Infrastructure.Tools.ToolResultArtifactStore(dir, emitter) { ThresholdChars = 100 };
        var dump = ReplSessionContext.WrapTool((await GatedAsync(null))["dump"], emitter, store);

        var result = await dump.InvokeAsync(new());

        var stub = Assert.IsType<string>(result);
        Assert.Contains("offloaded", stub);
        Assert.Equal(new string('x', 500), File.ReadAllText(Assert.Single(Directory.GetFiles(dir, "*.txt"))));
        Directory.Delete(dir, recursive: true);
        File.Delete(events);
    }

    // A server's JSON answer is stored laid out, so a search of the stored copy finds the one row.
    [Fact]
    public async Task An_offloaded_json_answer_is_stored_a_row_per_line()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fuseraft-test-artifacts-{Guid.NewGuid():N}");
        var store = new fuseraft.Infrastructure.Tools.ToolResultArtifactStore(dir) { ThresholdChars = 50 };
        var events = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        using var emitter = new fuseraft.Core.Events.EventEmitter(events);
        var rows = ReplSessionContext.WrapTool((await GatedAsync(null))["rows"], emitter, store);

        await rows.InvokeAsync(new());

        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(dir, "*.txt")));
        Assert.Single(lines, l => l.Trim() == "\"ID\": 17");
        Directory.Delete(dir, recursive: true);
        File.Delete(events);
    }

    [Fact]
    public async Task Without_an_approval_gate_tools_are_left_as_they_are()
    {
        var tools = await GatedAsync(null);

        Assert.All(tools.Values, t => Assert.IsType<McpClientTool>(t));
    }
}
