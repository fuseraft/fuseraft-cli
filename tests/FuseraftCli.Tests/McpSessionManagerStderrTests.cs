using fuseraft.Core.Models;
using fuseraft.Infrastructure.Mcp;

namespace FuseraftCli.Tests;

/// <summary>
/// A stdio server's stderr is its log, and its errors point there ("the server log lists why"), so the lines it
/// writes are kept for /mcp log. The child here isn't an MCP server, so the connect fails; its stderr is still kept.
/// </summary>
public sealed class McpSessionManagerStderrTests
{
    private static McpServerConfig Server(string script, Dictionary<string, string>? env = null) => OperatingSystem.IsWindows()
        ? new() { Name = "noisy", Command = "cmd.exe", Args = ["/c", script], Env = env ?? [] }
        : new() { Name = "noisy", Command = "sh", Args = ["-c", script], Env = env ?? [] };

    private static async Task<IReadOnlyList<string>?> ConnectAndReadStderr(McpServerConfig server)
    {
        await using var manager = new McpSessionManager();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await manager.ConnectSingleAsync(server, cts.Token); }
        catch { /* not an MCP server */ }
        // The lines arrive on the process's own reader thread.
        for (var i = 0; i < 50 && manager.StderrLines("NOISY") is not [_, ..]; i++)
            await Task.Delay(100);
        return manager.StderrLines("NOISY");
    }

    [Fact]
    public async Task WhatAStdioServerWritesToStderr_IsKept()
    {
        var lines = await ConnectAndReadStderr(Server(OperatingSystem.IsWindows() ? "echo login can write 1>&2" : "echo 'login can write' >&2"));

        Assert.Contains(lines!, l => l.Contains("login can write"));
    }

    [Fact]
    public async Task EnvValues_ExpandEnvTokens()
    {
        Environment.SetEnvironmentVariable("FUSERAFT_TEST_MCP_SECRET", "s3cret");
        try
        {
            var lines = await ConnectAndReadStderr(Server(
                OperatingSystem.IsWindows() ? "echo pw=%DB_PW% 1>&2" : "echo \"pw=$DB_PW\" >&2",
                new() { ["DB_PW"] = "${FUSERAFT_TEST_MCP_SECRET}" }));

            Assert.Contains(lines!, l => l.Contains("pw=s3cret"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FUSERAFT_TEST_MCP_SECRET", null);
        }
    }

    [Fact]
    public async Task AServerNeverConnected_HasNoLog()
    {
        await using var manager = new McpSessionManager();

        Assert.Null(manager.StderrLines("missing"));
    }
}
