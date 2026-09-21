using fuseraft.Core.Models;
using fuseraft.Infrastructure.Memory;

namespace FuseraftCli.Tests;

public sealed class MemoryRankerTests
{
    private static MemoryEntry Entry(string name, string type, string description = "", string body = "") =>
        new() { Name = name, Type = type, Description = description, Body = body };

    private static string[] Names(IEnumerable<MemoryEntry> entries) => [.. entries.Select(e => e.Name)];

    [Fact]
    public void Rank_NoTerms_OrdersByTypePriorityThenName()
    {
        var ranked = MemoryRanker.Rank(
            [Entry("z_ref", "reference"), Entry("b_user", "user"), Entry("a_user", "user"),
             Entry("m_proj", "project"), Entry("f_feed", "feedback")],
            []);

        Assert.Equal(["f_feed", "m_proj", "a_user", "b_user", "z_ref"], Names(ranked));
    }

    [Fact]
    public void Rank_NullTerms_BehavesLikeNoTerms()
    {
        var ranked = MemoryRanker.Rank([Entry("a", "reference"), Entry("b", "feedback")], null);

        Assert.Equal(["b", "a"], Names(ranked));
    }

    [Fact]
    public void Rank_TermMatch_OutranksHigherPriorityType()
    {
        var ranked = MemoryRanker.Rank(
            [Entry("style", "feedback", "prefers terse replies"),
             Entry("db_notes", "reference", "Postgres connection quirks", "pool size is capped")],
            ["postgres", "connection"]);

        Assert.Equal("db_notes", ranked[0].Name);
    }

    [Fact]
    public void Rank_HeaderMatch_OutranksBodyMatch()
    {
        var ranked = MemoryRanker.Rank(
            [Entry("a_body", "project", "unrelated", "mentions retries in passing"),
             Entry("b_header", "project", "retries policy", "unrelated")],
            ["retries"]);

        Assert.Equal(["b_header", "a_body"], Names(ranked));
    }

    [Fact]
    public void Rank_NoOverlap_FallsBackToTypePriority()
    {
        var ranked = MemoryRanker.Rank(
            [Entry("a_ref", "reference", "docs"), Entry("b_feed", "feedback", "style")],
            ["kubernetes"]);

        Assert.Equal(["b_feed", "a_ref"], Names(ranked));
    }

    [Fact]
    public void Rank_TermMatchIsCaseInsensitive()
    {
        var ranked = MemoryRanker.Rank(
            [Entry("a_feed", "feedback"), Entry("b_ref", "reference", "AuthMiddleware refresh quirks")],
            ["AUTHMIDDLEWARE", "Refresh"]);

        Assert.Equal("b_ref", ranked[0].Name);
    }

    [Fact]
    public void Rank_DuplicateTerms_AreCountedOnce()
    {
        // Counted once: reference 1 + header 2 = 3, below the unmatched feedback entry's 4.
        var ranked = MemoryRanker.Rank(
            [Entry("a_ref", "reference", "deploy notes"), Entry("b_feed", "feedback")],
            ["deploy", "Deploy", "DEPLOY"]);

        Assert.Equal("b_feed", ranked[0].Name);
    }

    [Fact]
    public void Rank_WhitespaceOnlyTerms_AreIgnored()
    {
        // Unfiltered, these would each match the run of spaces in the description: reference 1 + 6 = 7 > 4.
        var ranked = MemoryRanker.Rank(
            [Entry("a_ref", "reference", "one    gap"), Entry("b_feed", "feedback")],
            ["  ", "   ", "    "]);

        Assert.Equal("b_feed", ranked[0].Name);
    }

    [Fact]
    public void Rank_DoesNotMutateInputOrder()
    {
        List<MemoryEntry> input = [Entry("a", "reference"), Entry("b", "feedback")];

        MemoryRanker.Rank(input, []);

        Assert.Equal(["a", "b"], Names(input));
    }
}
