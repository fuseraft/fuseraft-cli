using System.Text.Json;
using Moq;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Session;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

public sealed class InvestigationPluginTests : IDisposable
{
    private readonly string _root;
    private readonly string _logPath;

    public InvestigationPluginTests()
    {
        _root    = Path.Combine(Path.GetTempPath(), "fuseraft_investigation_tests_" + Guid.NewGuid().ToString("N")[..8]);
        _logPath = Path.Combine(_root, "investigation-log.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private InvestigationPlugin NewPlugin(IEventSink? eventSink = null, string sessionId = "sess-1") =>
        new(_logPath, sessionId, eventSink);

    private async Task<InvestigationLog> ReadLogAsync()
    {
        var json = await File.ReadAllTextAsync(_logPath);
        return JsonSerializer.Deserialize<InvestigationLog>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    // CreateHypothesisAsync

    [Fact]
    public async Task CreateHypothesis_EmptyText_ReturnsError()
    {
        var plugin = NewPlugin();
        var result = await plugin.CreateHypothesisAsync("   ");
        Assert.StartsWith("[ERROR]", result);
        Assert.False(File.Exists(_logPath));
    }

    [Fact]
    public async Task CreateHypothesis_Valid_ReturnsIdAndPersists()
    {
        var plugin = NewPlugin();
        var result = await plugin.CreateHypothesisAsync("The bug is in the retry logic.");

        Assert.Contains("H-001", result);
        Assert.Contains("The bug is in the retry logic.", result);

        var log = await ReadLogAsync();
        Assert.Single(log.Hypotheses);
        Assert.Equal("H-001", log.Hypotheses[0].Id);
        Assert.Equal("open", log.Hypotheses[0].Status);
    }

    [Fact]
    public async Task CreateHypothesis_MultipleCalls_AssignsSequentialIds()
    {
        var plugin = NewPlugin();
        await plugin.CreateHypothesisAsync("First hypothesis.");
        var second = await plugin.CreateHypothesisAsync("Second hypothesis.");

        Assert.Contains("H-002", second);

        var log = await ReadLogAsync();
        Assert.Equal(2, log.Hypotheses.Count);
    }

    [Fact]
    public async Task CreateHypothesis_PersistsSessionId()
    {
        var plugin = NewPlugin(sessionId: "my-session-42");
        await plugin.CreateHypothesisAsync("A hypothesis.");

        var log = await ReadLogAsync();
        Assert.Equal("my-session-42", log.SessionId);
    }

    [Fact]
    public async Task CreateHypothesis_CorruptExistingLog_StartsFreshInsteadOfThrowing()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(_logPath, "{ not valid json");

        var plugin = NewPlugin();
        var result = await plugin.CreateHypothesisAsync("Recover from corruption.");

        Assert.Contains("H-001", result);
        var log = await ReadLogAsync();
        Assert.Single(log.Hypotheses);
    }

    // RejectHypothesisAsync

    [Fact]
    public async Task RejectHypothesis_EmptyId_ReturnsError()
    {
        var plugin = NewPlugin();
        var result = await plugin.RejectHypothesisAsync("  ", "reason");
        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task RejectHypothesis_UnknownId_ReturnsNotFound()
    {
        var plugin = NewPlugin();
        var result = await plugin.RejectHypothesisAsync("H-999", "reason");
        Assert.StartsWith("[NOT FOUND]", result);
    }

    [Fact]
    public async Task RejectHypothesis_Valid_UpdatesStatusAndEvidence()
    {
        var plugin = NewPlugin();
        await plugin.CreateHypothesisAsync("A hypothesis.");

        var result = await plugin.RejectHypothesisAsync("H-001", "Disproven by logs.", "line 1\nline 2");

        Assert.Contains("H-001", result);
        Assert.Contains("rejected", result);

        var log = await ReadLogAsync();
        Assert.Equal("rejected", log.Hypotheses[0].Status);
        Assert.Equal("Disproven by logs.", log.Hypotheses[0].RejectReason);
        Assert.Equal(["line 1", "line 2"], log.Hypotheses[0].Evidence);
    }

    [Fact]
    public async Task RejectHypothesis_IdIsCaseInsensitive()
    {
        var plugin = NewPlugin();
        await plugin.CreateHypothesisAsync("A hypothesis.");

        var result = await plugin.RejectHypothesisAsync("h-001", "reason");

        Assert.Contains("h-001", result);
        var log = await ReadLogAsync();
        Assert.Equal("rejected", log.Hypotheses[0].Status);
    }

    [Fact]
    public async Task RejectHypothesis_Valid_EmitsAttemptFailedEvent()
    {
        var sink = new Mock<IEventSink>();
        var plugin = NewPlugin(sink.Object);
        await plugin.CreateHypothesisAsync("A hypothesis worth rejecting.");

        await plugin.RejectHypothesisAsync("H-001", "Disproven.");

        sink.Verify(s => s.Emit(It.Is<AttemptFailedEvent>(e =>
            e.Description == "A hypothesis worth rejecting." && e.ErrorSummary == "Disproven.")), Times.Once);
    }

    [Fact]
    public async Task RejectHypothesis_UnknownId_DoesNotEmitEvent()
    {
        var sink = new Mock<IEventSink>();
        var plugin = NewPlugin(sink.Object);

        await plugin.RejectHypothesisAsync("H-999", "reason");

        sink.Verify(s => s.Emit(It.IsAny<ExecutionEvent>()), Times.Never);
    }

    // ConfirmHypothesisAsync

    [Fact]
    public async Task ConfirmHypothesis_EmptyId_ReturnsError()
    {
        var plugin = NewPlugin();
        var result = await plugin.ConfirmHypothesisAsync("");
        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task ConfirmHypothesis_UnknownId_ReturnsNotFound()
    {
        var plugin = NewPlugin();
        var result = await plugin.ConfirmHypothesisAsync("H-999");
        Assert.StartsWith("[NOT FOUND]", result);
    }

    [Fact]
    public async Task ConfirmHypothesis_Valid_UpdatesStatusAndEvidence()
    {
        var plugin = NewPlugin();
        await plugin.CreateHypothesisAsync("A hypothesis.");

        var result = await plugin.ConfirmHypothesisAsync("H-001", "confirmed via logs");

        Assert.Contains("H-001", result);
        Assert.Contains("confirmed", result);

        var log = await ReadLogAsync();
        Assert.Equal("confirmed", log.Hypotheses[0].Status);
        Assert.Equal(["confirmed via logs"], log.Hypotheses[0].Evidence);
    }

    // RecordInvestigationAsync

    [Fact]
    public async Task RecordInvestigation_EmptySummary_ReturnsError()
    {
        var plugin = NewPlugin();
        var result = await plugin.RecordInvestigationAsync("", "conclusion");
        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task RecordInvestigation_Valid_Persists()
    {
        var plugin = NewPlugin();
        var result = await plugin.RecordInvestigationAsync("Checked the retry path.", "Found an off-by-one bug.");

        Assert.Contains("Checked the retry path.", result);

        var log = await ReadLogAsync();
        Assert.Single(log.Investigations);
        Assert.Equal("Checked the retry path.", log.Investigations[0].Summary);
        Assert.Equal("Found an off-by-one bug.", log.Investigations[0].Conclusion);
    }

    // IdentifyRootCauseAsync

    [Fact]
    public async Task IdentifyRootCause_Empty_ReturnsError()
    {
        var plugin = NewPlugin();
        var result = await plugin.IdentifyRootCauseAsync("   ");
        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task IdentifyRootCause_Valid_Persists()
    {
        var plugin = NewPlugin();
        var result = await plugin.IdentifyRootCauseAsync("Off-by-one in the retry counter.");

        Assert.Contains("Identified root cause", result);
        var log = await ReadLogAsync();
        Assert.Equal(["Off-by-one in the retry counter."], log.ConfirmedRootCauses);
    }

    [Fact]
    public async Task IdentifyRootCause_Duplicate_DoesNotDuplicateEntry()
    {
        var plugin = NewPlugin();
        await plugin.IdentifyRootCauseAsync("Off-by-one bug.");

        var result = await plugin.IdentifyRootCauseAsync("off-by-one bug.");

        Assert.Contains("already recorded", result);
        var log = await ReadLogAsync();
        Assert.Single(log.ConfirmedRootCauses);
    }

    // Concurrency

    [Fact]
    public async Task ConcurrentHypothesisCreation_AssignsDistinctIds()
    {
        var plugin = NewPlugin();
        var tasks = Enumerable.Range(0, 10).Select(i => plugin.CreateHypothesisAsync($"Hypothesis {i}"));
        await Task.WhenAll(tasks);

        var log = await ReadLogAsync();
        Assert.Equal(10, log.Hypotheses.Count);
        Assert.Equal(10, log.Hypotheses.Select(h => h.Id).Distinct().Count());
    }
}
