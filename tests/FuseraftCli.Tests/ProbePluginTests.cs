using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="ProbePlugin"/>. Shells out to real "bash" (matching this codebase's
/// convention of not mocking process execution — see ShellPluginTests) since AssertOutputAsync/
/// CompareOutputsAsync/RunHypothesisAsync always run their commands via bash regardless of any
/// "language" concept, and ProbeCodeAsync's own language runner map is only meaningfully
/// exercised for "bash", which is guaranteed present in this test environment.
/// </summary>
public sealed class ProbePluginTests
{
    // ProbeCodeAsync

    [Fact]
    public async Task ProbeCode_UnsupportedLanguage_ReturnsErrorListingSupported()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.ProbeCodeAsync("cobol", "DISPLAY 'HI'.");

        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("Unsupported language 'cobol'", result);
        Assert.Contains("bash", result);
    }

    [Fact]
    public async Task ProbeCode_Bash_Success_ReturnsStdoutAndExitCode()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.ProbeCodeAsync("bash", "echo hello-probe");

        Assert.Contains("LANGUAGE : bash", result);
        Assert.Contains("EXIT CODE: 0", result);
        Assert.Contains("hello-probe", result);
    }

    [Fact]
    public async Task ProbeCode_Bash_NoStdout_ReportsEmpty()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.ProbeCodeAsync("bash", "true");

        Assert.Contains("STDOUT   : (empty)", result);
    }

    [Fact]
    public async Task ProbeCode_Bash_NonZeroExit_ReportsExitCode()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.ProbeCodeAsync("bash", "exit 7");

        Assert.Contains("EXIT CODE: 7", result);
    }

    // AssertOutputAsync

    [Fact]
    public async Task AssertOutput_Contains_Match_Passes()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.AssertOutputAsync("echo the-value-here", "value-here", matchType: "contains");

        Assert.Contains("VERDICT: PASS", result);
    }

    [Fact]
    public async Task AssertOutput_Contains_NoMatch_FailsWithActualOutput()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.AssertOutputAsync("echo something-else", "value-here", matchType: "contains");

        Assert.Contains("VERDICT: FAIL", result);
        Assert.Contains("something-else", result);
    }

    [Fact]
    public async Task AssertOutput_Equals_ExactMatch_Passes()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.AssertOutputAsync("echo exact", "exact", matchType: "equals");

        Assert.Contains("VERDICT: PASS", result);
    }

    [Fact]
    public async Task AssertOutput_Equals_Mismatch_FailsWithDiff()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.AssertOutputAsync("echo actual-value", "expected-value", matchType: "equals");

        Assert.Contains("VERDICT: FAIL", result);
        Assert.Contains("EXPECTED:", result);
        Assert.Contains("ACTUAL:", result);
    }

    [Fact]
    public async Task AssertOutput_Regex_Match_Passes()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.AssertOutputAsync("echo build-42-ok", @"build-\d+-ok", matchType: "regex");

        Assert.Contains("VERDICT: PASS", result);
    }

    [Fact]
    public async Task AssertOutput_Regex_InvalidPattern_FailsWithMessage()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.AssertOutputAsync("echo x", "[unterminated", matchType: "regex");

        Assert.Contains("VERDICT: FAIL", result);
        Assert.Contains("Invalid regex pattern", result);
    }

    [Fact]
    public async Task AssertOutput_ExitCode_Match_Passes()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.AssertOutputAsync("exit 3", "3", matchType: "exitcode");

        Assert.Contains("VERDICT: PASS", result);
    }

    [Fact]
    public async Task AssertOutput_ExitCode_Mismatch_Fails()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.AssertOutputAsync("exit 3", "0", matchType: "exitcode");

        Assert.Contains("VERDICT: FAIL", result);
    }

    [Fact]
    public async Task AssertOutput_UnknownMatchType_Fails()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.AssertOutputAsync("echo x", "x", matchType: "fuzzy");

        Assert.Contains("VERDICT: FAIL", result);
        Assert.Contains("Unknown matchType", result);
    }

    [Fact]
    public async Task AssertOutput_HtmlEncodedExpected_IsDecodedBeforeComparison()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.AssertOutputAsync("echo '<tag>'", "&lt;tag&gt;", matchType: "contains");

        Assert.Contains("VERDICT: PASS", result);
    }

    // CompareOutputsAsync

    [Fact]
    public async Task CompareOutputs_SameOutput_ReportsMatch()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.CompareOutputsAsync("echo same", "echo same");

        Assert.Contains("OUTPUTS MATCH: yes", result);
        Assert.Contains("=== A ===", result);
        Assert.Contains("=== B ===", result);
    }

    [Fact]
    public async Task CompareOutputs_DifferentOutput_ReportsNoMatch()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.CompareOutputsAsync("echo first", "echo second");

        Assert.Contains("OUTPUTS MATCH: no", result);
        Assert.Contains("first", result);
        Assert.Contains("second", result);
    }

    // RunHypothesisAsync

    [Fact]
    public async Task RunHypothesis_NoSetup_ProbesDirectly()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.RunHypothesisAsync(
            "Echo prints its argument.", "echo works", "works");

        Assert.DoesNotContain("SETUP", result);
        Assert.Contains("VERDICT: PASS", result);
    }

    [Fact]
    public async Task RunHypothesis_ObservationFound_Passes()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.RunHypothesisAsync(
            "The counter increments.", "echo counter=1", "counter=1");

        Assert.Contains("VERDICT: PASS", result);
    }

    [Fact]
    public async Task RunHypothesis_ObservationNotFound_Fails()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.RunHypothesisAsync(
            "The counter increments.", "echo counter=0", "counter=1");

        Assert.Contains("VERDICT: FAIL", result);
    }

    [Fact]
    public async Task RunHypothesis_SetupFails_SkipsProbeAndFails()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.RunHypothesisAsync(
            "Should never be reached.", "echo should-not-run", "should-not-run",
            setupCommand: "exit 1");

        Assert.Contains("VERDICT: FAIL", result);
        Assert.Contains("Setup command failed", result);
        Assert.DoesNotContain("should-not-run", result);
    }

    [Fact]
    public async Task RunHypothesis_SetupSucceeds_RunsProbeAfterward()
    {
        var plugin = new ProbePlugin();
        var result = await plugin.RunHypothesisAsync(
            "Setup output is visible.", "echo probed", "probed",
            setupCommand: "echo setup-ran");

        Assert.Contains("SETUP", result);
        Assert.Contains("setup-ran", result);
        Assert.Contains("VERDICT: PASS", result);
    }
}
