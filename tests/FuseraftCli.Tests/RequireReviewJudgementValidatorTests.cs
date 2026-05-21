using System.Text.Json;
using Microsoft.Extensions.AI;
using fuseraft.Orchestration.Validation;

namespace FuseraftCli.Tests;

public sealed class RequireReviewJudgementValidatorTests : IDisposable
{
    private readonly RequireReviewJudgementValidator _validator = new();

    // Brief-aware validator helpers
    private readonly string _briefPath = Path.GetTempFileName();

    public void Dispose()
    {
        if (File.Exists(_briefPath)) File.Delete(_briefPath);
    }

    private RequireReviewJudgementValidator BriefValidator() => new(_briefPath);

    private void WriteBriefWithCriteria(params string[] criteria) =>
        File.WriteAllText(_briefPath, JsonSerializer.Serialize(new
        {
            goal = "test",
            acceptance_criteria = criteria
        }));

    private static ChatMessage UserMsg() =>
        new(ChatRole.User, "[fuseraft: Tester → Reviewer]");

    private static ChatMessage ReviewerMsg(string content) =>
        new(ChatRole.Assistant, content) { AuthorName = "Reviewer" };

    private static (ChatMessage call, ChatMessage result) ShellRunMsg(string callId = "sh1")
    {
        var call = new ChatMessage(ChatRole.Assistant,
            new List<AIContent>
            {
                new FunctionCallContent(callId, "shell_run",
                    new Dictionary<string, object?> { ["command"] = "dotnet build" })
            });
        var result = new ChatMessage(ChatRole.Tool,
            new List<AIContent> { new FunctionResultContent(callId, (object)"Build succeeded. 0 Error(s).") });
        return (call, result);
    }

    // Passing cases

    [Fact]
    public async Task CodeFencedReviewBlock_Passes()
    {
        var (shCall, shResult) = ShellRunMsg();
        var msg = ReviewerMsg(
            "I reviewed the code.\n\n" +
            "```json\n" +
            """{"review":[{"criterion":"Feature works","verdict":"PASS","evidence":"ran the test"}]}""" +
            "\n```\n\nAPPROVED");

        var result = await _validator.ValidateAsync([UserMsg(), shCall, shResult, msg]);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task BareJsonReviewBlock_Passes()
    {
        var (shCall, shResult) = ShellRunMsg();
        var msg = ReviewerMsg(
            """{"review":[{"criterion":"Feature works","verdict":"PASS","evidence":"verified"}]}""" +
            "\n\nAPPROVED");

        var result = await _validator.ValidateAsync([UserMsg(), shCall, shResult, msg]);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task MultipleEntries_Passes()
    {
        var (shCall, shResult) = ShellRunMsg();
        var json = """
            {"review":[
              {"criterion":"Criterion A","verdict":"PASS","evidence":"ran cmd A"},
              {"criterion":"Criterion B","verdict":"PASS","evidence":"ran cmd B"}
            ]}
            """;
        var msg = ReviewerMsg($"```json\n{json}\n```\n\nAPPROVED");

        var result = await _validator.ValidateAsync([UserMsg(), shCall, shResult, msg]);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task PassVerdictWithoutShellRun_Fails()
    {
        var msg = ReviewerMsg(
            "```json\n" +
            """{"review":[{"criterion":"Feature works","verdict":"PASS","evidence":"looks correct"}]}""" +
            "\n```\n\nAPPROVED");

        var result = await _validator.ValidateAsync([UserMsg(), msg]);
        Assert.False(result.IsValid);
        Assert.Contains("shell command", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailOnlyVerdicts_BlocksApproved()
    {
        // This validator is only called when APPROVED is the detected keyword.
        // A FAIL-only review attempting APPROVED must be blocked — the developer needs to fix things.
        var (shCall, shResult) = ShellRunMsg();
        var msg = ReviewerMsg(
            "```json\n" +
            """{"review":[{"criterion":"Feature works","verdict":"FAIL","evidence":"missing implementation"}]}""" +
            "\n```\n\nAPPROVED");

        var result = await _validator.ValidateAsync([UserMsg(), shCall, shResult, msg]);
        Assert.False(result.IsValid);
        Assert.Contains("FAIL verdict", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnyFailVerdict_BlocksApproved()
    {
        var (shCall, shResult) = ShellRunMsg();
        var json = """
            {"review":[
              {"criterion":"Build passes","verdict":"PASS","evidence":"build ok"},
              {"criterion":"Runtime x=include works","verdict":"FAIL","evidence":"FunctionUndefinedError: sqrt"}
            ]}
            """;
        var msg = ReviewerMsg($"```json\n{json}\n```\n\nAPPROVED");

        var result = await _validator.ValidateAsync([UserMsg(), shCall, shResult, msg]);
        Assert.False(result.IsValid);
        Assert.Contains("FAIL verdict", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Runtime x=include works", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllFailVerdicts_BlocksApproved()
    {
        var (shCall, shResult) = ShellRunMsg();
        var msg = ReviewerMsg(
            "```json\n" +
            """{"review":[{"criterion":"Feature works","verdict":"FAIL","evidence":"crashes at runtime"}]}""" +
            "\n```\n\nAPPROVED");

        var result = await _validator.ValidateAsync([UserMsg(), shCall, shResult, msg]);
        Assert.False(result.IsValid);
        Assert.Contains("FAIL verdict", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // Failure cases

    [Fact]
    public async Task NoJsonBlock_Fails()
    {
        var msg = ReviewerMsg("The code looks good. APPROVED");

        var result = await _validator.ValidateAsync([UserMsg(), msg]);
        Assert.False(result.IsValid);
        Assert.Contains("structured review block", result.ErrorMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyHistory_Fails()
    {
        var result = await _validator.ValidateAsync([]);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ReviewArrayEmpty_Fails()
    {
        var msg = ReviewerMsg("```json\n{\"review\":[]}\n```\n\nAPPROVED");

        var result = await _validator.ValidateAsync([UserMsg(), msg]);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task EntryMissingEvidence_Fails()
    {
        var msg = ReviewerMsg(
            "```json\n" +
            """{"review":[{"criterion":"Feature works","verdict":"PASS","evidence":""}]}""" +
            "\n```\n\nAPPROVED");

        var result = await _validator.ValidateAsync([UserMsg(), msg]);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task EntryMissingVerdict_Fails()
    {
        var msg = ReviewerMsg(
            "```json\n" +
            """{"review":[{"criterion":"Feature works","verdict":"","evidence":"verified"}]}""" +
            "\n```\n\nAPPROVED");

        var result = await _validator.ValidateAsync([UserMsg(), msg]);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ReviewBlockBeforeTurnBoundary_Fails()
    {
        // A valid block from the previous turn must not satisfy the current turn.
        var prevReview = ReviewerMsg(
            "```json\n" +
            """{"review":[{"criterion":"Old criterion","verdict":"PASS","evidence":"old evidence"}]}""" +
            "\n```\n\nAPPROVED");

        var history = new List<ChatMessage>
        {
            prevReview,          // previous turn
            UserMsg(),           // boundary
            ReviewerMsg("The code looks fine. APPROVED")  // current turn — no block
        };

        var result = await _validator.ValidateAsync(history);
        Assert.False(result.IsValid);
    }

    // Brief-aware criterion coverage tests

    [Fact]
    public async Task BriefCoverage_ReviewMatchesCriteriaCount_Passes()
    {
        // Brief has 2 criteria; review has 2 entries → passes.
        WriteBriefWithCriteria("Parser accepts x = include", "x.foofunc() resolves");
        var (shCall, shResult) = ShellRunMsg();
        var json = """
            {"review":[
              {"criterion":"Parser accepts x = include","verdict":"PASS","evidence":"ran parser test"},
              {"criterion":"x.foofunc() resolves","verdict":"PASS","evidence":"ran ./bin/kiwi /tmp/ac.kiwi"}
            ]}
            """;
        var msg = ReviewerMsg($"```json\n{json}\n```\n\nAPPROVED");

        var result = await BriefValidator().ValidateAsync([UserMsg(), shCall, shResult, msg]);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task BriefCoverage_ReviewHasMoreEntriesThanCriteria_Passes()
    {
        // Extra review entries are fine — only a deficit blocks.
        WriteBriefWithCriteria("Build passes");
        var (shCall, shResult) = ShellRunMsg();
        var json = """
            {"review":[
              {"criterion":"Build passes","verdict":"PASS","evidence":"build.sh ok"},
              {"criterion":"Runtime check","verdict":"PASS","evidence":"ran kiwi test"}
            ]}
            """;
        var msg = ReviewerMsg($"```json\n{json}\n```\n\nAPPROVED");

        var result = await BriefValidator().ValidateAsync([UserMsg(), shCall, shResult, msg]);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task BriefCoverage_ReviewHasFewerEntriesThanCriteria_Fails()
    {
        // Brief has 5 criteria (as in d441beb8); review has only 1 entry → blocked.
        WriteBriefWithCriteria(
            "Parser accepts include expr in assignment context",
            "Compile emits Include that leaves module ref on stack",
            "VM Include opcode returns populated module object",
            "x.foofunc() resolves on returned object",
            "build.sh succeeds; tests pass");
        var (shCall, shResult) = ShellRunMsg();
        var json = """{"review":[{"criterion":"Build passes","verdict":"PASS","evidence":"build.sh ok"}]}""";
        var msg = ReviewerMsg($"```json\n{json}\n```\n\nAPPROVED");

        var result = await BriefValidator().ValidateAsync([UserMsg(), shCall, shResult, msg]);
        Assert.False(result.IsValid);
        Assert.Contains("5", result.ErrorMessage);   // shows expected count
        Assert.Contains("1", result.ErrorMessage);   // shows actual count
    }

    [Fact]
    public async Task BriefCoverage_NoBriefFile_DoesNotBlock()
    {
        // No brief path → coverage check is skipped; single-entry review is fine.
        var (shCall, shResult) = ShellRunMsg();
        var msg = ReviewerMsg(
            "```json\n" +
            """{"review":[{"criterion":"Feature works","verdict":"PASS","evidence":"ran the test"}]}""" +
            "\n```\n\nAPPROVED");

        // _validator has no brief path (default constructor)
        var result = await _validator.ValidateAsync([UserMsg(), shCall, shResult, msg]);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task BriefCoverage_BriefHasNoCriteria_DoesNotBlock()
    {
        // Brief exists but acceptance_criteria is empty → no coverage requirement.
        File.WriteAllText(_briefPath, """{"goal":"g","files_to_change":[]}""");
        var (shCall, shResult) = ShellRunMsg();
        var msg = ReviewerMsg(
            "```json\n" +
            """{"review":[{"criterion":"Feature works","verdict":"PASS","evidence":"verified"}]}""" +
            "\n```\n\nAPPROVED");

        var result = await BriefValidator().ValidateAsync([UserMsg(), shCall, shResult, msg]);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task BriefCoverage_BriefMissing_DoesNotBlock()
    {
        // Brief path is configured but file does not yet exist → skip coverage check gracefully.
        File.Delete(_briefPath);
        var (shCall, shResult) = ShellRunMsg();
        var msg = ReviewerMsg(
            "```json\n" +
            """{"review":[{"criterion":"Feature works","verdict":"PASS","evidence":"verified"}]}""" +
            "\n```\n\nAPPROVED");

        var result = await BriefValidator().ValidateAsync([UserMsg(), shCall, shResult, msg]);
        Assert.True(result.IsValid);
    }
}
