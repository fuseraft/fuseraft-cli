using fuseraft.Cli;
using fuseraft.Cli.Commands.Eval;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Orchestration;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for the eval scoring, suite loading, and filtering logic.
/// These exercise the internal static helpers on EvalCommand directly,
/// without spinning up an orchestrator or hitting any LLM API.
/// </summary>
public sealed class EvalCommandTests
{
    // ── Score — must_succeed ──────────────────────────────────────────────────

    [Fact]
    public void Score_MustSucceed_PassesWhenSessionSucceeded()
    {
        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", MustSucceed = true },
            MakeResult(succeeded: true, "Great answer"),
            "sid1");

        Assert.True(result.Passed);
        Assert.Empty(result.FailureReasons);
    }

    [Fact]
    public void Score_MustSucceed_FailsWhenSessionFailed()
    {
        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", MustSucceed = true },
            MakeResult(succeeded: false, "Great answer", errorMessage: "LLM error"),
            "sid1");

        Assert.False(result.Passed);
        Assert.Single(result.FailureReasons);
        Assert.Contains("LLM error", result.FailureReasons[0]);
    }

    [Fact]
    public void Score_MustSucceedFalse_DoesNotFailOnSessionFailure()
    {
        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", MustSucceed = false },
            MakeResult(succeeded: false, "anything"),
            "sid1");

        Assert.True(result.Passed);
    }

    // ── Score — expect_keywords ───────────────────────────────────────────────

    [Fact]
    public void Score_ExpectKeyword_PassesWhenPresent()
    {
        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", ExpectKeywords = ["hello"] },
            MakeResult(succeeded: true, "Hello, world!"),
            "sid1");

        Assert.True(result.Passed);
    }

    [Fact]
    public void Score_ExpectKeyword_IsCaseInsensitive()
    {
        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", ExpectKeywords = ["HELLO"] },
            MakeResult(succeeded: true, "hello world"),
            "sid1");

        Assert.True(result.Passed);
    }

    [Fact]
    public void Score_ExpectKeyword_FailsWhenMissing()
    {
        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", ExpectKeywords = ["missing"] },
            MakeResult(succeeded: true, "this content has nothing"),
            "sid1");

        Assert.False(result.Passed);
        Assert.Contains("\"missing\"", result.FailureReasons[0]);
    }

    [Fact]
    public void Score_ExpectKeywords_AllMustBePresent()
    {
        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", ExpectKeywords = ["def", "return", "missing"] },
            MakeResult(succeeded: true, "def foo(): return 42"),
            "sid1");

        Assert.False(result.Passed);
        Assert.Single(result.FailureReasons);
        Assert.Contains("\"missing\"", result.FailureReasons[0]);
    }

    // ── Score — expect_regex ──────────────────────────────────────────────────

    [Fact]
    public void Score_ExpectRegex_PassesWhenMatches()
    {
        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", ExpectRegex = [@"def \w+\("] },
            MakeResult(succeeded: true, "def reverse_string(s):"),
            "sid1");

        Assert.True(result.Passed);
    }

    [Fact]
    public void Score_ExpectRegex_FailsWhenNoMatch()
    {
        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", ExpectRegex = [@"def \w+\("] },
            MakeResult(succeeded: true, "Here is a function that reverses a string."),
            "sid1");

        Assert.False(result.Passed);
        Assert.Contains("regex not matched", result.FailureReasons[0]);
    }

    [Fact]
    public void Score_ExpectRegex_InvalidPatternRecordedAsFailure()
    {
        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", ExpectRegex = ["[invalid("] },
            MakeResult(succeeded: true, "anything"),
            "sid1");

        Assert.False(result.Passed);
        Assert.Contains("invalid regex pattern", result.FailureReasons[0]);
    }

    // ── Score — handoff-only turns (empty Content, keyword in tool-call args) ──

    [Fact]
    public void Score_ExpectRegex_MatchesHandoffKeywordWhenContentEmpty()
    {
        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", ExpectRegex = [@"\bAPPROVED\b"] },
            MakeHandoffOnlyResult("APPROVED"),
            "sid1");

        Assert.True(result.Passed);
    }

    [Fact]
    public void Score_ExpectKeyword_MatchesHandoffKeywordWhenContentEmpty()
    {
        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", ExpectKeywords = ["APPROVED"] },
            MakeHandoffOnlyResult("APPROVED"),
            "sid1");

        Assert.True(result.Passed);
    }

    [Fact]
    public void Score_IgnoresNonHandoffToolCalls()
    {
        var messages = new List<AgentMessage>
        {
            new()
            {
                AgentName = "Agent",
                Content   = string.Empty,
                Role      = "assistant",
                ToolCalls = [new ToolCallRecord("shell_run", "command=ls", true)],
            },
        };
        var sessionResult = new SessionResult(true, null, messages, TimeSpan.FromMilliseconds(500));

        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", ExpectRegex = [@"\bAPPROVED\b"] },
            sessionResult,
            "sid1");

        Assert.False(result.Passed);
    }

    // ── Score — termination-agent scoping ─────────────────────────────────────
    // Regression coverage for: a periodic/auxiliary agent (e.g. a Verifier) speaking
    // *after* the approving agent's turn must not shadow that agent's actual approval,
    // matching RegexTerminationCondition's own agent-filtered backward scan.

    [Fact]
    public void Score_TerminationAgentScoped_FindsApprovalBehindLaterUnrelatedAgent()
    {
        var messages = new List<AgentMessage>
        {
            new() { AgentName = "Reviewer", Content = "Looks good. APPROVED", Role = "assistant" },
            new() { AgentName = "Verifier", Content = "Evidence verified — no inconsistencies found.", Role = "assistant" },
        };
        var sessionResult = new SessionResult(true, null, messages, TimeSpan.FromMilliseconds(500));
        var termination = new TerminationStrategyConfig
        {
            Type       = "composite",
            Strategies =
            [
                new TerminationStrategyConfig { Type = "regex", Pattern = @"\bAPPROVED\b", AgentNames = ["Reviewer"] },
                new TerminationStrategyConfig { Type = "maxiterations", MaxIterations = 60 },
            ],
        };

        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", ExpectRegex = [@"\bAPPROVED\b"] },
            sessionResult,
            "sid1",
            termination);

        Assert.True(result.Passed);
    }

    [Fact]
    public void Score_NoTerminationConfig_FallsBackToLastAssistantMessage()
    {
        var messages = new List<AgentMessage>
        {
            new() { AgentName = "Reviewer", Content = "Looks good. APPROVED", Role = "assistant" },
            new() { AgentName = "Verifier", Content = "Evidence verified — no inconsistencies found.", Role = "assistant" },
        };
        var sessionResult = new SessionResult(true, null, messages, TimeSpan.FromMilliseconds(500));

        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", ExpectRegex = [@"\bAPPROVED\b"] },
            sessionResult,
            "sid1");

        Assert.False(result.Passed);
    }

    // ── Score — forbidden_keywords ────────────────────────────────────────────

    [Fact]
    public void Score_ForbiddenKeyword_PassesWhenAbsent()
    {
        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", ForbiddenKeywords = ["I cannot"] },
            MakeResult(succeeded: true, "Sure, here are three benefits."),
            "sid1");

        Assert.True(result.Passed);
    }

    [Fact]
    public void Score_ForbiddenKeyword_FailsWhenPresent()
    {
        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", ForbiddenKeywords = ["I cannot"] },
            MakeResult(succeeded: true, "I cannot help with that."),
            "sid1");

        Assert.False(result.Passed);
        Assert.Contains("\"I cannot\"", result.FailureReasons[0]);
    }

    [Fact]
    public void Score_ForbiddenKeyword_IsCaseInsensitive()
    {
        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", ForbiddenKeywords = ["i cannot"] },
            MakeResult(succeeded: true, "I CANNOT do that."),
            "sid1");

        Assert.False(result.Passed);
    }

    // ── Score — max_turns ─────────────────────────────────────────────────────

    [Fact]
    public void Score_MaxTurns_PassesWhenWithinLimit()
    {
        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", MaxTurns = 3 },
            MakeResult(succeeded: true, "answer", turnCount: 3),
            "sid1");

        Assert.True(result.Passed);
    }

    [Fact]
    public void Score_MaxTurns_FailsWhenExceeded()
    {
        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", MaxTurns = 2 },
            MakeResult(succeeded: true, "answer", turnCount: 5),
            "sid1");

        Assert.False(result.Passed);
        Assert.Contains("exceeded max_turns: 5 > 2", result.FailureReasons[0]);
    }

    [Fact]
    public void Score_MaxTurnsZero_NeverFails()
    {
        var result = EvalCommand.Score(
            new EvalCase { Id = "t1", MaxTurns = 0 },
            MakeResult(succeeded: true, "answer", turnCount: 100),
            "sid1");

        Assert.True(result.Passed);
    }

    // ── Score — aggregates ────────────────────────────────────────────────────

    [Fact]
    public void Score_AggregatesTokensAndDuration()
    {
        var messages = new List<AgentMessage>
        {
            new() { AgentName = "A", Content = "hi",    Role = "assistant", Usage = new TokenUsage(100, 50) },
            new() { AgentName = "B", Content = "hello", Role = "assistant", Usage = new TokenUsage(200, 80) },
        };
        var sessionResult = new SessionResult(
            Succeeded: true, ErrorMessage: null, Messages: messages, Elapsed: TimeSpan.FromMilliseconds(1234));

        var result = EvalCommand.Score(new EvalCase { Id = "t1" }, sessionResult, "sid1");

        Assert.Equal(300, result.TotalInputTokens);
        Assert.Equal(130, result.TotalOutputTokens);
        Assert.Equal(1234, result.DurationMs);
        Assert.Equal(2, result.TotalTurns);
    }

    // ── LoadSuite — YAML ──────────────────────────────────────────────────────

    [Fact]
    public void LoadSuite_Yaml_ParsesNameAndCases()
    {
        var yaml = """
            name: My Suite
            config: .fuseraft/config/orchestration.yaml
            cases:
              - id: case-1
                task: "Say hello"
                must_succeed: true
                expect_keywords:
                  - hello
                max_turns: 3
                tags:
                  - smoke
              - id: case-2
                task: "Write code"
                forbidden_keywords:
                  - "I cannot"
            """;

        var path = WriteTempFile(yaml, ".yaml");
        var suite = EvalCommand.LoadSuite(path);

        Assert.Equal("My Suite", suite.Name);
        Assert.Equal(".fuseraft/config/orchestration.yaml", suite.Config);
        Assert.Equal(2, suite.Cases.Count);

        var c1 = suite.Cases[0];
        Assert.Equal("case-1", c1.Id);
        Assert.Equal("Say hello", c1.Task);
        Assert.True(c1.MustSucceed);
        Assert.Equal(["hello"], c1.ExpectKeywords);
        Assert.Equal(3, c1.MaxTurns);
        Assert.Equal(["smoke"], c1.Tags);

        var c2 = suite.Cases[1];
        Assert.Equal("case-2", c2.Id);
        Assert.Equal(["I cannot"], c2.ForbiddenKeywords);
    }

    [Fact]
    public void LoadSuite_Yaml_EmptyFileThrows()
    {
        var path = WriteTempFile("", ".yaml");
        Assert.Throws<InvalidDataException>(() => EvalCommand.LoadSuite(path));
    }

    // ── LoadSuite — JSON ──────────────────────────────────────────────────────

    [Fact]
    public void LoadSuite_Json_ParsesNameAndCases()
    {
        var json = """
            {
              "name": "JSON Suite",
              "cases": [
                { "id": "j1", "task": "Do something", "mustSucceed": true }
              ]
            }
            """;

        var path = WriteTempFile(json, ".json");
        var suite = EvalCommand.LoadSuite(path);

        Assert.Equal("JSON Suite", suite.Name);
        Assert.Single(suite.Cases);
        Assert.Equal("j1", suite.Cases[0].Id);
        Assert.True(suite.Cases[0].MustSucceed);
    }

    // ── ApplyFilter ───────────────────────────────────────────────────────────

    [Fact]
    public void ApplyFilter_NullFilter_ReturnsAll()
    {
        var cases = MakeCases("a", "b", "c");
        var result = EvalCommand.ApplyFilter(cases, null);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void ApplyFilter_EmptyFilter_ReturnsAll()
    {
        var cases = MakeCases("a", "b", "c");
        var result = EvalCommand.ApplyFilter(cases, "   ");
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void ApplyFilter_ById_Substring()
    {
        var cases = MakeCases("smoke-basic", "code-gen", "smoke-advanced");
        var result = EvalCommand.ApplyFilter(cases, "smoke");
        Assert.Equal(2, result.Count);
        Assert.All(result, c => Assert.Contains("smoke", c.Id));
    }

    [Fact]
    public void ApplyFilter_ById_CaseInsensitive()
    {
        var cases = MakeCases("SmokeTest", "other");
        var result = EvalCommand.ApplyFilter(cases, "SMOKE");
        Assert.Single(result);
    }

    [Fact]
    public void ApplyFilter_ByTag_Matches()
    {
        var cases = new List<EvalCase>
        {
            new() { Id = "a", Tags = ["smoke", "fast"] },
            new() { Id = "b", Tags = ["coding"] },
            new() { Id = "c", Tags = ["smoke"] },
        };
        var result = EvalCommand.ApplyFilter(cases, "smoke");
        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, c => c.Id == "b");
    }

    [Fact]
    public void ApplyFilter_ByTag_CaseInsensitive()
    {
        var cases = new List<EvalCase>
        {
            new() { Id = "a", Tags = ["Coding"] },
            new() { Id = "b", Tags = ["other"] },
        };
        var result = EvalCommand.ApplyFilter(cases, "CODING");
        Assert.Single(result);
    }

    [Fact]
    public void ApplyFilter_NoMatch_ReturnsEmpty()
    {
        var cases = MakeCases("foo", "bar");
        var result = EvalCommand.ApplyFilter(cases, "xyz");
        Assert.Empty(result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SessionResult MakeResult(
        bool succeeded,
        string? assistantContent,
        string? errorMessage = null,
        int turnCount = 1)
    {
        var messages = new List<AgentMessage>();
        for (var i = 0; i < turnCount; i++)
        {
            messages.Add(new AgentMessage
            {
                AgentName = "Agent",
                Content   = i == turnCount - 1 ? (assistantContent ?? string.Empty) : "intermediate",
                Role      = "assistant",
            });
        }

        return new SessionResult(succeeded, errorMessage, messages, TimeSpan.FromMilliseconds(500));
    }

    private static SessionResult MakeHandoffOnlyResult(string routeKeyword)
    {
        var messages = new List<AgentMessage>
        {
            new()
            {
                AgentName = "Reviewer",
                Content   = string.Empty,
                Role      = "assistant",
                ToolCalls = [new ToolCallRecord("handoff", $"route_keyword={routeKeyword}", true)],
            },
        };
        return new SessionResult(true, null, messages, TimeSpan.FromMilliseconds(500));
    }

    private static List<EvalCase> MakeCases(params string[] ids) =>
        ids.Select(id => new EvalCase { Id = id }).ToList();

    private static string WriteTempFile(string content, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"eval_test_{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, content);
        return path;
    }
}
