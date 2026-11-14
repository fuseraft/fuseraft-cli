using fuseraft.Core;
using fuseraft.Infrastructure.Agents;
using fuseraft.Infrastructure.Context;
using fuseraft.Infrastructure.Plugins;
using Microsoft.Extensions.AI;

namespace FuseraftCli.Tests;

/// <summary>A command run again back-to-back gets its cached output; after any other call in between it runs again.</summary>
public sealed class ShellPluginRunCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fuseraft_runcache_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ToolCallSequence _calls = new();
    private readonly FileLedger _ledger = new();
    private readonly ShellPlugin _shell;
    private readonly AIFunction _run;
    private readonly string _file;

    public ShellPluginRunCacheTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "test.txt");
        File.WriteAllText(_file, "FAILED\n");
        _shell = new ShellPlugin(sandboxRoot: _dir, toolCalls: _calls);
        _run   = PluginRegistry.GetFunctionsFromObject(_shell).Single(f => f.Name == "shell_run");
    }

    public void Dispose()
    {
        _shell.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<string> Invoke(AIFunction tool, AIFunctionArguments args) =>
        ToolResultText.ToStringOrDefault(await AgentMiddlewareBuilder.ChangeReportingInvoker(null, _ledger, _calls)(
            new FunctionInvocationContext { Function = tool, Arguments = args }, CancellationToken.None));

    private Task<string> Cat() =>
        Invoke(_run, new AIFunctionArguments { ["command"] = $"cat '{_file}'", ["workingDirectory"] = _dir });

    [Fact]
    public async Task BackToBack_GetsTheCachedOutput()
    {
        await Cat();

        Assert.Contains("already ran this turn", await Cat());
    }

    // As a live run did: the test failed, the model patched it, and the same command must show the new result.
    [Fact]
    public async Task AfterAnEditInBetween_ItRunsAgain()
    {
        var patch = AIFunctionFactory.Create(() => { File.WriteAllText(_file, "PASSED\n"); return "patched"; }, "patch_file");

        Assert.Contains("FAILED", await Cat());
        await Invoke(patch, []);
        var again = await Cat();

        Assert.DoesNotContain("already ran", again);
        Assert.Contains("PASSED", again);
    }
}
