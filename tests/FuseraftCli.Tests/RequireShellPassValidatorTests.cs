using System.Text.Json;
using Microsoft.Extensions.AI;
using fuseraft.Core.Models;
using fuseraft.Orchestration.Validation;

namespace FuseraftCli.Tests;

public sealed class RequireShellPassValidatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fuseraft_shellpass_{Guid.NewGuid():N}");
    private string ChangesPath => Path.Combine(_dir, "changes.json");

    public RequireShellPassValidatorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private RequireShellPassValidator WithLog(string? pattern = null)
        => new(pattern, ChangesPath);

    private async Task WriteLog(
        string sessionId,
        params (string command, bool succeeded)[] commands)
    {
        var log = new ChangeLog
        {
            ActiveSessionId = sessionId,
            Entries =
            [
                new ChangeEntry
                {
                    Agent       = "Developer",
                    TurnIndex   = 0,
                    Timestamp   = DateTime.UtcNow,
                    SessionId   = sessionId,
                    CommandsRun = commands.Select(c =>
                        new CommandRecord { Command = c.command, Succeeded = c.succeeded }).ToList()
                }
            ]
        };
        await File.WriteAllTextAsync(ChangesPath, JsonSerializer.Serialize(log));
    }

    private static ChatMessage UserMsg() => new(ChatRole.User, "task");

    private static ChatMessage ShellResultMsg(string output, string callId = "c1")
    {
        return new ChatMessage(ChatRole.Tool,
            new List<AIContent> { new FunctionResultContent(callId, (object)output) });
    }

    private static ChatMessage ShellCallMsg(string command, string id = "c1")
    {
        return new ChatMessage(ChatRole.Assistant,
            new List<AIContent> { new FunctionCallContent(id, "shell_run",
                new Dictionary<string, object?> { ["command"] = command }) });
    }

    // No-pattern tests

    [Fact]
    public async Task NoHistory_Fails()
    {
        var result = await new RequireShellPassValidator().ValidateAsync([]);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task OnlyUserMessage_Fails()
    {
        var result = await new RequireShellPassValidator().ValidateAsync([UserMsg()]);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task NonShellToolResult_Fails()
    {
        // A write_file result with no matching call — function name can't be resolved as shell_run.
        var msg = new ChatMessage(ChatRole.Tool,
            new List<AIContent> { new FunctionResultContent("c_write", (object)"OK") });

        var result = await new RequireShellPassValidator().ValidateAsync([UserMsg(), msg]);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ShellRunBeforeTurnBoundary_Fails()
    {
        var history = new List<ChatMessage>
        {
            ShellCallMsg("run", "c0"),
            ShellResultMsg("ok", "c0"),  // previous turn
            UserMsg(),                   // boundary
            // no shell run in current turn
        };

        var result = await new RequireShellPassValidator().ValidateAsync(history);

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("[EXIT 1] build failed")]
    [InlineData("[EXIT 0]\n")]  // edge: [EXIT with any suffix including 0 is treated as failure
    [InlineData("[ERROR] command not found")]
    [InlineData("[TIMEOUT] process killed after 30s")]
    [InlineData("[DENIED] sandbox blocked path")]
    public async Task ErrorPrefixes_Fail(string output)
    {
        // Add a call message so function name can be resolved
        var history = new List<ChatMessage>
        {
            UserMsg(),
            ShellCallMsg("run", "c1"),
            ShellResultMsg(output, "c1")
        };

        var result = await new RequireShellPassValidator().ValidateAsync(history);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ShellRunSuccess_Passes()
    {
        var history = new List<ChatMessage>
        {
            UserMsg(),
            ShellCallMsg("run", "c1"),
            ShellResultMsg("all tests passed\nok  ./...  0.42s", "c1")
        };

        var result = await new RequireShellPassValidator().ValidateAsync(history);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ShellRun_EmptyOutput_Passes()
    {
        // Empty output is not an error prefix — still counts as a pass.
        var history = new List<ChatMessage>
        {
            UserMsg(),
            ShellCallMsg("run", "c1"),
            ShellResultMsg(string.Empty, "c1")
        };

        var result = await new RequireShellPassValidator().ValidateAsync(history);

        Assert.True(result.IsValid);
    }

    // Pattern tests

    [Fact]
    public async Task WithPattern_MatchingCommand_Passes()
    {
        var history = new List<ChatMessage>
        {
            UserMsg(),
            ShellCallMsg("go test ./...", "c1"),
            ShellResultMsg("ok", "c1")
        };

        var result = await new RequireShellPassValidator("go build|go test").ValidateAsync(history);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task WithPattern_FirstAlternativeMatches_Passes()
    {
        var history = new List<ChatMessage>
        {
            UserMsg(),
            ShellCallMsg("go build ./...", "c1"),
            ShellResultMsg("ok", "c1")
        };

        var result = await new RequireShellPassValidator("go build|go test").ValidateAsync(history);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task WithPattern_SecondAlternativeMatches_Passes()
    {
        var history = new List<ChatMessage>
        {
            UserMsg(),
            ShellCallMsg("cargo build", "c1"),
            ShellResultMsg("ok", "c1")
        };

        var result = await new RequireShellPassValidator("cargo test|cargo build").ValidateAsync(history);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task WithPattern_PatternMatchingCaseInsensitive_Passes()
    {
        var history = new List<ChatMessage>
        {
            UserMsg(),
            ShellCallMsg("GO TEST ./...", "c1"),
            ShellResultMsg("ok", "c1")
        };

        var result = await new RequireShellPassValidator("go test").ValidateAsync(history);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task WithPattern_NonMatchingCommand_Fails()
    {
        // go mod tidy exits 0 but does not match "go build|go test"
        var history = new List<ChatMessage>
        {
            UserMsg(),
            ShellCallMsg("go mod tidy", "c1"),
            ShellResultMsg("ok", "c1")
        };

        var result = await new RequireShellPassValidator("go build|go test").ValidateAsync(history);

        Assert.False(result.IsValid);
        Assert.Contains("go build|go test", result.ErrorMessage);
    }

    [Fact]
    public async Task WithPattern_NoCallContent_CommandUnresolvable_Fails()
    {
        // Result message present but no FunctionCallContent to extract the command from.
        var history = new List<ChatMessage>
        {
            UserMsg(),
            ShellResultMsg("ok", "c1")  // no matching FunctionCallContent
        };

        var result = await new RequireShellPassValidator("go test").ValidateAsync(history);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task WithPattern_SuccessButWrongCallId_Fails()
    {
        // Result and call IDs don't match — command cannot be resolved.
        var history = new List<ChatMessage>
        {
            UserMsg(),
            ShellCallMsg("go test ./...", "call-A"),
            ShellResultMsg("ok", "call-B")  // different ID
        };

        var result = await new RequireShellPassValidator("go test").ValidateAsync(history);

        Assert.False(result.IsValid);
    }

    // -------------------------------------------------------------------------
    // ChangeLog primary-source tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ChangeLog_SuccessfulCommand_Passes()
    {
        await WriteLog("s1", ("go test ./...", true));

        var result = await WithLog().ValidateAsync([]);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ChangeLog_OnlyFailedCommands_Fails()
    {
        await WriteLog("s1", ("go test ./...", false));

        var result = await WithLog().ValidateAsync([]);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ChangeLog_MixedSuccess_Passes()
    {
        // At least one succeeded — should pass.
        await WriteLog("s1", ("go build ./...", false), ("go test ./...", true));

        var result = await WithLog().ValidateAsync([]);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ChangeLog_NoEntries_Fails()
    {
        var log = new ChangeLog { ActiveSessionId = "s1", Entries = [] };
        await File.WriteAllTextAsync(ChangesPath, JsonSerializer.Serialize(log));

        var result = await WithLog().ValidateAsync([]);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ChangeLog_SessionFilter_OnlyCurrentSession_Passes()
    {
        // The current session has a success; a stale prior session should not matter.
        var log = new ChangeLog
        {
            ActiveSessionId = "current",
            Entries =
            [
                new ChangeEntry
                {
                    SessionId   = "old",
                    Agent       = "Developer",
                    TurnIndex   = 0,
                    Timestamp   = DateTime.UtcNow.AddHours(-1),
                    CommandsRun = [new CommandRecord { Command = "go test ./...", Succeeded = false }]
                },
                new ChangeEntry
                {
                    SessionId   = "current",
                    Agent       = "Developer",
                    TurnIndex   = 1,
                    Timestamp   = DateTime.UtcNow,
                    CommandsRun = [new CommandRecord { Command = "go test ./...", Succeeded = true }]
                }
            ]
        };
        await File.WriteAllTextAsync(ChangesPath, JsonSerializer.Serialize(log));

        var result = await WithLog().ValidateAsync([]);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ChangeLog_SessionFilter_PriorSessionOnly_Fails()
    {
        // Only prior-session commands in the log; current session has nothing.
        var log = new ChangeLog
        {
            ActiveSessionId = "current",
            Entries =
            [
                new ChangeEntry
                {
                    SessionId   = "old",
                    Agent       = "Developer",
                    TurnIndex   = 0,
                    Timestamp   = DateTime.UtcNow.AddHours(-1),
                    CommandsRun = [new CommandRecord { Command = "go test ./...", Succeeded = true }]
                }
            ]
        };
        await File.WriteAllTextAsync(ChangesPath, JsonSerializer.Serialize(log));

        var result = await WithLog().ValidateAsync([]);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ChangeLog_WithPattern_MatchingCommand_Passes()
    {
        await WriteLog("s1", ("go test ./...", true));

        var result = await WithLog("go build|go test").ValidateAsync([]);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ChangeLog_WithPattern_NonMatchingCommand_Fails()
    {
        await WriteLog("s1", ("go mod tidy", true));

        var result = await WithLog("go build|go test").ValidateAsync([]);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ChangeLog_TakesPrecedence_HistoryAloneWouldFail_Passes()
    {
        // Log has a successful command; history has nothing. The log wins.
        await WriteLog("s1", ("go test ./...", true));

        var result = await WithLog().ValidateAsync([UserMsg()]);  // empty history

        Assert.True(result.IsValid);
    }

    // -------------------------------------------------------------------------
    // requireCurrentTurn tests (used by termination validators)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RequireCurrentTurn_BoundaryFoundWithChangeLog_Fails()
    {
        // Change log has a successful command from an earlier turn, but a user boundary
        // was reached before any shell pass in the current turn's history.
        // requireCurrentTurn=true must block the stale log from satisfying the check.
        await WriteLog("s1", ("go test ./...", true));

        var validator = new RequireShellPassValidator("go test", ChangesPath, requireCurrentTurn: true);
        var result = await validator.ValidateAsync([UserMsg()]);  // user boundary, no current-turn shell

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task RequireCurrentTurn_ShellInCurrentTurn_Passes()
    {
        // requireCurrentTurn=true still passes when the current turn contains a shell pass.
        var history = new List<ChatMessage>
        {
            UserMsg(),
            ShellCallMsg("go test ./...", "c1"),
            ShellResultMsg("ok", "c1")
        };

        var validator = new RequireShellPassValidator("go test", ChangesPath, requireCurrentTurn: true);
        var result = await validator.ValidateAsync(history);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ChangeLog_FileMissing_FallsBackToHistory_Passes()
    {
        // Log path is configured but the file doesn't exist yet — should fall through
        // to history scanning, which finds a successful shell result.
        var history = new List<ChatMessage>
        {
            UserMsg(),
            ShellCallMsg("go test ./...", "c1"),
            ShellResultMsg("ok", "c1")
        };

        // WithLog() points to ChangesPath which doesn't exist — no WriteLog call.
        var result = await WithLog().ValidateAsync(history);

        Assert.True(result.IsValid);
    }
}
