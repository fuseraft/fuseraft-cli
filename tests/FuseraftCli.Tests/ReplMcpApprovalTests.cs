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

    [Fact]
    public async Task Without_an_approval_gate_tools_are_left_as_they_are()
    {
        var tools = await GatedAsync(null);

        Assert.All(tools.Values, t => Assert.IsType<McpClientTool>(t));
    }
}
