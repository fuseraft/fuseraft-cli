using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

/// <summary>
/// Regression tests for <see cref="ReplTurn.SanitizeAssistantResponse"/> (#119): the model
/// sometimes quotes a prior tool result back out as if it were the user-facing answer, e.g.
/// literally echoing <c>[OK] Patched '...'</c> (fuseraft's own <c>PluginResult</c>/
/// <c>ProcessHelper.Ok</c>/<c>Error</c> prefix convention) instead of actually answering.
/// </summary>
public sealed class ReplTurnSanitizeAssistantResponseTests
{
    [Fact]
    public void GenuineAnswer_PassesThroughUnchanged()
    {
        const string answer = "I added a Project property to ApiSettings and patched AppSettings.cs accordingly.";

        var result = ReplTurn.SanitizeAssistantResponse(answer, out var warning);

        Assert.Equal(answer, result);
        Assert.Null(warning);
    }

    [Fact]
    public void RealIncident_ToolCallsHeaderWrappingSingleOkLine_IsSanitized()
    {
        // The actual turn-15 leak from #119's session log: a short structural wrapper around
        // one [OK]-prefixed line. A naive per-line majority check would miss this (1 of 4
        // non-blank lines matches) — the fix must weigh by content, not line count.
        const string leaked = "[\n\nTool Calls]\npatch_file:\n  - [OK] Patched 'C:\\workspace\\source\\QG\\AppSettings.cs': added Project property to ApiSettings.";

        var result = ReplTurn.SanitizeAssistantResponse(leaked, out var warning);

        Assert.Equal(string.Empty, result);
        Assert.NotNull(warning);
        Assert.Contains("internal tool-call text", warning);
    }

    [Fact]
    public void MultipleOkErrorLines_IsSanitized()
    {
        const string leaked = "[OK] Written 120 chars to foo.cs\n[OK] Patched 'bar.cs': replaced 2-line block\n[ERROR] File not found: baz.cs";

        var result = ReplTurn.SanitizeAssistantResponse(leaked, out var warning);

        Assert.Equal(string.Empty, result);
        Assert.NotNull(warning);
    }

    [Fact]
    public void SingleBareOkLine_IsSanitized()
    {
        const string leaked = "[OK] Patched 'MainForm.Designer.cs': replaced 1-line block with 1-line block at character offset 42.";

        var result = ReplTurn.SanitizeAssistantResponse(leaked, out var warning);

        Assert.Equal(string.Empty, result);
        Assert.NotNull(warning);
    }

    [Fact]
    public void ResponseWithOneShortOkLineAmongSubstantialProse_IsNotSanitized()
    {
        // A single "[OK] ..."-prefixed line that starts a line (so it does match the pattern)
        // but is a small fraction of an otherwise substantial explanation must not trip the
        // dominance check — only a response that's mostly composed of result lines should.
        const string answer =
            "Summary of what I did:\n" +
            "[OK] tests passing\n\n" +
            "I also rewrote the config loader to support layered overrides, added validation " +
            "for malformed YAML, documented the new behavior in the README under a new " +
            "\"Configuration\" section, and wrote three new integration tests covering the " +
            "override precedence rules end to end.";

        var result = ReplTurn.SanitizeAssistantResponse(answer, out var warning);

        Assert.Equal(answer, result);
        Assert.Null(warning);
    }

    [Fact]
    public void LegacyToFunctionsPattern_StillSanitized()
    {
        const string leaked = "to=functions.patch_file {\"path\":\"foo.cs\"}";

        var result = ReplTurn.SanitizeAssistantResponse(leaked, out var warning);

        Assert.Equal(string.Empty, result);
        Assert.NotNull(warning);
    }

    [Fact]
    public void EmptyResponse_ReturnsEmptyWithNoWarning()
    {
        var result = ReplTurn.SanitizeAssistantResponse("   ", out var warning);

        Assert.Equal(string.Empty, result);
        Assert.Null(warning);
    }
}
