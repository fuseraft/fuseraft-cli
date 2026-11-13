using fuseraft.Core;
using fuseraft.Infrastructure.Agents;
using fuseraft.Infrastructure.Context;
using Microsoft.Extensions.AI;

namespace FuseraftCli.Tests;

/// <summary>Each tool call reports changes to files the agent has seen: made before it outside fuseraft, or while it ran.</summary>
public sealed class AgentMiddlewareBuilderChangeReportingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fuseraft_report_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FileLedger _ledger = new() { CollectToolChanges = true };
    private readonly string _file;

    public AgentMiddlewareBuilderChangeReportingTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(_file, "one\n");
        _ledger.RecordSeen(_file);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ValueTask<object?> Invoke(AIFunction tool) =>
        AgentMiddlewareBuilder.ChangeReportingInvoker(null, _ledger)(
            new FunctionInvocationContext { Function = tool, Arguments = new AIFunctionArguments() }, CancellationToken.None);

    [Fact]
    public async Task AChangeTheToolMade_IsReportedAsWhileItRan_AndNoted()
    {
        var tool = AIFunctionFactory.Create(() => { File.WriteAllText(_file, "one\ntwo\n"); return "ok"; }, "shell_run");

        var result = Assert.IsType<string>(await Invoke(tool));

        Assert.Contains("while shell_run ran", result);
        Assert.Equal(_file, Assert.Single(_ledger.TakeToolChanges()).Path);
    }

    [Fact]
    public async Task AChangeBeforeTheTool_IsReportedAsOutside_WithItsLines()
    {
        File.WriteAllText(_file, "one\nfrom the editor\n");
        var tool = AIFunctionFactory.Create(() => "listing", "list_files");

        var result = Assert.IsType<string>(await Invoke(tool));

        Assert.StartsWith("listing", result);
        Assert.Contains("changed outside fuseraft", result);
        Assert.Contains("+ from the editor", result);
        Assert.Empty(_ledger.TakeToolChanges());
    }

    [Fact]
    public async Task NoChanges_LeaveTheResultAlone()
    {
        var tool = AIFunctionFactory.Create(() => "listing", "list_files");

        var result = await Invoke(tool);

        Assert.DoesNotContain("fuseraft:", result?.ToString());
    }

    [Fact]
    public async Task InASubagent_NothingIsReported_SoTheParentHearsOfIt()
    {
        var tool = AIFunctionFactory.Create(() => { File.WriteAllText(_file, "one\ntwo\n"); return "ok"; }, "shell_run");

        var result = await Task.Run(async () => { ReadScope.BeginInThisFlow(); return await Invoke(tool); });

        Assert.DoesNotContain("fuseraft:", result?.ToString());
        Assert.Single(_ledger.Sweep());
    }
}
