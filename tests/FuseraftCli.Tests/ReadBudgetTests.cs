using fuseraft.Cli.Commands.Repl;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// The REPL sizes the per-turn read_file budget from the model's working context budget, and the
/// plugin asks for it on every read so a model switch takes effect mid-session.
/// </summary>
public sealed class ReadBudgetTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fuseraft-read-budget-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("xai.grok-4.6", 300_000)]
    [InlineData("gpt-4o", 200_000)]
    [InlineData("llama3:8b", 160_000)]
    public void ReadBudget_IsHalfTheContextBudget_InChars(string model, int expectedChars) =>
        Assert.Equal(expectedChars, ModelContextWindow.GetReadBudgetChars(ModelContextWindow.GetBudget(model)));

    [Fact]
    public void ReadBudget_FollowsTheContextBudgetOverride() =>
        Assert.Equal(800_000, ModelContextWindow.GetReadBudgetChars(ModelContextWindow.GetBudget("xai.grok-4.6", 400_000)));

    [Fact]
    public async Task Plugin_AsksForItsBudgetOnEveryRead()
    {
        var path = Path.Combine(_dir, "a.txt");
        await File.WriteAllTextAsync(path, new string('x', 2_000));
        var budget = 1_000;
        var fs = new FileSystemPlugin(sandboxRoot: _dir);
        fs.SetReadBudgetSource(() => budget);

        Assert.Contains("Truncated to fit per-turn read budget of 1,000", await fs.ReadFileAsync(path, maxLines: 1));

        budget = 100_000;   // e.g. /model switched to a larger-context model
        ((ITurnResettable)fs).BeginTurn();
        Assert.DoesNotContain("budget", await fs.ReadFileAsync(path, maxLines: 1));
    }

    [Fact]
    public async Task Plugin_NonPositiveBudget_FallsBackToTheConstructorBudget()
    {
        var path = Path.Combine(_dir, "a.txt");
        await File.WriteAllTextAsync(path, new string('x', 2_000));
        var fs = new FileSystemPlugin(sandboxRoot: _dir, readBudgetPerTurn: 500);
        fs.SetReadBudgetSource(() => 0);

        Assert.Contains("read budget of 500", await fs.ReadFileAsync(path, maxLines: 1));
    }
}
