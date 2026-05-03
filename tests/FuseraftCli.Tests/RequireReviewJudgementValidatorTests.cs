using Microsoft.Extensions.AI;
using fuseraft.Orchestration.Validation;

namespace FuseraftCli.Tests;

public sealed class RequireReviewJudgementValidatorTests
{
    private readonly RequireReviewJudgementValidator _validator = new();

    private static ChatMessage UserMsg() =>
        new(ChatRole.User, "[fuseraft: Tester → Reviewer]");

    private static ChatMessage ReviewerMsg(string content) =>
        new(ChatRole.Assistant, content) { AuthorName = "Reviewer" };

    // Passing cases

    [Fact]
    public async Task CodeFencedReviewBlock_Passes()
    {
        var msg = ReviewerMsg(
            "I reviewed the code.\n\n" +
            "```json\n" +
            """{"review":[{"criterion":"Feature works","verdict":"PASS","evidence":"ran the test"}]}""" +
            "\n```\n\nAPPROVED");

        var result = await _validator.ValidateAsync([UserMsg(), msg]);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task BareJsonReviewBlock_Passes()
    {
        var msg = ReviewerMsg(
            """{"review":[{"criterion":"Feature works","verdict":"PASS","evidence":"verified"}]}""" +
            "\n\nAPPROVED");

        var result = await _validator.ValidateAsync([UserMsg(), msg]);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task MultipleEntries_Passes()
    {
        var json = """
            {"review":[
              {"criterion":"Criterion A","verdict":"PASS","evidence":"ran cmd A"},
              {"criterion":"Criterion B","verdict":"PASS","evidence":"ran cmd B"}
            ]}
            """;
        var msg = ReviewerMsg($"```json\n{json}\n```\n\nAPPROVED");

        var result = await _validator.ValidateAsync([UserMsg(), msg]);
        Assert.True(result.IsValid);
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
}
