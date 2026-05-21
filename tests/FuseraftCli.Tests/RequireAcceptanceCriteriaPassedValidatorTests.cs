using System.Text.Json;
using Microsoft.Extensions.AI;
using fuseraft.Core.Models;
using fuseraft.Orchestration.Validation;

namespace FuseraftCli.Tests;

public sealed class RequireAcceptanceCriteriaPassedValidatorTests : IDisposable
{
    private readonly string _briefPath  = Path.GetTempFileName();
    private readonly string _logPath    = Path.GetTempFileName();

    public void Dispose()
    {
        if (File.Exists(_briefPath)) File.Delete(_briefPath);
        if (File.Exists(_logPath))   File.Delete(_logPath);
    }

    // -- Helpers ------------------------------------------------------------------

    private RequireAcceptanceCriteriaPassedValidator Validator() =>
        new(_briefPath, _logPath);

    private void WriteBrief(object brief) =>
        File.WriteAllText(_briefPath, JsonSerializer.Serialize(brief));

    private void WriteLog(string sessionId, params (string cmd, bool ok, string output)[] commands)
    {
        var entries = new[]
        {
            new
            {
                Agent      = "Developer",
                TurnIndex  = 0,
                Timestamp  = DateTime.UtcNow,
                SessionId  = sessionId,
                FilesWritten = Array.Empty<string>(),
                FilesDeleted = Array.Empty<string>(),
                CommandsRun  = commands.Select(c => new { Command = c.cmd, succeeded = c.ok, Output = c.output }).ToArray(),
                GitCommits   = Array.Empty<string>()
            }
        };
        var log = new { ActiveSessionId = sessionId, Entries = entries };
        File.WriteAllText(_logPath, JsonSerializer.Serialize(log));
    }

    private static IList<ChatMessage> EmptyHistory() => [new ChatMessage(ChatRole.User, "go")];

    // -- Plain string criteria: no machine testing --------------------------------

    [Fact]
    public async Task PlainStringCriteria_AlwaysPasses()
    {
        WriteBrief(new
        {
            acceptance_criteria = new[] { "Parser accepts x = include", "Tests pass" }
        });
        // No commands in log — still passes because plain strings are not machine-testable.
        WriteLog("s1");

        var result = await Validator().ValidateAsync(EmptyHistory());
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task NoCriteria_Passes()
    {
        WriteBrief(new { goal = "do something" });
        var result = await Validator().ValidateAsync(EmptyHistory());
        Assert.True(result.IsValid);
    }

    // -- Object criteria without expected_output_contains: skipped ----------------

    [Fact]
    public async Task CriterionWithoutExpectedOutput_Skipped()
    {
        WriteBrief(new
        {
            acceptance_criteria = new object[]
            {
                new { criterion = "Build passes", test_command = "./build.sh" }
                // no expected_output_contains → not machine-testable
            }
        });
        WriteLog("s1"); // no commands

        var result = await Validator().ValidateAsync(EmptyHistory());
        Assert.True(result.IsValid);
    }

    // -- Sentinel found in session output: passes ---------------------------------

    [Fact]
    public async Task SentinelFoundInOutput_Passes()
    {
        WriteBrief(new
        {
            acceptance_criteria = new object[]
            {
                new
                {
                    criterion               = "Runtime: x = include works",
                    test_command            = "./bin/kiwi /tmp/ac.kiwi && echo CRITERION_RUNTIME_PASS",
                    expected_output_contains = "CRITERION_RUNTIME_PASS"
                }
            }
        });
        WriteLog("s1",
            ("./bin/kiwi /tmp/ac.kiwi && echo CRITERION_RUNTIME_PASS", true,
             "4.0\nCRITERION_RUNTIME_PASS"));

        var result = await Validator().ValidateAsync(EmptyHistory());
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task AllCriteriaSatisfied_Passes()
    {
        WriteBrief(new
        {
            acceptance_criteria = new object[]
            {
                new { criterion = "C1", test_command = "cmd1", expected_output_contains = "SENTINEL_C1" },
                new { criterion = "C2", test_command = "cmd2", expected_output_contains = "SENTINEL_C2" }
            }
        });
        WriteLog("s1",
            ("cmd1 && echo SENTINEL_C1", true, "ok\nSENTINEL_C1"),
            ("cmd2 && echo SENTINEL_C2", true, "ok\nSENTINEL_C2"));

        var result = await Validator().ValidateAsync(EmptyHistory());
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task SentinelFromDifferentTurnOrAgent_Passes()
    {
        // The validator scans all session turns, not just the current one.
        // Evidence produced by a prior turn still counts.
        var sessionId = "session-multi-turn";
        WriteBrief(new
        {
            acceptance_criteria = new object[]
            {
                new { criterion = "C1", test_command = "cmd1", expected_output_contains = "SENTINEL_C1" }
            }
        });

        var log = new
        {
            ActiveSessionId = sessionId,
            Entries = new[]
            {
                new
                {
                    Agent = "Developer", TurnIndex = 0, Timestamp = DateTime.UtcNow,
                    SessionId = sessionId, FilesWritten = Array.Empty<string>(),
                    FilesDeleted = Array.Empty<string>(),
                    CommandsRun = new[] { new { Command = "cmd1", succeeded = true, Output = "SENTINEL_C1" } },
                    GitCommits = Array.Empty<string>()
                },
                new
                {
                    Agent = "Reviewer", TurnIndex = 1, Timestamp = DateTime.UtcNow,
                    SessionId = sessionId, FilesWritten = Array.Empty<string>(),
                    FilesDeleted = Array.Empty<string>(),
                    CommandsRun = new[] { new { Command = string.Empty, succeeded = false, Output = string.Empty } }[..0],
                    GitCommits = Array.Empty<string>()
                }
            }
        };
        File.WriteAllText(_logPath, JsonSerializer.Serialize(log));

        var result = await Validator().ValidateAsync(EmptyHistory());
        Assert.True(result.IsValid);
    }

    // -- Sentinel missing: blocks -------------------------------------------------

    [Fact]
    public async Task SentinelNotInOutput_Blocks()
    {
        WriteBrief(new
        {
            acceptance_criteria = new object[]
            {
                new
                {
                    criterion               = "Runtime: x = include works",
                    test_command            = "./bin/kiwi /tmp/ac.kiwi && echo CRITERION_RUNTIME_PASS",
                    expected_output_contains = "CRITERION_RUNTIME_PASS"
                }
            }
        });
        // Command ran but exited non-zero (succeeded = false) — output doesn't count.
        WriteLog("s1", ("./bin/kiwi /tmp/ac.kiwi", false, "[EXIT 1] FunctionUndefinedError: sqrt"));

        var result = await Validator().ValidateAsync(EmptyHistory());
        Assert.False(result.IsValid);
        Assert.Contains("CRITERION_RUNTIME_PASS", result.ErrorMessage);
        Assert.Contains("Runtime: x = include works", result.ErrorMessage);
    }

    [Fact]
    public async Task NoCommandsRun_Blocks()
    {
        WriteBrief(new
        {
            acceptance_criteria = new object[]
            {
                new { criterion = "C1", test_command = "cmd1 && echo S1", expected_output_contains = "S1" }
            }
        });
        WriteLog("s1"); // empty CommandsRun

        var result = await Validator().ValidateAsync(EmptyHistory());
        Assert.False(result.IsValid);
        Assert.Contains("S1", result.ErrorMessage);
    }

    [Fact]
    public async Task PartialSatisfaction_BlocksWithOnlyUnsatisfied()
    {
        WriteBrief(new
        {
            acceptance_criteria = new object[]
            {
                new { criterion = "C1", test_command = "cmd1", expected_output_contains = "SENTINEL_C1" },
                new { criterion = "C2", test_command = "cmd2", expected_output_contains = "SENTINEL_C2" }
            }
        });
        // C1 passes, C2 does not.
        WriteLog("s1", ("cmd1", true, "SENTINEL_C1"));

        var result = await Validator().ValidateAsync(EmptyHistory());
        Assert.False(result.IsValid);
        Assert.DoesNotContain("SENTINEL_C1", result.ErrorMessage); // C1 was satisfied
        Assert.Contains("SENTINEL_C2", result.ErrorMessage);        // C2 is missing
        Assert.Contains("C2",          result.ErrorMessage);
    }

    [Fact]
    public async Task StaleSessionOutputIgnored_Blocks()
    {
        // A passing command from a different session must not satisfy the check.
        WriteBrief(new
        {
            acceptance_criteria = new object[]
            {
                new { criterion = "C1", test_command = "cmd1", expected_output_contains = "SENTINEL_C1" }
            }
        });

        var log = new
        {
            ActiveSessionId = "current-session",
            Entries = new[]
            {
                new
                {
                    Agent = "Developer", TurnIndex = 0, Timestamp = DateTime.UtcNow,
                    SessionId = "old-session",           // different session
                    FilesWritten = Array.Empty<string>(), FilesDeleted = Array.Empty<string>(),
                    CommandsRun = new[] { new { Command = "cmd1", succeeded = true, Output = "SENTINEL_C1" } },
                    GitCommits = Array.Empty<string>()
                }
            }
        };
        File.WriteAllText(_logPath, JsonSerializer.Serialize(log));

        var result = await Validator().ValidateAsync(EmptyHistory());
        Assert.False(result.IsValid);
        Assert.Contains("SENTINEL_C1", result.ErrorMessage);
    }

    // -- Missing/invalid brief ----------------------------------------------------

    [Fact]
    public async Task MissingBrief_Blocks()
    {
        File.Delete(_briefPath);
        var result = await Validator().ValidateAsync(EmptyHistory());
        Assert.False(result.IsValid);
        Assert.Contains("does not exist", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedBrief_Blocks()
    {
        File.WriteAllText(_briefPath, "not valid json {{{");
        var result = await Validator().ValidateAsync(EmptyHistory());
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task MissingChangeLog_Blocks()
    {
        WriteBrief(new
        {
            acceptance_criteria = new object[]
            {
                new { criterion = "C1", test_command = "cmd1", expected_output_contains = "S1" }
            }
        });
        File.Delete(_logPath);

        // No log → no evidence → blocks.
        var result = await Validator().ValidateAsync(EmptyHistory());
        Assert.False(result.IsValid);
    }

    // -- Mixed plain + testable criteria ------------------------------------------

    [Fact]
    public async Task MixedCriteria_OnlyTestableCriteriaEnforced()
    {
        WriteBrief(new
        {
            acceptance_criteria = new object[]
            {
                "Plain string criterion — not machine testable",
                new { criterion = "Testable C1", test_command = "cmd1", expected_output_contains = "SENTINEL_C1" },
                new { criterion = "No sentinel", test_command = "cmd2" }  // no expected_output_contains
            }
        });
        // Only SENTINEL_C1 is required — plain string and no-sentinel object are skipped.
        WriteLog("s1", ("cmd1", true, "ok\nSENTINEL_C1"));

        var result = await Validator().ValidateAsync(EmptyHistory());
        Assert.True(result.IsValid);
    }
}
