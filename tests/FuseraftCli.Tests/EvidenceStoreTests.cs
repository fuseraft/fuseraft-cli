using fuseraft.Core.Models.Repository;
using fuseraft.Orchestration.Knowledge;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="EvidenceStore"/>.
///
/// The on-disk graph file is shared by every <see cref="EvidenceStore"/> instance ever
/// pointed at the same path (e.g. successive eval-suite cases run sequentially against the
/// same project). Regression coverage here targets the case where a *later* instance's
/// <see cref="EvidenceStore.SetSessionIdAsync"/> call overwrites the shared file's
/// <c>ActiveSessionId</c> after an *earlier* instance already stamped its own session —
/// queries on the earlier instance must keep using the session it was actually stamped
/// with, not whatever the file says most recently.
/// </summary>
public sealed class EvidenceStoreTests
{
    private static string NewTempGraphPath() =>
        Path.Combine(Path.GetTempPath(), $"evidence-store-test-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task QueryNodes_UsesOwnStampedSession_NotLaterSharedFileOverwrite()
    {
        var path = NewTempGraphPath();
        try
        {
            var storeA = new EvidenceStore(path);
            await storeA.SetSessionIdAsync("session-A");
            await storeA.RecordAsync(
                [new EvidenceNode { NodeType = "FileWrite", SessionId = "session-A", Path = "a.py" }]);

            // Simulate the next eval case starting: a second instance over the same file
            // stamps its own (different) session, overwriting the shared ActiveSessionId.
            var storeB = new EvidenceStore(path);
            await storeB.SetSessionIdAsync("session-B");

            // storeA's own query must still see session-A's evidence, not session-B's
            // (empty) view, even though the file's ActiveSessionId now says "session-B".
            var writtenByA = await storeA.GetWrittenFilePathsAsync();

            Assert.Contains("a.py", writtenByA);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task QueryNodes_FallsBackToFileActiveSessionId_WhenInstanceNeverStamped()
    {
        var path = NewTempGraphPath();
        try
        {
            var writer = new EvidenceStore(path);
            await writer.SetSessionIdAsync("session-A");
            await writer.RecordAsync(
                [new EvidenceNode { NodeType = "FileWrite", SessionId = "session-A", Path = "a.py" }]);

            // A fresh, never-stamped instance over the same file falls back to whatever
            // the file itself says is active — preserving the original read-only-caller behavior.
            var reader = new EvidenceStore(path);
            var written = await reader.GetWrittenFilePathsAsync();

            Assert.Contains("a.py", written);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
