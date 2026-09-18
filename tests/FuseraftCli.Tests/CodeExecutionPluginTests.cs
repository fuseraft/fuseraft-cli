using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="CodeExecutionPlugin"/> — Docker-backed sandboxed code execution and
/// REPL sessions. The highest-blast-radius untested plugin in the codebase prior to this file
/// (it executes arbitrary agent-authored code), so these deliberately exercise real Docker
/// container runs rather than mocking them out — a passing suite here is evidence the sandbox
/// actually runs code and actually isolates REPL session state, not just that the plumbing
/// compiles. Requires a working `docker` CLI (real Docker Engine or podman-docker compat); the
/// project's dev/CI environment has one.
/// </summary>
public sealed class CodeExecutionPluginTests
{
    private static CodeExecutionPlugin NewPlugin() => new();

    // -----------------------------------------------------------------------
    // CheckDockerAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CheckDockerAsync_DockerAvailable_ReturnsOkWithServerVersion()
    {
        var plugin = NewPlugin();

        var result = await plugin.CheckDockerAsync();

        Assert.StartsWith("[OK]", result);
        Assert.Contains("Docker is available", result);
    }

    // -----------------------------------------------------------------------
    // SandboxRunAsync — validation (no container involved)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SandboxRunAsync_UnsupportedLanguage_ReturnsErrorListingSupportedLanguages()
    {
        var plugin = NewPlugin();

        var result = await plugin.SandboxRunAsync("cobol", "PRINT 'hi'.");

        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("Unsupported language 'cobol'", result);
        Assert.Contains("python", result);
    }

    // -----------------------------------------------------------------------
    // SandboxRunAsync — real container execution
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SandboxRunAsync_Python_ExecutesAndReturnsStdout()
    {
        var plugin = NewPlugin();

        var result = await plugin.SandboxRunAsync("python", "print('hello-from-sandbox')", timeoutSeconds: 60);

        Assert.Contains("EXIT CODE: 0", result);
        Assert.Contains("hello-from-sandbox", result);
    }

    [Fact]
    public async Task SandboxRunAsync_PythonRaisesException_ReportsNonZeroExitAndStderr()
    {
        var plugin = NewPlugin();

        var result = await plugin.SandboxRunAsync("python", "raise ValueError('boom')", timeoutSeconds: 60);

        Assert.DoesNotContain("EXIT CODE: 0", result);
        Assert.Contains("STDERR:", result);
        Assert.Contains("ValueError", result);
    }

    [Fact]
    public async Task SandboxRunAsync_NoOutput_ReportsOutputNone()
    {
        var plugin = NewPlugin();

        var result = await plugin.SandboxRunAsync("python", "x = 1 + 1", timeoutSeconds: 60);

        Assert.Contains("EXIT CODE: 0", result);
        Assert.Contains("OUTPUT: (none)", result);
    }

    [Fact]
    public async Task SandboxRunAsync_NetworkAccess_IsBlocked()
    {
        // --network none is a hard security property of the sandbox (see DockerLimits) — a
        // script that tries to reach the network should fail rather than silently succeed.
        var plugin = NewPlugin();

        var result = await plugin.SandboxRunAsync(
            "python",
            "import socket\n" +
            "s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)\n" +
            "s.settimeout(3)\n" +
            "s.connect(('1.1.1.1', 80))\n" +
            "print('reached-network')",
            timeoutSeconds: 30);

        Assert.DoesNotContain("reached-network", result);
    }

    // -----------------------------------------------------------------------
    // REPL session lifecycle — validation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReplStartAsync_LanguageWithoutReplSupport_ReturnsError()
    {
        var plugin = NewPlugin();

        var result = await plugin.ReplStartAsync("bash");

        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("only supported for python and node", result);
    }

    [Fact]
    public async Task ReplExecAsync_UnknownSessionId_ReturnsError()
    {
        var plugin = NewPlugin();

        var result = await plugin.ReplExecAsync(Guid.NewGuid().ToString("N")[..8], "1 + 1");

        Assert.StartsWith("[ERROR]", result);
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task ReplResetAsync_UnknownSessionId_ReturnsError()
    {
        var plugin = NewPlugin();

        var result = await plugin.ReplResetAsync(Guid.NewGuid().ToString("N")[..8]);

        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task ReplStopAsync_UnknownSessionId_ReturnsError()
    {
        var plugin = NewPlugin();

        var result = await plugin.ReplStopAsync(Guid.NewGuid().ToString("N")[..8]);

        Assert.StartsWith("[ERROR]", result);
    }

    // -----------------------------------------------------------------------
    // REPL session lifecycle — real container execution
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReplStartAsync_ValidLanguage_ReturnsUsableSessionId()
    {
        var plugin = NewPlugin();

        var start = await plugin.ReplStartAsync("python");
        Assert.StartsWith("[OK]", start);

        var sessionId = ExtractSessionId(start);
        Assert.False(string.IsNullOrWhiteSpace(sessionId));

        // Clean up so this test doesn't leave a dangling session behind.
        await plugin.ReplStopAsync(sessionId);
    }

    [Fact]
    public async Task ReplExecAsync_AccumulatesStateAcrossCalls()
    {
        var plugin = NewPlugin();
        var sessionId = ExtractSessionId(await plugin.ReplStartAsync("python"));

        try
        {
            var first = await plugin.ReplExecAsync(sessionId, "x = 21 * 2", timeoutSeconds: 60);
            // First call defines x but prints nothing new — accumulated-so-far output is
            // suppressed by design, only output strictly after the separator counts.
            Assert.DoesNotContain("42", first);

            var second = await plugin.ReplExecAsync(sessionId, "print(x)", timeoutSeconds: 60);
            Assert.Contains("42", second);
        }
        finally
        {
            await plugin.ReplStopAsync(sessionId);
        }
    }

    [Fact]
    public async Task ReplExecAsync_OnlyReturnsOutputFromTheCurrentCall_NotAccumulatedOutput()
    {
        var plugin = NewPlugin();
        var sessionId = ExtractSessionId(await plugin.ReplStartAsync("python"));

        try
        {
            var first = await plugin.ReplExecAsync(sessionId, "print('turn-one-output')", timeoutSeconds: 60);
            Assert.Contains("turn-one-output", first);

            // Replaying turn one's code happens again internally to rebuild state, but its
            // stdout must stay suppressed — only turn two's own output should come back.
            var second = await plugin.ReplExecAsync(sessionId, "print('turn-two-output')", timeoutSeconds: 60);
            Assert.Contains("turn-two-output", second);
            Assert.DoesNotContain("turn-one-output", second);
        }
        finally
        {
            await plugin.ReplStopAsync(sessionId);
        }
    }

    [Fact]
    public async Task ReplResetAsync_ClearsAccumulatedState()
    {
        var plugin = NewPlugin();
        var sessionId = ExtractSessionId(await plugin.ReplStartAsync("python"));

        try
        {
            await plugin.ReplExecAsync(sessionId, "y = 99", timeoutSeconds: 60);

            var resetResult = await plugin.ReplResetAsync(sessionId);
            Assert.StartsWith("[OK]", resetResult);

            // y is no longer defined after reset — referencing it raises NameError.
            var afterReset = await plugin.ReplExecAsync(sessionId, "print(y)", timeoutSeconds: 60);
            Assert.Contains("NameError", afterReset);
        }
        finally
        {
            await plugin.ReplStopAsync(sessionId);
        }
    }

    [Fact]
    public async Task ReplStopAsync_RemovesSession_SubsequentExecFails()
    {
        var plugin = NewPlugin();
        var sessionId = ExtractSessionId(await plugin.ReplStartAsync("node"));

        var stopResult = await plugin.ReplStopAsync(sessionId);
        Assert.StartsWith("[OK]", stopResult);

        var afterStop = await plugin.ReplExecAsync(sessionId, "1 + 1");
        Assert.StartsWith("[ERROR]", afterStop);
        Assert.Contains("not found", afterStop);
    }

    [Fact]
    public async Task ReplExecAsync_Node_ExecutesAndAccumulatesState()
    {
        var plugin = NewPlugin();
        var sessionId = ExtractSessionId(await plugin.ReplStartAsync("node"));

        try
        {
            await plugin.ReplExecAsync(sessionId, "const greeting = 'hi-from-node';", timeoutSeconds: 60);
            var result = await plugin.ReplExecAsync(sessionId, "console.log(greeting);", timeoutSeconds: 60);

            Assert.Contains("hi-from-node", result);
        }
        finally
        {
            await plugin.ReplStopAsync(sessionId);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    // Pulls the session ID out of ReplStartAsync's "[OK] ... Session ID : <id>\n..." format.
    private static string ExtractSessionId(string startResult)
    {
        const string marker = "Session ID : ";
        var idx = startResult.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"Could not find session ID marker in: {startResult}");
        var afterMarker = startResult[(idx + marker.Length)..];
        var end = afterMarker.IndexOf('\n');
        return (end >= 0 ? afterMarker[..end] : afterMarker).Trim();
    }
}
