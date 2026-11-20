using ReplPrompt = fuseraft.Cli.Commands.Repl.SystemPromptBuilder;

namespace FuseraftCli.Tests;

/// <summary>
/// A provider's prompt cache reuses a prompt only up to its first difference, so the REPL prompt keeps what changes per
/// session (the session ID, start time, and date/time) out of the environment block and in the session block it ends with.
/// </summary>
public sealed class ReplSystemPromptCacheOrderTests
{
    private static string Prompt(string sessionId, DateTime startedAt) => new ReplPrompt()
        .AddIdentity("test-model", toolCount: 10)
        .AddToolGuidance(toolCount: 10)
        .AddOsEnvironment()
        .AddSessionInfo(sessionId, startedAt, toolCount: 0)
        .Build();

    [Fact]
    public void EnvironmentBlock_LeavesOutTheDateTime()
    {
        var environment = new ReplPrompt().AddOsEnvironment().Build();

        Assert.Contains("Working directory:", environment);
        Assert.DoesNotContain("Date/time:", environment);
    }

    [Fact]
    public void SessionBlock_CarriesTheDateTime()
    {
        var session = new ReplPrompt().AddSessionInfo("abc12345", DateTime.UtcNow, toolCount: 0).Build();

        Assert.Contains("Session ID: abc12345", session);
        Assert.Contains("Date/time:", session);
    }

    [Fact]
    public void WithoutASession_TheDateTimeIsStillStated() =>
        Assert.Contains("Date/time:", new ReplPrompt().AddSessionInfo(null, null, toolCount: 0).Build());

    [Fact]
    public void TwoSessions_ShareEverythingBeforeTheSessionBlock()
    {
        var first  = Prompt("aaaa1111", new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc));
        var second = Prompt("bbbb2222", new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc));

        var shared = first.IndexOf("# Current session", StringComparison.Ordinal);
        Assert.True(shared > 0);
        Assert.Equal(first[..shared], second[..shared]);
    }
}
