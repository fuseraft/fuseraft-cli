using System.Text.Json;
using Microsoft.Extensions.AI;
using fuseraft.Core.Models;
using fuseraft.Orchestration.Validation;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="HandoffToReviewerValidator"/>.
/// Each test targets a specific numbered check inside the validator.
/// Files are written to a per-test-run temp directory and cleaned up on dispose.
/// </summary>
public sealed class HandoffToReviewerValidatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fuseraft_reviewer_{Guid.NewGuid():N}");
    private static readonly IList<ChatMessage> NoHistory = [];

    public HandoffToReviewerValidatorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string ReportPath  => Path.Combine(_dir, "test-report.json");
    private string BriefPath   => Path.Combine(_dir, "brief.json");
    private string ChangesPath => Path.Combine(_dir, "changes.json");

    private ValidationConfig Config(bool withChanges = false) => new()
    {
        TestReportPath       = ReportPath,
        BriefPath            = BriefPath,
        ChangeLogPath        = withChanges ? ChangesPath : null,
        TestAssertionPatterns = [@"\bassert\b", @"\bexpect\b"]
    };

    private HandoffToReviewerValidator Validator(bool withChanges = false)
        => new(Config(withChanges));

    // JSON helpers

    // Raw JSON string helpers — use exact field names the validator expects.

    private static string Report(
        string status = "PASS",
        string criterion = "it works",
        string command = "go test ./...",
        int exitCode = 0,
        string[]? fakeFiles = null)
        => $$"""
             {
               "results": [
                 {
                   "criterion": "{{criterion}}",
                   "status": "{{status}}",
                   "command": "{{command}}",
                   "exit_code": {{exitCode}}
                 }
               ],
               "fake_test_files": {{JsonSerializer.Serialize(fakeFiles ?? [])}}
             }
             """;

    private static string MultiResultReport(
        IEnumerable<(string criterion, string status, string command, int exitCode)> results,
        string[]? fakeFiles = null)
    {
        var entries = results.Select(r =>
            $$"""{"criterion":"{{r.criterion}}","status":"{{r.status}}","command":"{{r.command}}","exit_code":{{r.exitCode}}}""");
        return $$"""
                 {"results":[{{string.Join(",", entries)}}],"fake_test_files":{{JsonSerializer.Serialize(fakeFiles ?? [])}}}
                 """;
    }

    private async Task WriteReport(string json) => await File.WriteAllTextAsync(ReportPath, json);

    private async Task WriteBrief(
        string goal = "build it",
        string[]? criteria = null,
        (string path, string reason)[]? files = null)
    {
        var brief = new
        {
            goal,
            files_to_change = (files ?? [("main.go", "entry")])
                .Select(f => new { path = f.path, reason = f.reason }),
            acceptance_criteria = criteria ?? new[] { "it works" }
        };
        await File.WriteAllTextAsync(BriefPath, JsonSerializer.Serialize(brief));
    }

    private async Task WriteChanges(string sessionId, params string[] commands)
    {
        var log = new ChangeLog
        {
            ActiveSessionId = sessionId,
            Entries =
            [
                new ChangeEntry
                {
                    Agent = "Developer",
                    TurnIndex = 0,
                    Timestamp = DateTime.UtcNow,
                    SessionId = sessionId,
                    CommandsRun = commands.Select(c => new CommandRecord { Command = c, Succeeded = true }).ToList()
                }
            ]
        };
        await File.WriteAllTextAsync(ChangesPath, JsonSerializer.Serialize(log));
    }

    private async Task WriteChangesWithOutput(
        string sessionId,
        params (string command, string output)[] entries)
    {
        var log = new ChangeLog
        {
            ActiveSessionId = sessionId,
            Entries =
            [
                new ChangeEntry
                {
                    Agent     = "Tester",
                    TurnIndex = 1,
                    Timestamp = DateTime.UtcNow,
                    SessionId = sessionId,
                    CommandsRun = entries.Select(e => new CommandRecord
                    {
                        Command   = e.command,
                        Succeeded = true,
                        Output    = e.output
                    }).ToList()
                }
            ]
        };
        await File.WriteAllTextAsync(ChangesPath, JsonSerializer.Serialize(log));
    }

    // Check 1: test-report.json existence

    [Fact]
    public async Task Check1_ReportMissing_Fails()
    {
        var result = await Validator().ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
        Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // Check 2: JSON validity and non-empty results

    [Fact]
    public async Task Check2_InvalidJson_Fails()
    {
        await WriteReport("not json {{{");

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
        Assert.Contains("could not be parsed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Check2_EmptyResults_Fails()
    {
        await WriteReport("""{"results":[],"fake_test_files":[]}""");

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
        Assert.Contains("no results", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // Check 3: no FAIL results

    [Fact]
    public async Task Check3_FailResult_Fails()
    {
        await WriteReport(Report(status: "FAIL", command: "go test ./...", exitCode: 1));

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
        Assert.Contains("FAIL", result.ErrorMessage);
    }

    // Check 4: no PASS with empty command

    [Fact]
    public async Task Check4_PassWithEmptyCommand_Fails()
    {
        await WriteReport(Report(command: ""));

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
        Assert.Contains("empty", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Check4_PassWithWhitespaceCommand_Fails()
    {
        await WriteReport(Report(command: "   "));

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
    }

    // Check 4b: no tool-call strings as commands

    [Theory]
    [InlineData("FileSystem-read_file path=test.go")]
    [InlineData("Search-web_search query=hello")]
    [InlineData("Git-git_status")]
    [InlineData("Scratchpad-read")]
    [InlineData("Changes-changes_read_latest")]
    [InlineData("MCP-browser_navigate")]
    public async Task Check4b_ToolCallCommand_Fails(string command)
    {
        await WriteReport(Report(command: command));

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
        Assert.Contains("tool call", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // Check 4c removed: PASS results with non-zero exit_code are now allowed.
    // Testers legitimately write PASS entries for criteria that test expected error exit
    // codes (e.g. "exit 3 for missing file") — blocking on non-zero exit_code caused
    // consistent double-Tester loops. Check 8 (command cross-reference) provides the
    // anti-fabrication guarantee instead.

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Check4c_Removed_PassWithNonZeroExitCode_NowAllowed(int exitCode)
    {
        await WriteReport(Report(status: "PASS", command: "go test ./...", exitCode: exitCode));

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.True(result.IsValid);
    }

    // Check 5: no declared fake test files

    [Fact]
    public async Task Check5_FakeTestFileDeclared_Fails()
    {
        await WriteReport(Report(fakeFiles: ["tests/fake_test.go"]));

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
        Assert.Contains("fake", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // Check 6: criterion coverage (requires brief.json)

    [Fact]
    public async Task Check6_FewerResultsThanCriteria_Fails()
    {
        await WriteBrief(criteria: ["criterion 1", "criterion 2"]);
        // Report has only one result; brief has two criteria.
        await WriteReport(Report(criterion: "criterion 1"));

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
        Assert.Contains("criteria", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Check6_ResultCountMatchesCriteria_Passes()
    {
        await WriteBrief(criteria: ["c1", "c2"]);
        await WriteReport(MultiResultReport(
        [
            ("c1", "PASS", "go test -run TestC1 ./...", 0),
            ("c2", "PASS", "go test -run TestC2 ./...", 0)
        ]));

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Check6_NullAcceptanceCriteriaInBrief_Fails()
    {
        // Brief exists but has no acceptance_criteria field at all.
        await File.WriteAllTextAsync(BriefPath, """{"goal":"g","files_to_change":[{"path":"a.go","reason":"r"}]}""");
        await WriteReport(Report());

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
        Assert.Contains("acceptance_criteria", result.ErrorMessage);
    }

    // Check 7: static fake-test detection

    [Fact]
    public async Task Check7_TestFileWithNoAssertions_Fails()
    {
        var testFile = Path.Combine(_dir, "app_test.go");
        await File.WriteAllTextAsync(testFile, "package app\n\nfunc TestFoo(t *testing.T) {\n  // nothing here\n}\n");

        await WriteBrief(files: [(testFile, "test file")]);
        await WriteReport(Report(command: "go test ./..."));

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
        Assert.Contains("assertion", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Check7_TestFileWithAssertions_Passes()
    {
        var testFile = Path.Combine(_dir, "app_test.go");
        await File.WriteAllTextAsync(testFile,
            "package app\n\nfunc TestFoo(t *testing.T) {\n  assert(len(result) > 0)\n}\n");

        await WriteBrief(files: [(testFile, "test file")]);
        await WriteReport(Report(command: "go test ./..."));

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Check7_NonTestFileInBrief_NotChecked_Passes()
    {
        // Files without "test" in the path are not checked for assertions.
        await WriteBrief(files: [("main.go", "entry point")]);
        await WriteReport(Report(command: "go test ./..."));

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.True(result.IsValid);
    }

    // Check 8: command cross-reference against changes.json

    [Fact]
    public async Task Check8_NoChangesJson_Skipped_Passes()
    {
        // ChangeLogPath is null — check 8 should be completely skipped.
        await WriteReport(Report(command: "go test ./..."));

        var result = await Validator(withChanges: false).ValidateAsync(NoHistory);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Check8_ChangesJsonMissing_Skipped_Passes()
    {
        // ChangeLogPath is set but the file doesn't exist — graceful degradation.
        await WriteReport(Report(command: "go test ./..."));
        // Do NOT write changes.json.

        var result = await Validator(withChanges: true).ValidateAsync(NoHistory);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Check8_NoCommandsInChanges_PassResultsRejected_Fails()
    {
        await WriteReport(Report(command: "go test ./..."));
        await WriteChanges("session-1");  // session exists but has no commands

        // Override to write an empty commands list.
        var log = new ChangeLog { ActiveSessionId = "session-1", Entries = [] };
        await File.WriteAllTextAsync(ChangesPath, JsonSerializer.Serialize(log));

        var result = await Validator(withChanges: true).ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
        Assert.Contains("no shell commands recorded", result.ErrorMessage);
    }

    [Fact]
    public async Task Check8_CommandMatchesCurrentSession_Passes()
    {
        await WriteReport(Report(command: "go test ./..."));
        await WriteChanges("session-1", "go test ./...");

        // Set the active session to match.
        var log = new ChangeLog
        {
            ActiveSessionId = "session-1",
            Entries =
            [
                new ChangeEntry
                {
                    SessionId  = "session-1",
                    Agent      = "Developer",
                    TurnIndex  = 0,
                    Timestamp  = DateTime.UtcNow,
                    CommandsRun = [new CommandRecord { Command = "go test ./...", Succeeded = true }]
                }
            ]
        };
        await File.WriteAllTextAsync(ChangesPath, JsonSerializer.Serialize(log));

        var result = await Validator(withChanges: true).ValidateAsync(NoHistory);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Check8_CommandNotMatchedInChanges_Fails()
    {
        await WriteReport(Report(command: "cargo test unit_tests"));
        await WriteChanges("session-1", "go mod tidy");  // unrelated command

        var log = new ChangeLog
        {
            ActiveSessionId = "session-1",
            Entries =
            [
                new ChangeEntry
                {
                    SessionId  = "session-1",
                    Agent      = "Developer",
                    TurnIndex  = 0,
                    Timestamp  = DateTime.UtcNow,
                    CommandsRun = [new CommandRecord { Command = "go mod tidy", Succeeded = true }]
                }
            ]
        };
        await File.WriteAllTextAsync(ChangesPath, JsonSerializer.Serialize(log));

        var result = await Validator(withChanges: true).ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
        Assert.Contains("not found in the change log", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Check8_SessionFilter_PriorSessionCommandIgnored_Fails()
    {
        // The matching command exists in changes.json but belongs to a previous session.
        // The current active session has no commands — check 8 must reject the PASS.
        await WriteReport(Report(command: "cargo test integration_tests"));

        var log = new ChangeLog
        {
            ActiveSessionId = "session-current",  // active session has no entries
            Entries =
            [
                new ChangeEntry
                {
                    SessionId  = "session-old",   // different session
                    Agent      = "Developer",
                    TurnIndex  = 0,
                    Timestamp  = DateTime.UtcNow.AddHours(-1),
                    CommandsRun = [new CommandRecord { Command = "cargo test integration_tests", Succeeded = true }]
                }
            ]
        };
        await File.WriteAllTextAsync(ChangesPath, JsonSerializer.Serialize(log));

        var result = await Validator(withChanges: true).ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
        Assert.Contains("no shell commands recorded", result.ErrorMessage);
    }

    // Check 8: command-string matching

    [Fact]
    public async Task Check8_SharedCommandToken_Passes()
    {
        // Commands that share a significant token (e.g. same test name) match even if
        // the full command strings differ (e.g. different flags or argument order).
        await WriteReport(Report(command: "go test -run TestUserAuth ./auth/..."));
        await WriteChangesWithOutput("s1",
            ("go test ./auth/... -v -run TestUserAuth", ""));

        var result = await Validator(withChanges: true).ValidateAsync(NoHistory);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Check8_NoTokenOverlap_Fails()
    {
        // Completely unrelated command strings — no substring or token match possible.
        await WriteReport(Report(command: "go test ./..."));
        await WriteChangesWithOutput("s1", ("npm install", ""));

        var result = await Validator(withChanges: true).ValidateAsync(NoHistory);

        Assert.False(result.IsValid);
        Assert.Contains("not found in the change log", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Check8_ExactCommandMatch_Passes()
    {
        // Identical command string — direct substring match.
        await WriteReport(Report(command: "go build ./..."));
        await WriteChangesWithOutput("s1", ("go build ./...", ""));

        var result = await Validator(withChanges: true).ValidateAsync(NoHistory);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Check8_CommandSubstring_Passes()
    {
        // Report command is a substring of the logged command (e.g. agent added -v flag).
        await WriteReport(Report(command: "go test ./..."));
        await WriteChangesWithOutput("s1", ("go test ./... -v", ""));

        var result = await Validator(withChanges: true).ValidateAsync(NoHistory);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Check8_MultipleSessionEntries_CommandInLaterEntry_Passes()
    {
        // The matching command may appear in any entry for the session, not just the first.
        var log = new ChangeLog
        {
            ActiveSessionId = "s1",
            Entries =
            [
                new ChangeEntry
                {
                    Agent       = "Developer",
                    TurnIndex   = 0,
                    Timestamp   = DateTime.UtcNow,
                    SessionId   = "s1",
                    CommandsRun = [new CommandRecord { Command = "go build ./...", Succeeded = true }]
                },
                new ChangeEntry
                {
                    Agent       = "Tester",
                    TurnIndex   = 1,
                    Timestamp   = DateTime.UtcNow,
                    SessionId   = "s1",
                    CommandsRun = [new CommandRecord { Command = "go test ./...", Succeeded = true }]
                }
            ]
        };
        await File.WriteAllTextAsync(ChangesPath, JsonSerializer.Serialize(log));
        await WriteReport(Report(command: "go test ./..."));

        var result = await Validator(withChanges: true).ValidateAsync(NoHistory);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Check8_FailedCommand_IncludedInMatching_Passes()
    {
        // Commands recorded with Succeeded=false (expected-failure tests) still count.
        const string cmd = "python3 logparse.py nonexistent.ndjson";

        var log = new ChangeLog
        {
            ActiveSessionId = "s1",
            Entries =
            [
                new ChangeEntry
                {
                    Agent       = "Tester",
                    TurnIndex   = 1,
                    Timestamp   = DateTime.UtcNow,
                    SessionId   = "s1",
                    CommandsRun = [new CommandRecord { Command = cmd, Succeeded = false }]
                }
            ]
        };
        await File.WriteAllTextAsync(ChangesPath, JsonSerializer.Serialize(log));
        await WriteReport(Report(command: cmd));

        var result = await Validator(withChanges: true).ValidateAsync(NoHistory);

        Assert.True(result.IsValid);
    }

    // Happy path

    [Fact]
    public async Task HappyPath_NoBriefNoChanges_AllChecksPass()
    {
        await WriteReport(Report());

        var result = await Validator().ValidateAsync(NoHistory);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task HappyPath_WithBriefAndChanges_AllChecksPass()
    {
        var testFile = Path.Combine(_dir, "app_test.go");
        await File.WriteAllTextAsync(testFile,
            "func TestApp() { expect(result).toEqual(expected) }");

        await WriteBrief(
            criteria: ["app starts without error"],
            files: [(testFile, "test file")]);

        await WriteReport(MultiResultReport(
        [
            ("app starts without error", "PASS", "go test ./...", 0)
        ]));

        var log = new ChangeLog
        {
            ActiveSessionId = "session-xyz",
            Entries =
            [
                new ChangeEntry
                {
                    SessionId  = "session-xyz",
                    Agent      = "Developer",
                    TurnIndex  = 1,
                    Timestamp  = DateTime.UtcNow,
                    CommandsRun = [new CommandRecord { Command = "go test ./...", Succeeded = true }]
                }
            ]
        };
        await File.WriteAllTextAsync(ChangesPath, JsonSerializer.Serialize(log));

        var result = await Validator(withChanges: true).ValidateAsync(NoHistory);

        Assert.True(result.IsValid);
    }
}
