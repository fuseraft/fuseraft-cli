using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Orchestration;

namespace FuseraftCli.Tests;

/// <summary>
/// Integration tests covering the full knowledge layer round-trip:
/// write evidence → query graph → traverse to ADR → broker assembles context →
/// validator emits claim → provenance recorded → lifecycle gc runs → nothing lost.
/// </summary>
public sealed class KnowledgeLayerRoundTripTests : IDisposable
{
    // All state lives in a per-test temp directory; nothing touches the real repo.
    private readonly string _root;
    private readonly string _src;

    private readonly AdrStore             _adrStore;
    private readonly AdrRegistry          _adrRegistry;
    private readonly RepositoryGraphStore _graphStore;
    private readonly RepositoryGraphBuilder _graphBuilder;
    private readonly ProvenanceRegistry   _provenance;
    private readonly RepositoryMemoryStore _memStore;
    private readonly ObjectiveStore       _objectiveStore;
    private readonly KnowledgeLayer       _knowledgeLayer;

    public KnowledgeLayerRoundTripTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"fuseraft_kl_{Guid.NewGuid():N}");
        _src  = Path.Combine(_root, "src");

        var stateDir       = Path.Combine(_root, ".fuseraft", "state");
        var decisionsDir   = Path.Combine(_root, ".fuseraft", "knowledge", "decisions");
        var repoMemDir     = Path.Combine(_root, ".fuseraft", "knowledge", "repository");
        var objectivesDir  = Path.Combine(_root, ".fuseraft", "knowledge", "objectives");
        var graphPath      = Path.Combine(stateDir, "repository.graph");
        var provenancePath = Path.Combine(stateDir, "provenance.json");

        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(decisionsDir);
        Directory.CreateDirectory(Path.Combine(decisionsDir, "archive"));
        Directory.CreateDirectory(repoMemDir);
        Directory.CreateDirectory(objectivesDir);

        _adrStore       = new AdrStore(decisionsDir);
        _adrRegistry    = new AdrRegistry(_adrStore);
        _graphStore     = new RepositoryGraphStore(graphPath);
        _graphBuilder   = new RepositoryGraphBuilder(_graphStore, _root);
        _provenance     = new ProvenanceRegistry(provenancePath);
        _memStore       = new RepositoryMemoryStore(repoMemDir);
        _objectiveStore = new ObjectiveStore(objectivesDir);

        _knowledgeLayer = new KnowledgeLayer(
            _adrRegistry, _graphStore, _graphBuilder, _provenance, _objectiveStore);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ── Stage 1 — Write evidence: build graph from source file ────────────────

    [Fact]
    public async Task Stage1_GraphBuilder_IndexesFileTypeAndMethod()
    {
        WriteSourceFile("MyService.cs",
            "namespace Test;\n" +
            "public class MyService\n" +
            "{\n" +
            "    public void Run() { }\n" +
            "}\n");

        await _graphBuilder.BuildAllAsync(_src);

        var graph = await _graphStore.LoadAsync();
        Assert.NotNull(graph.FindById("file:MyService.cs"));
        Assert.Contains(graph.Nodes, n => n.Kind == NodeType.Type   && n.Name == "MyService");
        Assert.Contains(graph.Nodes, n => n.Kind == NodeType.Method && n.Name == "Run");
    }

    // ── Stage 2 — Create ADR → upsert as graph node ──────────────────────────

    [Fact]
    public async Task Stage2_RecordDecision_AddsAdrNodeToGraph()
    {
        WriteSourceFile("MyService.cs",
            "namespace Test;\npublic class MyService { }\n");
        await _graphBuilder.BuildAllAsync(_src);

        var adrId = _adrStore.NextId();
        await _knowledgeLayer.RecordDecisionAsync(new AdrEntry
        {
            Id       = adrId,
            Title    = "Single-responsibility service classes",
            Status   = "Accepted",
            Decision = "Each service class does exactly one thing.",
            Governs  = ["file:MyService.cs"],
        });

        var graph   = await _graphStore.LoadAsync();
        var adrNode = graph.FindById($"adr:{adrId}");
        Assert.NotNull(adrNode);
        Assert.Equal(NodeType.Adr, adrNode!.Kind);
    }

    // ── Stage 3 — Query graph: traverse adr_governs edges to ADR ─────────────

    [Fact]
    public async Task Stage3_GraphTraversal_FindsAdrGoverningFile()
    {
        WriteSourceFile("MyService.cs",
            "namespace Test;\npublic class MyService { }\n");
        await _graphBuilder.BuildAllAsync(_src);

        var adrId = _adrStore.NextId();
        await _knowledgeLayer.RecordDecisionAsync(new AdrEntry
        {
            Id      = adrId,
            Title   = "Service design constraint",
            Status  = "Accepted",
            Governs = ["file:MyService.cs"],
        });

        var graph          = await _graphStore.LoadAsync();
        var governingEdges = graph.EdgesTo("file:MyService.cs", EdgeType.AdrGoverns).ToList();

        Assert.Single(governingEdges);
        Assert.Equal($"adr:{adrId}", governingEdges[0].From);
    }

    // ── Stage 4 — IKnowledgeLayer.SearchAsync returns ADR by keyword ─────────

    [Fact]
    public async Task Stage4_KnowledgeSearch_ReturnsAdrMatchingKeyword()
    {
        var adrId = _adrStore.NextId();
        await _knowledgeLayer.RecordDecisionAsync(new AdrEntry
        {
            Id       = adrId,
            Title    = "AuthMiddleware caching strategy",
            Status   = "Accepted",
            Decision = "Cache auth tokens in Redis with 5-minute TTL.",
            Tags     = ["auth", "caching"],
        });

        // SearchAsync does a single-term substring match; search by a tag value.
        var results = (await _knowledgeLayer.SearchAsync(
            "caching", kinds: [KnowledgeKind.Decision])).ToList();

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Id == $"adr:{adrId}");
    }

    // ── Stage 5 — ContextBroker assembles context for a matching query ────────

    [Fact]
    public async Task Stage5_ContextBroker_IncludesAdrInAssembledContext()
    {
        var adrId = _adrStore.NextId();
        await _knowledgeLayer.RecordDecisionAsync(new AdrEntry
        {
            Id       = adrId,
            Title    = "AuthMiddleware session caching",
            Status   = "Accepted",
            Decision = "Cache authenticated sessions in Redis with 5-minute TTL.",
            Tags     = ["auth", "caching"],
        });

        var broker  = new ContextBroker(_knowledgeLayer, _memStore, _provenance);
        var context = await broker.ResolveAsync("auth session middleware caching");

        Assert.NotNull(context);
        Assert.Contains("AuthMiddleware session caching", context!);
        Assert.Contains("[Knowledge Broker", context);
    }

    // ── Stage 6 — Record provenance claim backed by hard evidence ────────────

    [Fact]
    public async Task Stage6_RecordClaim_ComputesVerifiedStatus()
    {
        var claim = await _knowledgeLayer.RecordClaimAsync(
            claim:      "Build passes and all tests green",
            support:    [EvidenceClass.TestResult, EvidenceClass.ExitCode],
            artifactId: "build:main");

        Assert.Equal("Verified", claim.Status);
        Assert.NotNull(claim.VerifiedAt);
        Assert.Equal("build:main", claim.ArtifactId);
    }

    // ── Stage 7 — Provenance persisted and IsValid returns true ─────────────

    [Fact]
    public async Task Stage7_PersistedClaim_IsValidReturnsTrue()
    {
        var claim = await _knowledgeLayer.RecordClaimAsync(
            claim:   "Integration test assertion held",
            support: [EvidenceClass.Validator, EvidenceClass.TestResult]);

        Assert.True(await _provenance.IsValidAsync(claim.Id));

        var loaded = await _provenance.GetByIdAsync(claim.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Verified", loaded!.Status);
    }

    // ── Stage 8 — GC runs, fresh artifacts survive ────────────────────────────

    [Fact]
    public async Task Stage8_LifecycleGc_PreservesFreshArtifacts()
    {
        // Live (Accepted) ADR — must not be archived.
        var adrId = _adrStore.NextId();
        await _adrStore.SaveAsync(new AdrEntry { Id = adrId, Title = "Live decision", Status = "Accepted" });

        // Fresh Verified claim — must not be archived or decayed.
        var claim = await _knowledgeLayer.RecordClaimAsync(
            claim:   "Fresh evidence",
            support: [EvidenceClass.TestResult, EvidenceClass.ExitCode]);

        // Connected graph node — must not be pruned as orphan.
        WriteSourceFile("Svc.cs", "namespace T;\npublic class Svc { }\n");
        await _graphBuilder.BuildAllAsync(_src);

        var policy = new LifecyclePolicy
        {
            AdrRetentionDays            = 0,
            MemoryReinforceWindowDays   = 90,
            ConfidenceDecayDays         = 30,
            OrphanedNodeGracePeriodDays = 7,
        };
        var gc     = new KnowledgeLifecycleManager(_adrStore, _memStore, _graphStore, _provenance);
        var report = await gc.RunAsync(policy, apply: true);

        Assert.DoesNotContain(adrId,    report.ArchivedDecisionIds);
        Assert.DoesNotContain(claim.Id, report.DecayedClaimIds);
        Assert.DoesNotContain(claim.Id, report.ArchivedProvenanceIds);

        Assert.NotNull(await _adrStore.LoadAsync(adrId));
        Assert.True(await _provenance.IsValidAsync(claim.Id));
    }

    // ── Full round-trip: all 8 stages in a single flow ────────────────────────

    [Fact]
    public async Task FullRoundTrip_AllStagesSucceed()
    {
        // 1. Write evidence — build graph from a source file.
        WriteSourceFile("AuthService.cs",
            "namespace Test.Auth;\n" +
            "public class AuthService { public bool Validate(string token) => true; }\n");
        await _graphBuilder.BuildAllAsync(_src);

        // 2. Query graph — file node must be present.
        var graph    = await _graphStore.LoadAsync();
        var fileNode = graph.FindById("file:AuthService.cs");
        Assert.NotNull(fileNode);

        // 3. Traverse to ADR — create ADR governing the file; find via edge traversal.
        var adrId = _adrStore.NextId();
        await _knowledgeLayer.RecordDecisionAsync(new AdrEntry
        {
            Id       = adrId,
            Title    = "Token validation must short-circuit on expiry",
            Status   = "Accepted",
            Decision = "Reject tokens whose exp claim is in the past without a database call.",
            Tags     = ["auth", "validation"],
            Governs  = ["file:AuthService.cs"],
        });

        graph = await _graphStore.LoadAsync();
        var governing = graph.EdgesTo("file:AuthService.cs", EdgeType.AdrGoverns).ToList();
        Assert.Single(governing);
        Assert.Equal($"adr:{adrId}", governing[0].From);

        // 4. Broker assembles context — ADR title must appear in output.
        var broker  = new ContextBroker(_knowledgeLayer, _memStore, _provenance);
        var context = await broker.ResolveAsync("token validation auth expiry");
        Assert.NotNull(context);
        Assert.Contains("Token validation", context!);

        // 5–6. Validator emits claim → provenance recorded with Verified status.
        var claim = await _knowledgeLayer.RecordClaimAsync(
            claim:      "Auth token validation verified by test and exit-code evidence",
            support:    [EvidenceClass.TestResult, EvidenceClass.Validator],
            artifactId: "file:AuthService.cs");
        Assert.Equal("Verified", claim.Status);

        // 7. Provenance IsValid returns true.
        Assert.True(await _provenance.IsValidAsync(claim.Id));

        // 8. Lifecycle GC runs — nothing is lost.
        var gc     = new KnowledgeLifecycleManager(_adrStore, _memStore, _graphStore, _provenance);
        var report = await gc.RunAsync(new LifecyclePolicy
        {
            AdrRetentionDays            = 0,
            ConfidenceDecayDays         = 30,
            MemoryReinforceWindowDays   = 90,
            OrphanedNodeGracePeriodDays = 7,
        }, apply: true);

        Assert.DoesNotContain(adrId,    report.ArchivedDecisionIds);
        Assert.DoesNotContain(claim.Id, report.ArchivedProvenanceIds);
        Assert.DoesNotContain(claim.Id, report.DecayedClaimIds);

        Assert.NotNull(await _adrStore.LoadAsync(adrId));
        Assert.True(await _provenance.IsValidAsync(claim.Id));
        Assert.NotNull((await _graphStore.LoadAsync()).FindById("file:AuthService.cs"));
    }

    // ── GC correctness: stale artifacts are archived/demoted ─────────────────

    [Fact]
    public async Task LifecycleGc_ArchivesSupersededAdr_LeavingItInArchive()
    {
        var adrId = _adrStore.NextId();
        await _adrStore.SaveAsync(new AdrEntry
        {
            Id     = adrId,
            Title  = "Old caching approach",
            Status = "Superseded",
        });

        var gc     = new KnowledgeLifecycleManager(_adrStore, _memStore, _graphStore, _provenance);
        var report = await gc.RunAsync(new LifecyclePolicy { AdrRetentionDays = 0 }, apply: true);

        Assert.Contains(adrId, report.ArchivedDecisionIds);
        Assert.Null(await _adrStore.LoadAsync(adrId));                  // gone from active
        Assert.Contains(await _adrStore.LoadArchivedAsync(), e => e.Id == adrId); // preserved in archive
    }

    [Fact]
    public async Task LifecycleGc_DemotesStaleMem_KeepsFreshMem()
    {
        var staleId = Guid.NewGuid().ToString("N");
        var freshId = Guid.NewGuid().ToString("N");

        await _memStore.SaveAsync(new RepositoryMemoryEntry
        {
            Id               = staleId,
            Pattern          = "Always use async for I/O operations",
            Status           = "Approved",
            Confidence       = "Verified",
            LastReinforcedAt = DateTimeOffset.UtcNow.AddDays(-100),
        });
        await _memStore.SaveAsync(new RepositoryMemoryEntry
        {
            Id               = freshId,
            Pattern          = "Use guard clauses at method entry points",
            Status           = "Approved",
            Confidence       = "Verified",
            LastReinforcedAt = DateTimeOffset.UtcNow.AddDays(-1),
        });

        var gc     = new KnowledgeLifecycleManager(_adrStore, _memStore, _graphStore, _provenance);
        var report = await gc.RunAsync(
            // MemoryCandidatePruningDays defaults to 180 — keep the stale entry at
            // -100 days so it crosses the demotion window (90d) but not the pruning
            // window (180d), ensuring we test demotion without triggering deletion.
            new LifecyclePolicy { MemoryReinforceWindowDays = 90 }, apply: true);

        Assert.Contains(staleId,    report.DemotedMemoryIds);
        Assert.DoesNotContain(freshId, report.DemotedMemoryIds);

        var all = await _memStore.LoadAllAsync();
        Assert.Equal("Candidate", all.First(e => e.Id == staleId).Status);
        Assert.Equal("Approved",  all.First(e => e.Id == freshId).Status);
    }

    [Fact]
    public async Task LifecycleGc_ArchivesExpiredClaim_PreservesValidClaim()
    {
        // Record a claim that has already expired.
        var expiredClaim = await _provenance.RecordAsync(new ClaimRecord
        {
            Claim     = "Old build passed",
            Support   = [EvidenceClass.TestResult, EvidenceClass.ExitCode],
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1),
        });

        // Record a fresh claim with no expiry.
        var validClaim = await _provenance.RecordAsync(new ClaimRecord
        {
            Claim   = "Current build passes",
            Support = [EvidenceClass.TestResult, EvidenceClass.ExitCode],
        });

        var gc     = new KnowledgeLifecycleManager(_adrStore, _memStore, _graphStore, _provenance);
        var report = await gc.RunAsync(
            new LifecyclePolicy { MaxProvenanceAgeDays = 0 }, apply: true);

        Assert.Contains(expiredClaim.Id,    report.ArchivedProvenanceIds);
        Assert.DoesNotContain(validClaim.Id, report.ArchivedProvenanceIds);

        // Expired claim removed from active store → no longer valid.
        Assert.Null(await _provenance.GetByIdAsync(expiredClaim.Id));

        // Valid claim survives.
        Assert.True(await _provenance.IsValidAsync(validClaim.Id));
    }

    [Fact]
    public async Task ConfidenceComputer_SupportCompositionDeterminesStatus()
    {
        // Two hard-evidence sources → Verified.
        Assert.Equal("Verified", ConfidenceComputer.Compute(
            [EvidenceClass.TestResult, EvidenceClass.ExitCode]));

        // Single hard-evidence source → Inferred.
        Assert.Equal("Inferred", ConfidenceComputer.Compute(
            [EvidenceClass.Validator]));

        // ADR evidence → Inferred.
        Assert.Equal("Inferred", ConfidenceComputer.Compute(
            [EvidenceClass.ADR]));

        // AgentAssertion only → Assumed.
        Assert.Equal("Assumed", ConfidenceComputer.Compute(
            [EvidenceClass.AgentAssertion]));

        // No support → Guessed.
        Assert.Equal("Guessed", ConfidenceComputer.Compute([]));
    }

    [Fact]
    public async Task LifecycleGc_DryRun_WritesNothing()
    {
        var adrId = _adrStore.NextId();
        await _adrStore.SaveAsync(new AdrEntry { Id = adrId, Title = "Old", Status = "Superseded" });

        var gc     = new KnowledgeLifecycleManager(_adrStore, _memStore, _graphStore, _provenance);
        var report = await gc.RunAsync(new LifecyclePolicy { AdrRetentionDays = 0 }, apply: false);

        // Dry-run reports what would happen...
        Assert.Contains(adrId, report.ArchivedDecisionIds);

        // ...but nothing was actually changed.
        Assert.NotNull(await _adrStore.LoadAsync(adrId));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string WriteSourceFile(string name, string content)
    {
        var path = Path.Combine(_src, name);
        File.WriteAllText(path, content);
        return path;
    }
}
