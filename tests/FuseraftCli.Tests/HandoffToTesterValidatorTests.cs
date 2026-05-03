using Microsoft.Extensions.AI;
using fuseraft.Orchestration.Validation;

namespace FuseraftCli.Tests;

public sealed class HandoffToTesterValidatorTests
{
    // No shell fallback — write_file is the only accepted evidence.
    private readonly HandoffToTesterValidator _validator = new();

    // Shell fallback configured, matching the same pattern used in orchestration.yaml.
    private readonly HandoffToTesterValidator _validatorWithFallback = new("go mod tidy|go get");

    private static ChatMessage UserMsg() => new(ChatRole.User, "task");

    /// <summary>
    /// Returns a paired (assistant call msg, tool result msg) for a generic function invocation.
    /// Both messages are needed so the validator can look up the function name via CallId.
    /// </summary>
    private static (ChatMessage call, ChatMessage result) ToolMsg(
        string functionName, string callId = "tc-default")
    {
        var call = new ChatMessage(ChatRole.Assistant,
            new List<AIContent> { new FunctionCallContent(callId, functionName) });
        var result = new ChatMessage(ChatRole.Tool,
            new List<AIContent> { new FunctionResultContent(callId, (object)"OK") });
        return (call, result);
    }

    // Failure cases

    [Fact]
    public async Task NoHistory_Fails()
    {
        var result = await _validator.ValidateAsync([]);

        Assert.False(result.IsValid);
        Assert.Contains("write_file", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnlyUserMessage_Fails()
    {
        var result = await _validator.ValidateAsync([UserMsg()]);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task OtherFunctionOnly_Fails()
    {
        var (call, res) = ToolMsg("read_file", "c1");

        var result = await _validator.ValidateAsync([UserMsg(), call, res]);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task WriteFileBeforeTurnBoundary_Fails()
    {
        // write_file occurred before the most recent user message — not this turn.
        var (prevCall, prevRes) = ToolMsg("write_file", "c1");
        var (curCall,  curRes)  = ToolMsg("read_file",  "c2");
        var history = new List<ChatMessage>
        {
            prevCall, prevRes,  // previous turn
            UserMsg(),          // turn boundary
            curCall,  curRes    // current turn — no write_file
        };

        var result = await _validator.ValidateAsync(history);

        Assert.False(result.IsValid);
    }

    // Passing cases

    [Fact]
    public async Task WriteFilePresent_Passes()
    {
        var (call, res) = ToolMsg("write_file", "c1");

        var result = await _validator.ValidateAsync([UserMsg(), call, res]);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task WriteFile_PluginPrefixed_Passes()
    {
        var (call, res) = ToolMsg("FileSystem-write_file", "c1");

        var result = await _validator.ValidateAsync([UserMsg(), call, res]);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task WriteFile_CaseInsensitive_Passes()
    {
        var (call, res) = ToolMsg("WRITE_FILE", "c1");

        var result = await _validator.ValidateAsync([UserMsg(), call, res]);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task WriteFileAfterTurnBoundary_Passes()
    {
        // write_file from a previous turn should be ignored; the one after the boundary counts.
        var (prevCall, prevRes) = ToolMsg("shell_run",  "c0");
        var (cur1Call, cur1Res) = ToolMsg("read_file",  "c1");
        var (cur2Call, cur2Res) = ToolMsg("write_file", "c2");
        var history = new List<ChatMessage>
        {
            prevCall, prevRes,          // previous turn
            UserMsg(),                  // boundary
            cur1Call, cur1Res,          // current turn
            cur2Call, cur2Res           // current turn — this one counts
        };

        var result = await _validator.ValidateAsync(history);

        Assert.True(result.IsValid);
    }

    // Dep-management shell-run path

    [Fact]
    public async Task GoModTidy_NoWriteFile_Passes()
    {
        var history = new List<ChatMessage>
        {
            UserMsg(),
            AssistantShellCall("go mod tidy", "call-1"),
            DepShellResult("call-1")
        };

        var result = await _validatorWithFallback.ValidateAsync(history);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task GoGet_NoWriteFile_Passes()
    {
        var history = new List<ChatMessage>
        {
            UserMsg(),
            AssistantShellCall("go get ./...", "call-1"),
            DepShellResult("call-1")
        };

        var result = await _validatorWithFallback.ValidateAsync(history);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task GoModTidy_NoShellFallbackConfigured_Fails()
    {
        // Without a ShellFallbackPattern, dep shell never bypasses the write_file requirement.
        var history = new List<ChatMessage>
        {
            UserMsg(),
            AssistantShellCall("go mod tidy", "call-1"),
            DepShellResult("call-1")
        };

        var result = await _validator.ValidateAsync(history);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task GoModTidy_FailedShell_Fails()
    {
        // Exit 1 output — dep shell ran but failed; should not satisfy the validator.
        var history = new List<ChatMessage>
        {
            UserMsg(),
            AssistantShellCall("go mod tidy", "call-1"),
            DepShellResult("call-1", "[EXIT 1] go: errors loading modules")
        };

        var result = await _validatorWithFallback.ValidateAsync(history);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task GoBuild_OnlyShell_Fails()
    {
        // go build doesn't match the fallback pattern; should not bypass write_file.
        var history = new List<ChatMessage>
        {
            UserMsg(),
            AssistantShellCall("go build ./...", "call-1"),
            DepShellResult("call-1")
        };

        var result = await _validatorWithFallback.ValidateAsync(history);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task GoModTidy_BeforeTurnBoundary_Fails()
    {
        // Dep shell from a previous turn must not count toward the current turn.
        var (readCall, readRes) = ToolMsg("read_file", "c2");
        var history = new List<ChatMessage>
        {
            AssistantShellCall("go mod tidy", "call-0"),
            DepShellResult("call-0"),   // previous turn
            UserMsg(),                  // boundary
            readCall, readRes           // current turn — no write_file or dep shell
        };

        var result = await _validatorWithFallback.ValidateAsync(history);

        Assert.False(result.IsValid);
    }

    // Helpers

    private static ChatMessage AssistantShellCall(string command, string callId)
    {
        return new ChatMessage(ChatRole.Assistant,
            new List<AIContent>
            {
                new FunctionCallContent(callId, "shell_run",
                    new Dictionary<string, object?> { ["command"] = command })
            });
    }

    private static ChatMessage DepShellResult(string callId, string output = "")
    {
        return new ChatMessage(ChatRole.Tool,
            new List<AIContent> { new FunctionResultContent(callId, (object)output) });
    }
}
