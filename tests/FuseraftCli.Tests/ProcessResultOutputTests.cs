using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>A long failing log is cut to its head and tail; a failure from the middle is still shown.</summary>
public sealed class ProcessResultOutputTests
{
    // Shaped like the kiwi stdlib suite's log, where a live run's one failing test sat in the omitted middle.
    private static string SuiteLog(int tests, int failingAt)
    {
        var lines = new List<string> { "Running Kiwi Stdlib Test Suite v1.0.0..." };
        for (var i = 0; i < tests; i++)
        {
            if (i == failingAt)
            {
                lines.Add("Running test: web: sessions round-trip through httpserver and a real Server FAILED in 12.1ms");
                lines.Add("  FAIL: expected a fresh cookie after a rejected one");
            }
            else
                lines.Add($"Running test: stdlib: case number {i} does what it says on the tin PASSED in 0.42ms");
        }
        lines.Add($"Ran {tests} tests: {tests - 1} passed, 1 failed");
        return string.Join("\n", lines);
    }

    [Fact]
    public void AFailureInTheOmittedMiddle_IsStillShown()
    {
        var log = SuiteLog(tests: 500, failingAt: 250);

        var output = new ProcessResult(log, string.Empty, 1).ToPluginOutput();

        Assert.Contains("chars omitted", output);
        Assert.Contains("[failure lines from the omitted part:]", output);
        Assert.Contains("FAIL: expected a fresh cookie after a rejected one", output);
        Assert.Contains("sessions round-trip through httpserver and a real Server FAILED", output);
        Assert.Contains("1 failed", output);
        Assert.True(output.Length < log.Length / 2, $"{output.Length} of {log.Length} chars");
    }

    [Fact]
    public void WithoutFailureLinesInTheMiddle_OnlyTheNoticeIsAdded()
    {
        var log = SuiteLog(tests: 500, failingAt: 3);

        var output = new ProcessResult(log, string.Empty, 1).ToPluginOutput();

        Assert.Contains("chars omitted", output);
        Assert.DoesNotContain("[failure lines from the omitted part:]", output);
    }

    [Fact]
    public void FailureLines_AreCapped()
    {
        var middle = string.Join("\n", Enumerable.Range(0, 500).Select(i => $"error: thing {i} went wrong"));

        var kept = ProcessResult.FailureLines(middle);

        Assert.True(kept.Length <= 4_100, $"{kept.Length} chars");
        Assert.EndsWith("…", kept);
    }
}
