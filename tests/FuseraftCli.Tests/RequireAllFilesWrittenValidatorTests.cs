using System.Text.Json;
using Microsoft.Extensions.AI;
using fuseraft.Core.Models;
using fuseraft.Orchestration.Validation;

namespace FuseraftCli.Tests;

public sealed class RequireAllFilesWrittenValidatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fuseraft_allfiles_{Guid.NewGuid():N}");

    private string BriefPath   => Path.Combine(_dir, "brief.json");
    private string ChangesPath => Path.Combine(_dir, "changes.json");

    public RequireAllFilesWrittenValidatorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private RequireAllFilesWrittenValidator Validator(bool withChanges = false)
        => new(BriefPath, withChanges ? ChangesPath : null);

    // Brief helpers

    private async Task WriteBrief(params string[] filePaths)
    {
        var brief = new
        {
            goal = "build it",
            files_to_change = filePaths,
            acceptance_criteria = new[] { "it works" }
        };
        await File.WriteAllTextAsync(BriefPath, JsonSerializer.Serialize(brief));
    }

    private async Task WriteBriefNoFiles()
    {
        await File.WriteAllTextAsync(BriefPath, """{"goal":"g","acceptance_criteria":["c"]}""");
    }

    // History helpers

    private static ChatMessage UserMsg() => new(ChatRole.User, "task");

    /// <summary>
    /// Builds an assistant message with a write_file call and a corresponding tool
    /// result message, simulating a successful write_file invocation.
    /// </summary>
    private static (ChatMessage call, ChatMessage result) WriteFileMsg(
        string path, string callId)
    {
        var callMsg = new ChatMessage(ChatRole.Assistant,
            new List<AIContent>
            {
                new FunctionCallContent(callId, "write_file",
                    new Dictionary<string, object?> { ["path"] = path, ["content"] = "data" })
            });

        var resultMsg = new ChatMessage(ChatRole.Tool,
            new List<AIContent> { new FunctionResultContent(callId, (object)"OK") });

        return (callMsg, resultMsg);
    }

    // changes.json helpers

    private async Task WriteChanges(string sessionId, params string[] writtenFiles)
    {
        var log = new ChangeLog
        {
            ActiveSessionId = sessionId,
            Entries =
            [
                new ChangeEntry
                {
                    Agent        = "Developer",
                    TurnIndex    = 0,
                    Timestamp    = DateTime.UtcNow,
                    SessionId    = sessionId,
                    FilesWritten = writtenFiles.ToList()
                }
            ]
        };
        await File.WriteAllTextAsync(ChangesPath, JsonSerializer.Serialize(log));
    }

    // Brief missing / invalid

    [Fact]
    public async Task BriefMissing_Fails()
    {
        var result = await Validator().ValidateAsync([]);

        Assert.False(result.IsValid);
        Assert.Contains("does not exist", result.ErrorMessage);
    }

    [Fact]
    public async Task BriefInvalidJson_Fails()
    {
        await File.WriteAllTextAsync(BriefPath, "{bad json");

        var result = await Validator().ValidateAsync([]);

        Assert.False(result.IsValid);
        Assert.Contains("could not be parsed", result.ErrorMessage);
    }

    // No files to check

    [Fact]
    public async Task BriefHasNoFilesToChange_Passes()
    {
        await WriteBriefNoFiles();

        var result = await Validator().ValidateAsync([]);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task BriefHasEmptyFilesToChange_Passes()
    {
        await File.WriteAllTextAsync(BriefPath,
            """{"goal":"g","files_to_change":[],"acceptance_criteria":["c"]}""");

        var result = await Validator().ValidateAsync([]);

        Assert.True(result.IsValid);
    }

    // Current-turn history detection

    [Fact]
    public async Task SingleRequiredFile_WrittenThisTurn_Passes()
    {
        await WriteBrief("src/main.go");
        var (call, res) = WriteFileMsg("src/main.go", "c1");

        var outcome = await Validator().ValidateAsync([UserMsg(), call, res]);

        Assert.True(outcome.IsValid);
    }

    [Fact]
    public async Task MultipleRequiredFiles_AllWrittenThisTurn_Passes()
    {
        await WriteBrief("src/main.go", "src/handler.go", "src/db.go");
        var (c1, r1) = WriteFileMsg("src/main.go",    "id1");
        var (c2, r2) = WriteFileMsg("src/handler.go", "id2");
        var (c3, r3) = WriteFileMsg("src/db.go",      "id3");

        var outcome = await Validator().ValidateAsync([UserMsg(), c1, r1, c2, r2, c3, r3]);

        Assert.True(outcome.IsValid);
    }

    [Fact]
    public async Task RequiredFile_NotWrittenAtAll_Fails()
    {
        await WriteBrief("src/main.go");

        var outcome = await Validator().ValidateAsync([UserMsg()]);

        Assert.False(outcome.IsValid);
        Assert.Contains("src/main.go", outcome.ErrorMessage);
    }

    [Fact]
    public async Task PartialCoverage_OneFileMissing_Fails()
    {
        await WriteBrief("src/main.go", "src/handler.go");
        var (c1, r1) = WriteFileMsg("src/main.go", "id1");

        var outcome = await Validator().ValidateAsync([UserMsg(), c1, r1]);

        Assert.False(outcome.IsValid);
        // Missing file must appear in the error.
        Assert.Contains("src/handler.go", outcome.ErrorMessage);
        // Written file must appear in the confirmed-written list, not the missing list.
        Assert.Contains("✓ src/main.go", outcome.ErrorMessage);
        Assert.DoesNotContain("✗ src/main.go", outcome.ErrorMessage);
    }

    [Fact]
    public async Task WriteFileBeforeTurnBoundary_NotCounted_Fails()
    {
        // File written in a previous turn (before the User message) must not count.
        await WriteBrief("src/main.go");
        var (prevCall, prevResult) = WriteFileMsg("src/main.go", "old");

        var history = new List<ChatMessage>
        {
            prevCall, prevResult,   // previous turn
            UserMsg(),              // boundary
            // no write_file in current turn
        };

        var outcome = await Validator().ValidateAsync(history);

        Assert.False(outcome.IsValid);
    }

    // Path normalisation

    [Fact]
    public async Task LeadingDotSlash_InBrief_MatchesWithout_Passes()
    {
        await WriteBrief("./src/main.go");        // brief uses ./
        var (call, res) = WriteFileMsg("src/main.go", "c1"); // history without ./

        var outcome = await Validator().ValidateAsync([UserMsg(), call, res]);

        Assert.True(outcome.IsValid);
    }

    [Fact]
    public async Task LeadingDotSlash_InHistory_MatchesBriefWithout_Passes()
    {
        await WriteBrief("src/main.go");
        var (call, res) = WriteFileMsg("./src/main.go", "c1");

        var outcome = await Validator().ValidateAsync([UserMsg(), call, res]);

        Assert.True(outcome.IsValid);
    }

    // changes.json (previous-turn) detection

    [Fact]
    public async Task RequiredFile_WrittenInPreviousTurn_changes_json_Passes()
    {
        await WriteBrief("src/main.go");
        await WriteChanges("session-1", "src/main.go");

        // No write_file in current turn — should still pass because of changes.json.
        var outcome = await Validator(withChanges: true).ValidateAsync([UserMsg()]);

        Assert.True(outcome.IsValid);
    }

    [Fact]
    public async Task SplitAcrossTurns_FilesAcrossMultipleChangesEntries_Passes()
    {
        // When changeLogPath is set the validator reads changes.json exclusively.
        // ChangeTracker.FlushTurnAsync is guaranteed to run before validators fire, so
        // the current turn's writes are already in changes.json by the time we get here.
        // This test verifies that files spread across multiple ChangeEntry rows in the
        // same session are all counted correctly.
        await WriteBrief("src/main.go", "src/handler.go");

        var log = new ChangeLog
        {
            ActiveSessionId = "session-1",
            Entries =
            [
                new ChangeEntry
                {
                    Agent = "Developer", TurnIndex = 0, Timestamp = DateTime.UtcNow,
                    SessionId = "session-1", FilesWritten = ["src/main.go"]
                },
                new ChangeEntry
                {
                    Agent = "Developer", TurnIndex = 1, Timestamp = DateTime.UtcNow,
                    SessionId = "session-1", FilesWritten = ["src/handler.go"]
                }
            ]
        };
        await File.WriteAllTextAsync(ChangesPath, JsonSerializer.Serialize(log));

        var outcome = await Validator(withChanges: true).ValidateAsync([UserMsg()]);

        Assert.True(outcome.IsValid);
    }

    [Fact]
    public async Task SessionFilter_FileFromPriorSession_NotCounted_Fails()
    {
        // changes.json has the file, but it belongs to a different session.
        await WriteBrief("src/main.go");

        var log = new ChangeLog
        {
            ActiveSessionId = "session-current",
            Entries =
            [
                new ChangeEntry
                {
                    SessionId    = "session-old",   // different session — must not count
                    Agent        = "Developer",
                    TurnIndex    = 0,
                    Timestamp    = DateTime.UtcNow.AddHours(-1),
                    FilesWritten = ["src/main.go"]
                }
            ]
        };
        await File.WriteAllTextAsync(ChangesPath, JsonSerializer.Serialize(log));

        var outcome = await Validator(withChanges: true).ValidateAsync([UserMsg()]);

        Assert.False(outcome.IsValid);
        Assert.Contains("src/main.go", outcome.ErrorMessage);
    }

    [Fact]
    public async Task ChangesJsonMissing_OnlyHistoryCounted_Fails()
    {
        await WriteBrief("src/main.go");
        // changeLogPath is set but the file doesn't exist.
        // No write in history either — must fail.

        var outcome = await Validator(withChanges: true).ValidateAsync([UserMsg()]);

        Assert.False(outcome.IsValid);
    }

    [Fact]
    public async Task WithChangeLogPath_FileOnlyInHistory_NotCounted_Fails()
    {
        // When changeLogPath is configured the validator reads changes.json exclusively.
        // A write_file call that appears in the chat history but is NOT yet flushed to
        // changes.json must NOT be counted — this proves the contract that ChangeTracker
        // FlushTurnAsync is authoritative and the history-scan code path is bypassed.
        await WriteBrief("src/main.go");
        var (call, res) = WriteFileMsg("src/main.go", "c1");

        // changeLogPath is set but changes.json has no record of the file.
        var log = new ChangeLog { ActiveSessionId = "session-1", Entries = [] };
        await File.WriteAllTextAsync(ChangesPath, JsonSerializer.Serialize(log));

        var outcome = await Validator(withChanges: true).ValidateAsync([UserMsg(), call, res]);

        Assert.False(outcome.IsValid);
        Assert.Contains("src/main.go", outcome.ErrorMessage);
    }

    // Suffix matching (absolute/relative mismatch)

    [Fact]
    public async Task AbsolutePathInChanges_MatchesRelativeBriefPath_Passes()
    {
        await WriteBrief("src/main.go");
        // changes.json written with an absolute-style path that ends with the brief path
        await WriteChanges("session-1", "/project/src/main.go");

        var outcome = await Validator(withChanges: true).ValidateAsync([UserMsg()]);

        Assert.True(outcome.IsValid);
    }

    // Pre-existing file handling (disk-existence check)

    [Fact]
    public async Task NewFile_NotOnDisk_NotWritten_Fails()
    {
        // A file that didn't exist before and was never written → must fail.
        var newFile = Path.Combine(_dir, "brand_new.go");
        // Deliberately do NOT create the file on disk.
        await WriteBrief(newFile);

        var outcome = await Validator().ValidateAsync([UserMsg()]);

        Assert.False(outcome.IsValid);
        Assert.Contains(NormalizePath(newFile), outcome.ErrorMessage!);
    }

    [Fact]
    public async Task PreExistingFile_NotWrittenThisSession_Fails()
    {
        // Listing a file in files_to_change is a promise to modify it.
        // A pre-existing file not written this session must fail even though it exists on disk.
        var existingFile = Path.Combine(_dir, "existing.go");
        await File.WriteAllTextAsync(existingFile, "package main");
        await WriteBrief(existingFile);

        var outcome = await Validator().ValidateAsync([UserMsg()]);

        Assert.False(outcome.IsValid);
        Assert.Contains(NormalizePath(existingFile), outcome.ErrorMessage!);
    }

    [Fact]
    public async Task PreExistingFile_WrittenThisSession_Passes()
    {
        // File exists on disk AND was written this session → must pass.
        var existingFile = Path.Combine(_dir, "existing.go");
        await File.WriteAllTextAsync(existingFile, "package main");
        await WriteBrief(existingFile);
        var (call, res) = WriteFileMsg(existingFile, "c1");

        var outcome = await Validator().ValidateAsync([UserMsg(), call, res]);

        Assert.True(outcome.IsValid);
    }

    [Fact]
    public async Task Mixed_NewFileMissing_PreExistingNotWritten_BothFail()
    {
        // Brief lists two files, neither written this session:
        //   - brand_new.go: does not exist on disk → must appear in error
        //   - pre_existing.go: exists on disk but not written → must also appear in error
        var newFile      = Path.Combine(_dir, "brand_new.go");
        var existingFile = Path.Combine(_dir, "pre_existing.go");
        await File.WriteAllTextAsync(existingFile, "package main");
        await WriteBrief(newFile, existingFile);

        var outcome = await Validator().ValidateAsync([UserMsg()]);

        Assert.False(outcome.IsValid);
        Assert.Contains(NormalizePath(newFile),      outcome.ErrorMessage!);
        Assert.Contains(NormalizePath(existingFile), outcome.ErrorMessage!);
    }

    [Fact]
    public async Task AllFilesWritten_NoMissing_Passes()
    {
        // All files written this session — baseline sanity check preserved.
        await WriteBrief("src/main.go", "src/handler.go");
        var (c1, r1) = WriteFileMsg("src/main.go",    "id1");
        var (c2, r2) = WriteFileMsg("src/handler.go", "id2");

        var outcome = await Validator().ValidateAsync([UserMsg(), c1, r1, c2, r2]);

        Assert.True(outcome.IsValid);
    }

    // helper used in Mixed test above — mirrors NormalizePath in the validator
    private static string NormalizePath(string path)
    {
        path = path.Replace('\\', '/').Trim();
        if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        return path;
    }
}
