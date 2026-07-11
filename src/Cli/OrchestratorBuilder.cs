using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentGovernance;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentGovernance.Audit;
using AgentGovernance.Sre;
using AgentGovernance.Trust;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.KeyStore;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration;
using fuseraft.Orchestration.Saga;
using fuseraft.Orchestration.Strategies;

namespace fuseraft.Cli;

/// <summary>
/// The product of <see cref="OrchestratorBuilder.BuildAsync"/>: the ready-to-run orchestrator
/// together with all runtime components the session runner needs.
/// </summary>
public sealed record OrchestratorBuildResult(
    IOrchestrator                Orchestrator,
    OrchestrationConfig          Config,
    McpSessionManager            McpManager,
    ConversationCompactor?       Compactor,
    ChangeTracker?               ChangeTracker,
    EventEmitter?                EventEmitter,
    GovernanceKernel             GovernanceKernel,
    SkillCurator?                SkillCurator,
    RepositoryMemoryExtractor?   RepositoryMemoryExtractor,
    ChatClientFactory            ChatClientFactory,
    fuseraft.Orchestration.DependencyPlanner? DependencyPlanner = null,
    fuseraft.Cli.Telemetry.SessionMetrics?    SessionMetrics    = null);

/// <summary>
/// Which orchestrator kind <c>Selection.Type</c> resolved to, bundled so
/// <c>ValidateAndSelectStrategy</c> and <c>CreateOrchestrator</c> share one instance instead
/// of each taking the same 6-7 bools as separate positional parameters.
/// </summary>
internal sealed record OrchestratorKindFlags(
    bool HitlMode,
    bool UseMagentic,
    bool UseGraph,
    bool UseWorkflow,
    bool UseAdversarial,
    bool UseMapReduce,
    bool UseScatterGather);

/// <summary>
/// Shared infrastructure collaborators <c>CreateOrchestrator</c> threads into
/// <c>AgentFactory</c>/<c>StrategyFactory</c> and nearly every orchestrator kind's
/// constructor. Bundled for the same reason as <see cref="OrchestratorKindFlags"/> — these
/// were 8 separate positional parameters.
/// </summary>
internal sealed record OrchestratorInfraServices(
    ILoggerFactory LoggerFactory,
    ChatClientFactory ChatClientFactory,
    PluginRegistry PluginRegistry,
    GovernanceKernel GovernanceKernel,
    ChangeTracker? ChangeTracker,
    EventEmitter? EventEmitter,
    IdentityRegistry IdentityRegistry,
    fuseraft.Infrastructure.Tools.ToolResultArtifactStore ToolArtifactStore);

/// <summary>
/// Knowledge/memory/evidence collaborators that feed <c>ContextBroker</c>/
/// <c>ContextAssembler</c>/<c>ContextAssemblyPipeline</c> construction and the default
/// <c>AgentOrchestrator</c> branch in <c>CreateOrchestrator</c>.
/// </summary>
internal sealed record OrchestratorKnowledgeServices(
    fuseraft.Infrastructure.Knowledge.KnowledgeLayer KnowledgeLayer,
    fuseraft.Infrastructure.Objectives.ObjectiveManager ObjectiveManager,
    EvidenceStore? EvidenceStore,
    fuseraft.Orchestration.DependencyPlanner? DependencyPlanner,
    MemoryManager? MemoryManager);

/// <summary>
/// Session/path identity inputs to <c>ContextAssembler</c> and the repository-memory store
/// paths in <c>CreateOrchestrator</c>.
/// </summary>
internal sealed record OrchestratorSessionPaths(
    string ProjectSlug,
    string? SessionId,
    string? ExecutionStatePath,
    string? InvestigationLogPath);

/// <summary>
/// Builds a ready-to-use <see cref="IOrchestrator"/> directly from a config file path,
/// without requiring a full DI host. Used by CLI commands that load config at runtime.
/// </summary>
public static class OrchestratorBuilder
{
    // Shared client for API-key validation probes — created once, never disposed.
    private static readonly HttpClient _validationHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Internal (not private) — shared with SystemPromptBuilder and OrchestratorConfigLoader,
    // which also deserialize brownfield JSON (ConventionProfile / agent files).
    internal static readonly JsonSerializerOptions BrownfieldJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Loads <paramref name="configPath"/>, validates it, connects to any configured MCP servers,
    /// and returns a configured orchestrator together with the active session manager.
    /// The caller is responsible for disposing <paramref name="mcpManager"/> (via <c>await using</c>).
    /// </summary>
    public static async Task<OrchestratorBuildResult> BuildAsync(
        string configPath,
        ILoggerFactory loggerFactory,
        PluginRegistry pluginRegistry,
        IHumanApprovalService? humanApprovalService = null,
        bool hitlMode = false,
        string? sessionId = null,
        string? specContent = null,
        bool noReplan = false,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Config file not found: {configPath}");

        var (config, projectSlug) = await OrchestratorConfigLoader.LoadAndExpandConfig(
            configPath, loggerFactory, sessionId, noReplan, cancellationToken);

        var (configAfterSecurity, profiles, shellApprover) = ResolveSecurityConfig(
            config, pluginRegistry, hitlMode, humanApprovalService, loggerFactory);
        config = configAfterSecurity;

        config = await SystemPromptBuilder.BuildSystemPrompt(
            config, configPath, sessionId, specContent, loggerFactory, cancellationToken);

        var infra = await InitInfrastructure(
            config, pluginRegistry, loggerFactory, sessionId, projectSlug,
            profiles, shellApprover, cancellationToken);
        config = infra.Config;

        var (governanceKernel, chatClientFactory, identityRegistry, dependencyPlanner) =
            InitGovernanceKernel(
                config, loggerFactory, configPath, projectSlug,
                pluginRegistry, infra.EventEmitter);

        bool useMagentic      = config.Selection.Type.Equals(OrchestratorTypes.Magentic,      StringComparison.OrdinalIgnoreCase);
        bool useGraph         = config.Selection.Type.Equals(OrchestratorTypes.Graph,         StringComparison.OrdinalIgnoreCase);
        bool useWorkflow      = config.Selection.Type.Equals(OrchestratorTypes.Workflow,      StringComparison.OrdinalIgnoreCase);
        bool useAdversarial   = config.Selection.Type.Equals(OrchestratorTypes.Adversarial,   StringComparison.OrdinalIgnoreCase);
        bool useMapReduce     = config.Selection.Type.Equals(OrchestratorTypes.MapReduce,     StringComparison.OrdinalIgnoreCase);
        bool useScatterGather = config.Selection.Type.Equals(OrchestratorTypes.ScatterGather, StringComparison.OrdinalIgnoreCase);
        var kindFlags = new OrchestratorKindFlags(
            hitlMode, useMagentic, useGraph, useWorkflow, useAdversarial, useMapReduce, useScatterGather);

        var (configAfterStrategy, compactor, skillCurator) = await ValidateAndSelectStrategy(
            config, loggerFactory, chatClientFactory, kindFlags,
            infra.KnowledgeLayer, infra.ObjectiveManager, infra.KnowledgeSandbox, projectSlug,
            infra.IntentLog, infra.EvidenceStore, infra.ExecutionStatePath, infra.InvestigationLogPath,
            sessionId, readCachePath: infra.ReadCachePath, cancellationToken);
        config = configAfterStrategy;

        WireSkillsAndVerifier(config, chatClientFactory, loggerFactory, compactor);

        var infraServices = new OrchestratorInfraServices(
            loggerFactory, chatClientFactory, pluginRegistry, governanceKernel,
            infra.ChangeTracker, infra.EventEmitter, identityRegistry, infra.ToolArtifactStore);
        var knowledgeServices = new OrchestratorKnowledgeServices(
            infra.KnowledgeLayer, infra.ObjectiveManager, infra.EvidenceStore,
            dependencyPlanner, MemoryManager.FromConfig(config.Memory));
        var sessionPaths = new OrchestratorSessionPaths(
            projectSlug, sessionId, infra.ExecutionStatePath, infra.InvestigationLogPath);

        var (orchestrator, repoMemoryExtractor) = CreateOrchestrator(
            config, kindFlags, infraServices, knowledgeServices, sessionPaths, humanApprovalService);

        return new OrchestratorBuildResult(orchestrator, config, infra.McpManager, compactor, infra.ChangeTracker, infra.EventEmitter, governanceKernel, skillCurator, repoMemoryExtractor, chatClientFactory, dependencyPlanner, infra.SessionMetrics);
    }

    // -------------------------------------------------------------------------
    // ResolveSecurityConfig
    // -------------------------------------------------------------------------

    private static (OrchestrationConfig Config, IReadOnlyDictionary<string, ApiProfileConfig>? Profiles, Func<string, Task<bool>>? ShellApprover) ResolveSecurityConfig(
        OrchestrationConfig config,
        PluginRegistry pluginRegistry,
        bool hitlMode,
        IHumanApprovalService? humanApprovalService,
        ILoggerFactory loggerFactory)
    {
        // Apply per-config security constraints and API profiles to the security-sensitive plugins.
        var profiles = config.ApiProfiles.Count > 0
            ? (IReadOnlyDictionary<string, ApiProfileConfig>)config.ApiProfiles
            : null;
        Func<string, Task<bool>>? shellApprover = hitlMode && humanApprovalService is not null
            ? humanApprovalService.PromptShellCommandAsync
            : null;

        pluginRegistry.Configure(config.Security, profiles, shellApprover);

        // When a filesystem sandbox is configured, resolve relative validation and
        // change-tracking paths against the sandbox root so that validators and
        // ChangeTracker agree with FileSystemPlugin on where files live — regardless
        // of the working directory from which fuseraft was invoked.
        if (config.Security?.FileSystemSandboxPath is { } rawSandbox)
        {
            var sandboxRoot = FuseraftPaths.ExpandPath(rawSandbox);

            if (config.Validation is { } v)
                config = config with
                {
                    Validation = v with
                    {
                        BriefPath      = OrchestratorConfigLoader.ResolveSandboxPath(v.BriefPath,      sandboxRoot),
                        TestReportPath = OrchestratorConfigLoader.ResolveSandboxPath(v.TestReportPath, sandboxRoot),
                        ChangeLogPath  = v.ChangeLogPath is not null ? OrchestratorConfigLoader.ResolveSandboxPath(v.ChangeLogPath, sandboxRoot) : null,
                    }
                };

            if (config.ChangeTracking is { } ct)
                config = config with
                {
                    ChangeTracking = ct with { Path = OrchestratorConfigLoader.ResolveSandboxPath(ct.Path, sandboxRoot) }
                };
        }

        // Brownfield: resolve discovery brief and convention profile paths against the
        // sandbox root when a sandbox is configured, mirroring how validation paths are treated.
        if (config.Brownfield is { } bf && config.Security?.FileSystemSandboxPath is { } bfSandbox)
        {
            var bfRoot = FuseraftPaths.ExpandPath(bfSandbox);

            config = config with
            {
                Brownfield = bf with
                {
                    DiscoveryBriefPath    = OrchestratorConfigLoader.ResolveSandboxPath(bf.DiscoveryBriefPath,    bfRoot),
                    ConventionProfilePath = OrchestratorConfigLoader.ResolveSandboxPath(bf.ConventionProfilePath, bfRoot),
                }
            };
        }

        // Brownfield: seed the change envelope from the Archaeologist's discovery brief
        // when the brief already exists on disk (written by a prior recon pass).
        // NOTE: This async work is done synchronously here via a blocking call.
        // The seeding logic is preserved exactly; the async file read runs inline.

        // Cross-validate ChangeTracking.Path and Validation.ChangeLogPath. If both are
        // configured, they must resolve to the same file.
        if (config.ChangeTracking is { } ctPathCheck && config.Validation?.ChangeLogPath is { } vlPathCheck)
        {
            var ctNorm = FuseraftPaths.ExpandPath(ctPathCheck.Path);
            var vlNorm = FuseraftPaths.ExpandPath(vlPathCheck);
            if (!string.Equals(ctNorm, vlNorm, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"ChangeTracking.Path ('{ctPathCheck.Path}') and Validation.ChangeLogPath ('{vlPathCheck}') " +
                    $"must point to the same file, but resolve to different paths:\n" +
                    $"  ChangeTracking.Path      → {ctNorm}\n" +
                    $"  Validation.ChangeLogPath → {vlNorm}\n" +
                    $"Update one of them to match the other.");
        }

        return (config, profiles, shellApprover);
    }

    // -------------------------------------------------------------------------
    // InitInfrastructure
    // -------------------------------------------------------------------------

    private sealed record InfrastructureResult(
        OrchestrationConfig Config,
        McpSessionManager McpManager,
        EventEmitter? EventEmitter,
        EvidenceStore? EvidenceStore,
        fuseraft.Infrastructure.Knowledge.KnowledgeLayer KnowledgeLayer,
        ChangeTracker? ChangeTracker,
        IntentLog? IntentLog,
        StateProjector? StateProjector,
        string? ExecutionStatePath,
        string? InvestigationLogPath,
        fuseraft.Infrastructure.Tools.ToolResultArtifactStore ToolArtifactStore,
        fuseraft.Cli.Telemetry.SessionMetrics SessionMetrics,
        fuseraft.Infrastructure.Objectives.ObjectiveManager ObjectiveManager,
        string KnowledgeSandbox,
        string? ReadCachePath);

    private static async Task<InfrastructureResult> InitInfrastructure(
        OrchestrationConfig config,
        PluginRegistry pluginRegistry,
        ILoggerFactory loggerFactory,
        string? sessionId,
        string projectSlug,
        IReadOnlyDictionary<string, ApiProfileConfig>? profiles,
        Func<string, Task<bool>>? shellApprover,
        CancellationToken cancellationToken)
    {
        // Connect to MCP servers and register their tools before building agents.
        var mcpManager = new McpSessionManager(loggerFactory);
        if (config.McpServers.Count > 0)
            await mcpManager.InitializeAsync(config.McpServers, pluginRegistry, cancellationToken);

        EventEmitter? eventEmitter = config.Events is { } evtCfg
            ? new EventEmitter(evtCfg.Path, loggerFactory.CreateLogger<EventEmitter>())
            : null;

        // Evidence graph: structured typed evidence alongside the flat change log.
        EvidenceStore? evidenceStore = null;
        if (config.EvidenceStore is { } esCfg)
            evidenceStore = new EvidenceStore(esCfg.Path, loggerFactory.CreateLogger<EvidenceStore>());

        // Knowledge layer — single shared instance for the session.
        // Wired here so the ChangeTracker (incremental graph rebuild) and ContextAssembler
        // (adr_graph traversal) share the same underlying stores instead of creating
        // independent instances that diverge mid-session.
        var knowledgeSandbox   = config.Security?.FileSystemSandboxPath is { Length: > 0 } ks
            ? FuseraftPaths.ExpandPath(ks)
            : Directory.GetCurrentDirectory();
        var knowledgeGraphPath = FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalRepositoryGraph, projectSlug);
        var objectiveStore = new fuseraft.Infrastructure.Objectives.ObjectiveStore(FuseraftPaths.LocalObjectives);
        var objectiveManager = new fuseraft.Infrastructure.Objectives.ObjectiveManager(objectiveStore);

        var knowledgeLayer = new fuseraft.Infrastructure.Knowledge.KnowledgeLayer(
            new fuseraft.Infrastructure.Knowledge.AdrRegistry(
                new fuseraft.Infrastructure.Knowledge.AdrStore(FuseraftPaths.LocalDecisions)),
            new fuseraft.Infrastructure.Repository.RepositoryGraphStore(knowledgeGraphPath),
            new fuseraft.Infrastructure.Repository.RepositoryGraphBuilder(
                new fuseraft.Infrastructure.Repository.RepositoryGraphStore(knowledgeGraphPath),
                knowledgeSandbox),
            objectiveStore: objectiveStore);
        pluginRegistry.ConfigureKnowledge(knowledgeLayer);

        // Change tracking: hook a filter into every agent kernel that records tool results.
        // Pass eventEmitter, evidenceStore, and intentLog so tracked tool calls emit flat
        // entries, typed graph nodes, and pre-execution intent records.
        StateProjector? stateProjector   = null;
        ChangeTracker? changeTracker     = null;
        IntentLog? intentLog             = null;
        string? executionStatePath       = null;
        string? investigationLogPath     = null;
        if (config.ChangeTracking is { } ctConfig)
        {
            intentLog = new IntentLog(ctConfig.ResolveIntentLogPath(), loggerFactory.CreateLogger<IntentLog>());

            var stateDir = Path.GetDirectoryName(Path.GetFullPath(ctConfig.Path))
                        ?? FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalState, projectSlug);
            executionStatePath   = Path.Combine(stateDir, "execution-state.json");
            investigationLogPath = Path.Combine(stateDir, "investigation-log.json");

            stateProjector = new StateProjector(
                executionStatePath,
                sessionId ?? string.Empty,
                loggerFactory.CreateLogger<StateProjector>());

            await stateProjector.InitializeAsync();

            changeTracker = new ChangeTracker(ctConfig.Path, eventEmitter, evidenceStore, intentLog, loggerFactory.CreateLogger<ChangeTracker>(), knowledgeLayer.GraphBuilder, stateProjector);
            pluginRegistry.Register("Changes",      () => new ChangesPlugin(ctConfig.Path));
            pluginRegistry.Register("Investigation", () => new InvestigationPlugin(investigationLogPath, sessionId ?? string.Empty, stateProjector));
        }

        // File version store: tracks monotonic write counters per file so agents can detect
        // concurrent-write conflicts via get_file_info + write_file(baseVersion: N).
        // Path is derived from the (sandbox-resolved) change-tracking path so the store
        // lands in the same .fuseraft/state directory as changes.json and intents.json.
        var versionStorePath = config.ChangeTracking is { } ct2
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ct2.Path)) ?? FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalState, projectSlug), "file_versions.json")
            : FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalFileVersions, projectSlug);
        var fileVersionStore = new fuseraft.Infrastructure.Storage.FileVersionStore(versionStorePath, loggerFactory.CreateLogger<fuseraft.Infrastructure.Storage.FileVersionStore>());

        // Session-level read cache: short-circuits cross-turn re-reads of unchanged files
        // so agents receive a "content unchanged since last read" hint instead of re-dumping
        // full file content into context every turn. Persisted to the global session dir
        // so the cache survives process restarts within the same session.
        var readCachePath = sessionId is { Length: > 0 }
            ? FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalSessionReadCache, sessionId, projectSlug)
            : null;
        var sessionReadCache = new fuseraft.Infrastructure.Context.SessionReadCache(readCachePath);

        // Tool-result artifact store: offloads tool results that exceed the size threshold
        // to disk so they never accumulate verbatim in the conversation history. Only active
        // when a session ID is known (so each session gets its own artifact subdirectory).
        var toolArtifactsDir = sessionId is { Length: > 0 }
            ? FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalSessionToolArtifacts, sessionId, projectSlug)
            : null;
        var toolArtifactStore = new fuseraft.Infrastructure.Tools.ToolResultArtifactStore(toolArtifactsDir, eventEmitter);

        // Session metrics: accumulates per-turn quality data (tokens, tool calls, cache hits,
        // patch failures) and renders a summary table at session end.
        var sessionMetrics = new fuseraft.Cli.Telemetry.SessionMetrics();

        // Re-configure the FileSystem plugin with the version store and session read cache
        // so write_file, get_file_info, and read_file participate in version-aware conflict
        // detection and cross-turn read deduplication. Thread the cache-hit callback so
        // SessionMetrics can count duplicate reads across the session.
        pluginRegistry.Configure(config.Security ?? new SecurityConfig(), profiles, shellApprover, fileVersionStore, sessionReadCache, onCacheHit: sessionMetrics.RecordCacheHit, eventSink: stateProjector);

        // Session context plugin: shared handoff notes that agents write before routing
        // and read on re-entry. Stored in the global session directory.
        var ctxSummaryPath = sessionId is { Length: > 0 }
            ? FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalSessionContext, sessionId,  projectSlug)
            : FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalSessionContext, "default", projectSlug);
        pluginRegistry.Register("SessionContext", () => new fuseraft.Infrastructure.Plugins.SessionContextPlugin(ctxSummaryPath));

        // Narrow, fixed-path artifact writers for recon/planning-style agents (brownfield's
        // Archaeologist, greenfield/swe's Preflight, every template's Planner, swe's
        // PlannerCritic) so they can be locked to FileSystem:[read] via Capabilities while
        // still persisting their own findings. One ArtifactPlugin class registered many times
        // — see ArtifactPlugin's doc comment for why each registration still gives its agent
        // exactly one, uniquely-named write function.
        var reconSessionId = sessionId is { Length: > 0 } ? sessionId : "default";
        pluginRegistry.Register("Conventions", () => new fuseraft.Infrastructure.Plugins.ArtifactPlugin(
            FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalConventions, reconSessionId, projectSlug),
            fuseraft.Infrastructure.Plugins.ArtifactFormat.Json,
            "write_file_conventions", fuseraft.Infrastructure.Plugins.ReconDescriptions.Conventions));
        pluginRegistry.Register("DiscoveryBrief", () => new fuseraft.Infrastructure.Plugins.ArtifactPlugin(
            FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalBrownfieldBrief, reconSessionId, projectSlug),
            fuseraft.Infrastructure.Plugins.ArtifactFormat.Json,
            "write_file_discovery_brief", fuseraft.Infrastructure.Plugins.ReconDescriptions.DiscoveryBrief));
        pluginRegistry.Register("Preflight", () => new fuseraft.Infrastructure.Plugins.ArtifactPlugin(
            FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalPreflight, reconSessionId, projectSlug),
            fuseraft.Infrastructure.Plugins.ArtifactFormat.Json,
            "write_file_preflight", fuseraft.Infrastructure.Plugins.ReconDescriptions.Preflight));
        pluginRegistry.Register("Brief", () => new fuseraft.Infrastructure.Plugins.ArtifactPlugin(
            FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalBrief, reconSessionId, projectSlug),
            fuseraft.Infrastructure.Plugins.ArtifactFormat.Json,
            "write_file_brief", fuseraft.Infrastructure.Plugins.ReconDescriptions.Brief));
        pluginRegistry.Register("BriefReview", () => new fuseraft.Infrastructure.Plugins.ArtifactPlugin(
            FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalBriefReview, reconSessionId, projectSlug),
            fuseraft.Infrastructure.Plugins.ArtifactFormat.Json,
            "write_file_brief_review", fuseraft.Infrastructure.Plugins.ReconDescriptions.BriefReview));

        // Brownfield: seed the change envelope from the Archaeologist's discovery brief
        // when the brief already exists on disk (written by a prior recon pass).
        if (config.Brownfield is { SeedEnvelopeFromBrief: true, DiscoveryBriefPath: { } discoveryPath }
            && File.Exists(discoveryPath))
        {
            var expandedDiscoveryPath = discoveryPath;
            try
            {
                var briefJson  = await File.ReadAllTextAsync(expandedDiscoveryPath, cancellationToken);
                var brief      = JsonSerializer.Deserialize<BrownfieldDiscoveryBrief>(briefJson, BrownfieldJsonOpts);
                var scopeFiles = brief?.InScopeFiles;
                if (scopeFiles is { Count: > 0 })
                {
                    var existing = config.Security?.ChangeEnvelope ?? [];
                    var merged   = existing.Concat(scopeFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    config = config with { Security = (config.Security ?? new SecurityConfig()) with { ChangeEnvelope = merged } };
                }
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger(nameof(OrchestratorBuilder)).LogWarning(
                    "Could not seed change envelope from brownfield brief '{Path}': {Message}",
                    expandedDiscoveryPath, ex.Message);
            }
        }

        return new InfrastructureResult(
            config, mcpManager, eventEmitter, evidenceStore, knowledgeLayer,
            changeTracker, intentLog, stateProjector, executionStatePath, investigationLogPath,
            toolArtifactStore, sessionMetrics, objectiveManager, knowledgeSandbox, readCachePath);
    }

    // -------------------------------------------------------------------------
    // InitGovernanceKernel
    // -------------------------------------------------------------------------

    private static (GovernanceKernel GovernanceKernel, ChatClientFactory ChatClientFactory, IdentityRegistry IdentityRegistry, fuseraft.Orchestration.DependencyPlanner? DependencyPlanner) InitGovernanceKernel(
        OrchestrationConfig config,
        ILoggerFactory loggerFactory,
        string configPath,
        string projectSlug,
        PluginRegistry pluginRegistry,
        EventEmitter? eventEmitter)
    {
        // Governance kernel: load default policy if one exists alongside the config file.
        var configDir         = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
        var defaultPolicyPath = Path.Combine(configDir, "policies", "default.yaml");
        var governanceKernel  = new GovernanceKernel(new GovernanceOptions
        {
            EnableAudit                    = true,
            EnableMetrics                  = true,
            EnablePromptInjectionDetection = true,
            EnableRings                    = true,
            EnableCircuitBreaker           = true,
            CircuitBreakerConfig           = new CircuitBreakerConfig
            {
                FailureThreshold  = 5,
                ResetTimeout      = TimeSpan.FromSeconds(30),
                HalfOpenMaxCalls  = 1,
            },
            PolicyPaths                    = File.Exists(defaultPolicyPath) ? [defaultPolicyPath] : [],
        });

        // SLO: track routing validator compliance over a 1-hour rolling window.
        // Target: 95% of validator checks should pass. Burn-rate alerts fire at 2× and 5×.
        governanceKernel.SloEngine.Register(new SloSpec
        {
            Name        = "policy-compliance",
            Description = "Fraction of routing validator checks that pass within the session",
            Sli         = new SliSpec
            {
                Metric     = "compliance_rate",
                Threshold  = 1.0,
                Comparison = ComparisonOp.GreaterThanOrEqual,
            },
            Target = 95.0,
            Window = TimeSpan.FromHours(1),
            ErrorBudgetPolicy = new ErrorBudgetPolicy
            {
                Thresholds =
                [
                    new BurnRateThreshold { Name = "warning",  Rate = 2.0, Severity = BurnRateSeverity.Warning,  WindowSeconds = 3600 },
                    new BurnRateThreshold { Name = "critical", Rate = 5.0, Severity = BurnRateSeverity.Critical, WindowSeconds =  600 },
                ]
            },
        });

        // Bridge ToolCallBlocked events (from sandbox + injection checks) into the JSONL log.
        // PolicyViolation is NOT bridged here — KeywordSelectionStrategy emits those directly
        // via _eventEmitter to preserve the richer per-turn context (consecutive count, etc.).
        if (eventEmitter is not null)
        {
            governanceKernel.OnEvent(GovernanceEventType.ToolCallBlocked, async evt =>
                await eventEmitter.EmitAsync(EventTypes.ToolBlocked, evt.AgentId,
                    payload: new { policy = evt.PolicyName, data = evt.Data }));
        }

        // Hash-chain audit log: subscribe to all governance events so every denial
        // and policy check is tamper-evidently recorded for the session's lifetime.
        var auditLogger = new AuditLogger();
        governanceKernel.OnAllEvents(evt =>
        {
            var action   = evt.PolicyName is not null ? $"{evt.Type}:{evt.PolicyName}" : evt.Type.ToString();
            var decision = evt.Type is GovernanceEventType.PolicyViolation
                               or GovernanceEventType.ToolCallBlocked
                               or GovernanceEventType.TrustFailed
                ? "deny" : "allow";
            auditLogger.Log(evt.AgentId, action, decision);
        });

        // Reasoning audit: each turn's reasoning block is SHA-256-hashed and appended to the
        // audit chain so the association between model thinking and actions is tamper-evident
        // without exposing the full reasoning text in the audit record.
        if (eventEmitter is not null)
        {
            eventEmitter.RegisterHook(new ReasoningAuditHook(auditLogger));
        }

        var identityRegistry  = new IdentityRegistry();
        var providerErrorLog  = config.Events is { } evtPath
            ? Path.Combine(Path.GetDirectoryName(evtPath.Path) ?? FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalLogs, projectSlug), "provider_errors.jsonl")
            : FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalProviderErrors, projectSlug);
        var chatClientFactory = new ChatClientFactory(config.Models.Count > 0 ? config.Models : null, providerErrorLog, eventEmitter, loggerFactory);

        // Eagerly resolve every agent's model config so that undefined aliases
        // (e.g. "fast" not declared in the Models registry) fail here at startup
        // rather than mid-session when the agent is first invoked.
        foreach (var agent in config.Agents)
        {
            try { chatClientFactory.Resolve(agent.Model); }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"Agent '{agent.Name}' has an unresolvable model: {ex.Message}", ex);
            }
        }
        if (config.Selection.Model is { } selectionModel)
        {
            try { chatClientFactory.Resolve(selectionModel); }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"Selection strategy has an unresolvable model: {ex.Message}", ex);
            }
        }

        // Dependency planner: validate Produces/Requires graph and detect cycles at startup.
        // Active only when at least one agent declares a dependency token.
        fuseraft.Orchestration.DependencyPlanner? dependencyPlanner = null;
        if (config.Agents.Any(a => a.Produces.Count > 0 || a.Requires.Count > 0))
        {
            // Constructor throws InvalidOperationException on cycles.
            dependencyPlanner = new fuseraft.Orchestration.DependencyPlanner(config.Agents);

            if (dependencyPlanner.ExecutionLayers.Count > 0)
            {
                var layerSummary = string.Join(" → ",
                    dependencyPlanner.ExecutionLayers.Select(layer => $"[{string.Join(", ", layer)}]"));
                loggerFactory.CreateLogger(nameof(OrchestratorBuilder)).LogInformation(
                    "DependencyPlanner active — {LayerCount} layer(s): {Layers}",
                    dependencyPlanner.ExecutionLayers.Count, layerSummary);
            }
        }

        return (governanceKernel, chatClientFactory, identityRegistry, dependencyPlanner);
    }

    // -------------------------------------------------------------------------
    // ValidateAndSelectStrategy
    // -------------------------------------------------------------------------

    private static async Task<(OrchestrationConfig Config, ConversationCompactor? Compactor, SkillCurator? SkillCurator)> ValidateAndSelectStrategy(
        OrchestrationConfig config,
        ILoggerFactory loggerFactory,
        ChatClientFactory chatClientFactory,
        OrchestratorKindFlags flags,
        fuseraft.Infrastructure.Knowledge.KnowledgeLayer knowledgeLayer,
        fuseraft.Infrastructure.Objectives.ObjectiveManager objectiveManager,
        string knowledgeSandbox,
        string projectSlug,
        IntentLog? intentLog,
        EvidenceStore? evidenceStore,
        string? executionStatePath,
        string? investigationLogPath,
        string? sessionId,
        string? readCachePath,
        CancellationToken cancellationToken)
    {
        var goLogger = loggerFactory.CreateLogger<GraphOrchestrator>();

        // Eagerly validate the adversarial config when that strategy is selected.
        if (config.Selection.Type.Equals(OrchestratorTypes.Adversarial, StringComparison.OrdinalIgnoreCase))
        {
            if (config.Selection.Adversarial is null)
                throw new InvalidOperationException(
                    "Selection.Type 'adversarial' requires a 'Selection.Adversarial' configuration block.");

            var advCfg = config.Selection.Adversarial;

            if (advCfg.Stages.Count == 0)
                throw new InvalidOperationException(
                    "Selection.Adversarial.Stages must contain at least one stage.");

            if (advCfg.Rounds < 1)
                throw new InvalidOperationException(
                    $"Selection.Adversarial.Rounds must be at least 1 (got {advCfg.Rounds}).");

            if (string.IsNullOrWhiteSpace(advCfg.PassKeyword))
                throw new InvalidOperationException(
                    "Selection.Adversarial.PassKeyword must be a non-empty string.");

            var agentNames = config.Agents.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (int si = 0; si < advCfg.Stages.Count; si++)
            {
                var stage = advCfg.Stages[si];
                if (string.IsNullOrWhiteSpace(stage.Generator))
                    throw new InvalidOperationException(
                        $"Selection.Adversarial.Stages[{si}]: Generator must be a non-empty agent name.");
                if (!agentNames.Contains(stage.Generator))
                    throw new InvalidOperationException(
                        $"Selection.Adversarial.Stages[{si}].Generator '{stage.Generator}' " +
                        "is not defined in 'Orchestration.Agents'.");
                if (string.IsNullOrWhiteSpace(stage.Critic))
                    throw new InvalidOperationException(
                        $"Selection.Adversarial.Stages[{si}]: Critic must be a non-empty agent name.");
                if (!agentNames.Contains(stage.Critic))
                    throw new InvalidOperationException(
                        $"Selection.Adversarial.Stages[{si}].Critic '{stage.Critic}' " +
                        "is not defined in 'Orchestration.Agents'.");
            }
        }

        // Warn when Selection.Adversarial is configured but Selection.Type is not "adversarial".
        if (config.Selection.Adversarial is not null &&
            !config.Selection.Type.Equals(OrchestratorTypes.Adversarial, StringComparison.OrdinalIgnoreCase))
            loggerFactory.CreateLogger(nameof(OrchestratorBuilder)).LogWarning(
                "Selection.Adversarial is configured but Selection.Type is '{Type}', not 'adversarial'. " +
                "The Adversarial block will be ignored. Set Selection.Type: adversarial to enable it.",
                config.Selection.Type);

        // Eagerly validate the Magentic manager model and loop-counter config when that strategy is selected.
        if (config.Selection.Type.Equals(OrchestratorTypes.Magentic, StringComparison.OrdinalIgnoreCase))
        {
            if (config.Selection.Magentic?.Model is null)
                throw new InvalidOperationException(
                    "Selection.Type 'magentic' requires a 'Selection.Magentic.Model' configuration block.");

            try { chatClientFactory.Resolve(config.Selection.Magentic.Model); }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"Magentic manager has an unresolvable model: {ex.Message}", ex);
            }

            var mag = config.Selection.Magentic;
            if (mag.MaxRoundCount < 1)
                throw new InvalidOperationException(
                    $"Selection.Magentic.MaxRoundCount must be at least 1 (got {mag.MaxRoundCount}). " +
                    "A value of 0 would exit immediately without invoking any participant agents.");
            if (mag.MaxStallCount < 1)
                throw new InvalidOperationException(
                    $"Selection.Magentic.MaxStallCount must be at least 1 (got {mag.MaxStallCount}). " +
                    "A value of 0 would trigger a replan on every single round.");

            if (mag.MaxResetCount < 0)
                throw new InvalidOperationException(
                    $"Selection.Magentic.MaxResetCount must be >= 0 (got {mag.MaxResetCount}). " +
                    "Use 0 to disable replanning entirely.");

            // Warn when a Termination section is configured alongside the magentic type — it is
            // silently ignored and users may not realise termination is driven by MaxRoundCount,
            // MaxStallCount, and MaxResetCount in the Magentic block instead.
            var t = config.Termination;
            bool hasNonDefaultTermination = t is not null && (
                !t.Type.Equals("composite", StringComparison.OrdinalIgnoreCase) ||
                t.Pattern    is not null ||
                t.Strategies is { Count: > 0 });
            if (hasNonDefaultTermination)
                loggerFactory.CreateLogger(nameof(OrchestratorBuilder)).LogWarning(
                    "The 'Termination' section is ignored for Selection.Type 'magentic'. " +
                    "Session termination is controlled by MaxRoundCount, MaxStallCount, and MaxResetCount " +
                    "in the 'Selection.Magentic' block.");
        }

        // Warn when Selection.Magentic is configured but Selection.Type is not "magentic" —
        // the Magentic block would be silently ignored and the session would run as sequential.
        if (config.Selection.Magentic is not null &&
            !config.Selection.Type.Equals(OrchestratorTypes.Magentic, StringComparison.OrdinalIgnoreCase))
            loggerFactory.CreateLogger(nameof(OrchestratorBuilder)).LogWarning(
                "Selection.Magentic is configured but Selection.Type is '{Type}', not 'magentic'. " +
                "The Magentic block will be ignored. Set Selection.Type: magentic to enable it.",
                config.Selection.Type);

        // Warn when Selection.Graph is configured but Selection.Type is neither "graph" nor
        // "workflow" (both consume the same Selection.Graph block) — it would be silently
        // ignored and the session would run as sequential.
        if (config.Selection.Graph is not null &&
            !config.Selection.Type.Equals(OrchestratorTypes.Graph, StringComparison.OrdinalIgnoreCase) &&
            !config.Selection.Type.Equals(OrchestratorTypes.Workflow, StringComparison.OrdinalIgnoreCase))
            loggerFactory.CreateLogger(nameof(OrchestratorBuilder)).LogWarning(
                "Selection.Graph is configured but Selection.Type is '{Type}', not 'graph' or 'workflow'. " +
                "The Graph block will be ignored. Set Selection.Type: graph or workflow to enable it.",
                config.Selection.Type);

        // Validate verifier config: the named agent must exist in the agent pool.
        if (config.Verifier is { AgentName: { Length: > 0 } verifierAgentName })
        {
            var agentNameSet = config.Agents.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!agentNameSet.Contains(verifierAgentName))
                throw new InvalidOperationException(
                    $"Verifier.AgentName '{verifierAgentName}' is not defined in 'Orchestration.Agents'. " +
                    $"Add an agent with that name or correct the verifier configuration.");
        }

        // Validate state machine config at startup when that strategy is selected.
        if (config.Selection.Type.Equals(OrchestratorTypes.StateMachine, StringComparison.OrdinalIgnoreCase))
        {
            if (config.Selection.StateMachine is null)
                throw new InvalidOperationException(
                    "Selection.Type 'statemachine' requires a 'Selection.StateMachine' configuration block.");

            // Eagerly validate that every agent referenced in state machine states exists.
            var agentNames = config.Agents.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (stateName, state) in config.Selection.StateMachine.States)
            {
                if (!string.IsNullOrWhiteSpace(state.Agent) && !agentNames.Contains(state.Agent))
                    throw new InvalidOperationException(
                        $"State machine state '{stateName}' references agent '{state.Agent}' " +
                        $"which is not defined in 'Orchestration.Agents'.");
            }
        }

        // For state-machine configs with an active StateProjector, prepend execution_state
        // and investigation_log as the first context sources for every agent that does not
        // already declare them. This ensures build failures, compiler errors, failed attempts,
        // and rejected investigation paths survive compaction and are visible to every agent
        // on every turn, regardless of token pressure.
        if (config.Selection.Type.Equals(OrchestratorTypes.StateMachine, StringComparison.OrdinalIgnoreCase)
            && executionStatePath is not null)
        {
            static string SourceType(string s)
            {
                var i = s.IndexOf(':');
                return i < 0 ? s.Trim().ToLowerInvariant() : s[..i].Trim().ToLowerInvariant();
            }

            var execStateSrc = new ContextSource { Source = "execution_state" };
            var invLogSrc    = investigationLogPath is not null
                ? new ContextSource { Source = "investigation_log" }
                : (ContextSource?)null;

            config = config with
            {
                Agents = config.Agents.Select(a =>
                {
                    if (a.SkipExecutionState) return a;

                    if (a.Context is { Count: > 0 } existing)
                    {
                        var needsExecState = !existing.Any(s => SourceType(s.Source) == "execution_state");
                        var needsInvLog    = invLogSrc is not null && !existing.Any(s => SourceType(s.Source) == "investigation_log");

                        if (!needsExecState && !needsInvLog) return a;

                        var toPrepend = new List<ContextSource>();
                        if (needsExecState) toPrepend.Add(execStateSrc);
                        if (needsInvLog)    toPrepend.Add(invLogSrc!);
                        return a with { Context = [.. toPrepend, .. existing] };
                    }

                    // No context spec → inject a default that substitutes for shared-history replay:
                    // execution state + investigation log (ground truth) + own recent turns + handoff notes.
                    var defaultSources = new List<ContextSource> { execStateSrc };
                    if (invLogSrc is not null) defaultSources.Add(invLogSrc);
                    defaultSources.Add(new ContextSource { Source = "own_history:10" });
                    defaultSources.Add(new ContextSource { Source = "session_context" });
                    return a with { Context = defaultSources };
                }).ToList()
            };
        }

        // Validate map-reduce config at startup when that strategy is selected.
        if (flags.UseMapReduce)
        {
            if (config.Selection.MapReduce is null)
                throw new InvalidOperationException(
                    "Selection.Type 'mapreduce' requires a 'Selection.MapReduce' configuration block.");

            var mr        = config.Selection.MapReduce;
            var mrAgents  = config.Agents.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(mr.Splitter))
                throw new InvalidOperationException("Selection.MapReduce.Splitter must be a non-empty agent name.");
            if (!mrAgents.Contains(mr.Splitter))
                throw new InvalidOperationException(
                    $"Selection.MapReduce.Splitter '{mr.Splitter}' is not defined in 'Orchestration.Agents'.");

            if (string.IsNullOrWhiteSpace(mr.Mapper))
                throw new InvalidOperationException("Selection.MapReduce.Mapper must be a non-empty agent name.");
            if (!mrAgents.Contains(mr.Mapper))
                throw new InvalidOperationException(
                    $"Selection.MapReduce.Mapper '{mr.Mapper}' is not defined in 'Orchestration.Agents'.");

            if (string.IsNullOrWhiteSpace(mr.Reducer))
                throw new InvalidOperationException("Selection.MapReduce.Reducer must be a non-empty agent name.");
            if (!mrAgents.Contains(mr.Reducer))
                throw new InvalidOperationException(
                    $"Selection.MapReduce.Reducer '{mr.Reducer}' is not defined in 'Orchestration.Agents'.");

            if (mr.MaxConcurrency < 0)
                throw new InvalidOperationException(
                    $"Selection.MapReduce.MaxConcurrency must be >= 0 (got {mr.MaxConcurrency}). Use 0 for unlimited.");

            if (mr.MaxSplitterRetries < 1)
                throw new InvalidOperationException(
                    $"Selection.MapReduce.MaxSplitterRetries must be at least 1 (got {mr.MaxSplitterRetries}).");

            if (string.IsNullOrWhiteSpace(mr.ItemsJsonPath))
                throw new InvalidOperationException("Selection.MapReduce.ItemsJsonPath must be a non-empty string.");
        }

        // Warn when Selection.MapReduce is configured but Selection.Type is not "mapreduce".
        if (config.Selection.MapReduce is not null &&
            !config.Selection.Type.Equals(OrchestratorTypes.MapReduce, StringComparison.OrdinalIgnoreCase))
            loggerFactory.CreateLogger(nameof(OrchestratorBuilder)).LogWarning(
                "Selection.MapReduce is configured but Selection.Type is '{Type}', not 'mapreduce'. " +
                "The MapReduce block will be ignored. Set Selection.Type: mapreduce to enable it.",
                config.Selection.Type);

        if (flags.UseScatterGather)
        {
            if (config.Selection.ScatterGather is null)
                throw new InvalidOperationException(
                    "Selection.Type 'scattergather' requires a 'Selection.ScatterGather' configuration block.");

            var sg        = config.Selection.ScatterGather;
            var sgAgents  = config.Agents.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (sg.Participants.Count == 0)
                throw new InvalidOperationException("Selection.ScatterGather.Participants must contain at least one agent name.");

            foreach (var p in sg.Participants)
            {
                if (string.IsNullOrWhiteSpace(p) || !sgAgents.Contains(p))
                    throw new InvalidOperationException(
                        $"Selection.ScatterGather.Participants contains '{p}' which is not defined in 'Orchestration.Agents'.");
            }

            if (string.IsNullOrWhiteSpace(sg.Synthesizer))
                throw new InvalidOperationException("Selection.ScatterGather.Synthesizer must be a non-empty agent name.");
            if (!sgAgents.Contains(sg.Synthesizer))
                throw new InvalidOperationException(
                    $"Selection.ScatterGather.Synthesizer '{sg.Synthesizer}' is not defined in 'Orchestration.Agents'.");
            if (sg.MaxConcurrency < 0)
                throw new InvalidOperationException(
                    $"Selection.ScatterGather.MaxConcurrency must be >= 0 (got {sg.MaxConcurrency}). Use 0 for unlimited.");
        }

        // Warn when Selection.ScatterGather is configured but Selection.Type is not "scattergather".
        if (config.Selection.ScatterGather is not null &&
            !config.Selection.Type.Equals(OrchestratorTypes.ScatterGather, StringComparison.OrdinalIgnoreCase))
            loggerFactory.CreateLogger(nameof(OrchestratorBuilder)).LogWarning(
                "Selection.ScatterGather is configured but Selection.Type is '{Type}', not 'scattergather'. " +
                "The ScatterGather block will be ignored. Set Selection.Type: scattergather to enable it.",
                config.Selection.Type);

        // Validate graph config at startup when the graph strategy is selected.
        if (flags.UseGraph)
        {
            if (config.Selection.Graph is null)
                throw new InvalidOperationException(
                    "Selection.Type 'graph' requires a 'Selection.Graph' configuration block.");

            var graphCfg   = config.Selection.Graph;
            var agentNames = config.Agents.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nodeIds    = graphCfg.Nodes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (graphCfg.Nodes.Count == 0)
                throw new InvalidOperationException(
                    "Selection.Graph.Nodes must contain at least one node.");

            // Validate node agent references and uniqueness.
            var seenNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in graphCfg.Nodes)
            {
                if (string.IsNullOrWhiteSpace(node.Id))
                    throw new InvalidOperationException(
                        "Every node in Selection.Graph.Nodes must have a non-empty 'Id'.");
                if (!seenNodeIds.Add(node.Id))
                    throw new InvalidOperationException(
                        $"Duplicate node Id '{node.Id}' found in Selection.Graph.Nodes. Node Ids must be unique.");

                bool isSubGraphNode = !string.IsNullOrWhiteSpace(node.SubGraphId);

                if (isSubGraphNode)
                {
                    if (!string.IsNullOrWhiteSpace(node.Agent))
                        throw new InvalidOperationException(
                            $"Graph node '{node.Id}' has both 'Agent' and 'SubGraphId' set. " +
                            $"Use one or the other — leave 'Agent' empty when using 'SubGraphId'.");

                    if (graphCfg.SubGraphs is null || !graphCfg.SubGraphs.TryGetValue(node.SubGraphId!, out var subSpec))
                        throw new InvalidOperationException(
                            $"Graph node '{node.Id}' references SubGraphId '{node.SubGraphId}' " +
                            $"which is not defined in Selection.Graph.SubGraphs.");

                    if (!subSpec.IsValid)
                        throw new InvalidOperationException(
                            $"SubGraph '{node.SubGraphId}' must set exactly one of 'Graph', 'MapReduce', or 'ScatterGather'.");

                    if (subSpec.IsMapReduce)
                    {
                        var mr = subSpec.MapReduce!;
                        if (string.IsNullOrWhiteSpace(mr.Splitter) || !agentNames.Contains(mr.Splitter))
                            throw new InvalidOperationException(
                                $"SubGraph '{node.SubGraphId}' MapReduce.Splitter '{mr.Splitter}' is not defined in 'Orchestration.Agents'.");
                        if (string.IsNullOrWhiteSpace(mr.Mapper) || !agentNames.Contains(mr.Mapper))
                            throw new InvalidOperationException(
                                $"SubGraph '{node.SubGraphId}' MapReduce.Mapper '{mr.Mapper}' is not defined in 'Orchestration.Agents'.");
                        if (string.IsNullOrWhiteSpace(mr.Reducer) || !agentNames.Contains(mr.Reducer))
                            throw new InvalidOperationException(
                                $"SubGraph '{node.SubGraphId}' MapReduce.Reducer '{mr.Reducer}' is not defined in 'Orchestration.Agents'.");
                        if (mr.MaxConcurrency < 0)
                            throw new InvalidOperationException(
                                $"SubGraph '{node.SubGraphId}' MapReduce.MaxConcurrency must be >= 0 (got {mr.MaxConcurrency}).");
                        if (mr.MaxSplitterRetries < 1)
                            throw new InvalidOperationException(
                                $"SubGraph '{node.SubGraphId}' MapReduce.MaxSplitterRetries must be at least 1 (got {mr.MaxSplitterRetries}).");
                        if (string.IsNullOrWhiteSpace(mr.ItemsJsonPath))
                            throw new InvalidOperationException(
                                $"SubGraph '{node.SubGraphId}' MapReduce.ItemsJsonPath must be a non-empty string.");
                    }
                    else if (subSpec.IsScatterGather)
                    {
                        var sg = subSpec.ScatterGather!;
                        if (sg.Participants.Count == 0)
                            throw new InvalidOperationException(
                                $"SubGraph '{node.SubGraphId}' ScatterGather.Participants must contain at least one agent name.");
                        foreach (var p in sg.Participants)
                        {
                            if (string.IsNullOrWhiteSpace(p) || !agentNames.Contains(p))
                                throw new InvalidOperationException(
                                    $"SubGraph '{node.SubGraphId}' ScatterGather.Participants contains '{p}' which is not defined in 'Orchestration.Agents'.");
                        }
                        if (string.IsNullOrWhiteSpace(sg.Synthesizer) || !agentNames.Contains(sg.Synthesizer))
                            throw new InvalidOperationException(
                                $"SubGraph '{node.SubGraphId}' ScatterGather.Synthesizer '{sg.Synthesizer}' is not defined in 'Orchestration.Agents'.");
                        if (sg.MaxConcurrency < 0)
                            throw new InvalidOperationException(
                                $"SubGraph '{node.SubGraphId}' ScatterGather.MaxConcurrency must be >= 0 (got {sg.MaxConcurrency}).");
                    }
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(node.Agent))
                        throw new InvalidOperationException(
                            $"Graph node '{node.Id}' must specify an 'Agent' name (or set 'SubGraphId' for a sub-graph node).");
                    if (!agentNames.Contains(node.Agent))
                        throw new InvalidOperationException(
                            $"Graph node '{node.Id}' references agent '{node.Agent}' " +
                            $"which is not defined in 'Orchestration.Agents'.");
                }
            }

            // Validate edge node references.
            foreach (var edge in graphCfg.Edges)
            {
                if (!nodeIds.Contains(edge.From))
                    throw new InvalidOperationException(
                        $"Graph edge From='{edge.From}' does not match any node Id in Selection.Graph.Nodes.");
                if (!nodeIds.Contains(edge.To))
                    throw new InvalidOperationException(
                        $"Graph edge To='{edge.To}' does not match any node Id in Selection.Graph.Nodes.");
            }

            // Validate entry node when explicitly set.
            if (!string.IsNullOrWhiteSpace(graphCfg.EntryNode) && !nodeIds.Contains(graphCfg.EntryNode))
                throw new InvalidOperationException(
                    $"Selection.Graph.EntryNode '{graphCfg.EntryNode}' does not match any node Id in Selection.Graph.Nodes.");

            // GAP-5: Warn about no-keyword edges mixed with keyword edges on the same source node.
            // When a node has both keyword and no-keyword edges the no-keyword edge is silently ignored
            // at runtime because the unconditional routing path only activates for all-no-keyword nodes.
            foreach (var edge in graphCfg.Edges.Where(e => string.IsNullOrEmpty(e.Keyword)))
            {
                bool hasOtherKeywordEdges = graphCfg.Edges.Any(e =>
                    string.Equals(e.From, edge.From, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(e.Keyword));
                if (hasOtherKeywordEdges)
                    goLogger.LogWarning(
                        "[GraphOrchestrator] Node '{From}' has a no-keyword edge to '{To}' alongside " +
                        "keyword edges — the no-keyword edge will be ignored at runtime. " +
                        "Add a keyword to use it, or remove all keyword edges to use unconditional routing.",
                        edge.From, edge.To);
            }

            // GAP-6: Warn when the same keyword appears on both a forward and a back-edge from the
            // same source node. The back-edge takes priority at runtime (PhaseBreakKeywords is checked
            // before Routes), so the forward route for that keyword will never fire.
            // NOTE: Uses node list index as a BFS-layer proxy. This is accurate when nodes are listed
            // in topological order (entry node first). If the list order differs from BFS discovery
            // order, the warning may produce a false positive or miss a real ambiguity.
            var nodeIndexMap = graphCfg.Nodes
                .Select((n, i) => (n.Id, i))
                .ToDictionary(x => x.Id, x => x.i, StringComparer.OrdinalIgnoreCase);

            var keywordEdgeGroups = graphCfg.Edges
                .Where(e => !string.IsNullOrEmpty(e.Keyword))
                .GroupBy(e => (From: e.From, Keyword: e.Keyword!), (k, _) => k,
                    EqualityComparer<(string From, string Keyword)>.Create(
                        (a, b) => string.Equals(a.From, b.From, StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(a.Keyword, b.Keyword, StringComparison.OrdinalIgnoreCase),
                        x => StringComparer.OrdinalIgnoreCase.GetHashCode(x.From)
                             ^ StringComparer.OrdinalIgnoreCase.GetHashCode(x.Keyword)));

            foreach (var (fromNode, keyword) in keywordEdgeGroups)
            {
                var edgesForKw = graphCfg.Edges.Where(e =>
                    string.Equals(e.From, fromNode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Keyword, keyword, StringComparison.OrdinalIgnoreCase)).ToList();
                int fromIdx = nodeIndexMap.GetValueOrDefault(fromNode, 0);
                bool hasFwd  = edgesForKw.Any(e => nodeIndexMap.GetValueOrDefault(e.To, 0) > fromIdx);
                bool hasBack = edgesForKw.Any(e => nodeIndexMap.GetValueOrDefault(e.To, 0) <= fromIdx);
                if (hasFwd && hasBack)
                    goLogger.LogWarning(
                        "[GraphOrchestrator] Node '{From}' has keyword '{Keyword}' on both a forward " +
                        "and a back-edge — the back-edge takes priority at runtime. " +
                        "The forward route for this keyword will never fire.",
                        fromNode, keyword);
            }
        }

        // Validate workflow config at startup when the cycle-native workflow strategy is
        // selected. WorkflowOrchestrator reuses Selection.Graph (same schema as 'graph') but
        // is a v1 implementation — Parallel, SubGraphId, RequireHumanApproval, RecoveryAgent,
        // and no-keyword (unconditional) edges are rejected here rather than silently ignored.
        // See WorkflowOrchestrator's class doc comment and docs/strategies.md for rationale.
        if (flags.UseWorkflow)
        {
            if (config.Selection.Graph is null)
                throw new InvalidOperationException(
                    "Selection.Type 'workflow' requires a 'Selection.Graph' configuration block.");

            var wfCfg      = config.Selection.Graph;
            var agentByName = config.Agents.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
            var agentNames = agentByName.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nodeIds    = wfCfg.Nodes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (wfCfg.Nodes.Count == 0)
                throw new InvalidOperationException(
                    "Selection.Graph.Nodes must contain at least one node.");

            var seenNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in wfCfg.Nodes)
            {
                if (string.IsNullOrWhiteSpace(node.Id))
                    throw new InvalidOperationException(
                        "Every node in Selection.Graph.Nodes must have a non-empty 'Id'.");
                if (!seenNodeIds.Add(node.Id))
                    throw new InvalidOperationException(
                        $"Duplicate node Id '{node.Id}' found in Selection.Graph.Nodes. Node Ids must be unique.");

                if (!string.IsNullOrWhiteSpace(node.SubGraphId))
                    throw new InvalidOperationException(
                        $"Workflow node '{node.Id}' sets 'SubGraphId', which Selection.Type 'workflow' " +
                        "does not support in this version. Use Selection.Type 'graph' for sub-graph nodes.");

                if (node.Parallel)
                    throw new InvalidOperationException(
                        $"Workflow node '{node.Id}' sets 'Parallel: true', which Selection.Type 'workflow' " +
                        "does not support in this version. Use Selection.Type 'graph' for parallel fan-out.");

                if (string.IsNullOrWhiteSpace(node.Agent))
                    throw new InvalidOperationException(
                        $"Workflow node '{node.Id}' must specify an 'Agent' name.");
                if (!agentByName.TryGetValue(node.Agent, out var nodeAgentCfg))
                    throw new InvalidOperationException(
                        $"Workflow node '{node.Id}' references agent '{node.Agent}' " +
                        $"which is not defined in 'Orchestration.Agents'.");

                // Selection.Type 'workflow' routes exclusively via the Handoff plugin's
                // handoff(route_keyword: ...) tool call — there is no text-on-its-own-line
                // fallback the way 'graph' has. Reject rather than silently fail every turn.
                if (!nodeAgentCfg.Plugins.Contains(HandoffPlugin.PluginName, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Workflow node '{node.Id}' agent '{node.Agent}' does not have the " +
                        $"'{HandoffPlugin.PluginName}' plugin enabled. " +
                        "Selection.Type 'workflow' routes exclusively via handoff(route_keyword: ...) " +
                        "tool calls (no text-keyword fallback) — add 'Handoff' to this agent's Plugins list.");
            }

            foreach (var edge in wfCfg.Edges)
            {
                if (!nodeIds.Contains(edge.From))
                    throw new InvalidOperationException(
                        $"Workflow edge From='{edge.From}' does not match any node Id in Selection.Graph.Nodes.");
                if (!nodeIds.Contains(edge.To))
                    throw new InvalidOperationException(
                        $"Workflow edge To='{edge.To}' does not match any node Id in Selection.Graph.Nodes.");

                if (string.IsNullOrEmpty(edge.Keyword))
                    throw new InvalidOperationException(
                        $"Workflow edge From='{edge.From}' To='{edge.To}' has no 'Keyword'. " +
                        "Selection.Type 'workflow' requires every edge to declare a Keyword in this version " +
                        "(no unconditional routing). Use Selection.Type 'graph' for unconditional edges.");

                if (edge.RequireHumanApproval)
                    throw new InvalidOperationException(
                        $"Workflow edge From='{edge.From}' To='{edge.To}' sets 'RequireHumanApproval: true', " +
                        "which Selection.Type 'workflow' does not support in this version. " +
                        "Use Selection.Type 'graph' for human-approval gates.");

                if (edge.RecoveryAgent is not null)
                    throw new InvalidOperationException(
                        $"Workflow edge From='{edge.From}' To='{edge.To}' sets 'RecoveryAgent', " +
                        "which Selection.Type 'workflow' does not support in this version. " +
                        "Use Selection.Type 'graph' for recovery agents.");
            }

            if (!string.IsNullOrWhiteSpace(wfCfg.EntryNode) && !nodeIds.Contains(wfCfg.EntryNode))
                throw new InvalidOperationException(
                    $"Selection.Graph.EntryNode '{wfCfg.EntryNode}' does not match any node Id in Selection.Graph.Nodes.");
        }

        ConversationCompactor? compactor = null;
        if (config.Compaction is { } compactionConfig)
        {
            if (compactionConfig.TriggerTurnCount <= 0)
                throw new InvalidOperationException(
                    $"Compaction.TriggerTurnCount must be a positive integer (got {compactionConfig.TriggerTurnCount}). " +
                    "A value of 0 or less would compact the conversation on every turn.");

            if (compactionConfig.KeepRecentTurns < 1)
                throw new InvalidOperationException(
                    "Compaction.KeepRecentTurns must be at least 1.");

            if (compactionConfig.KeepRecentTurns >= compactionConfig.TriggerTurnCount)
                throw new InvalidOperationException(
                    $"Compaction.KeepRecentTurns ({compactionConfig.KeepRecentTurns}) must be " +
                    $"less than Compaction.TriggerTurnCount ({compactionConfig.TriggerTurnCount}).");

            var summaryModel = compactionConfig.Model ?? config.Agents[0].Model;
            // Magentic, adversarial, and map-reduce sessions have no brief.json or change log,
            // so the workflow-specific resumption note is suppressed to avoid wasting tokens.
            bool suppressResumptionNote = flags.UseMagentic || flags.UseAdversarial || flags.UseMapReduce || flags.UseScatterGather;
            var resumptionNote = suppressResumptionNote ? null : ConversationCompactor.WorkflowResumptionNote;
            var changeLogPath  = suppressResumptionNote ? null
                : (config.Validation?.ChangeLogPath ?? config.ChangeTracking?.Path);

            // Knowledge snapshot enricher: augments lossless/hybrid snapshots with ADR,
            // objective, architecture-violation, memory, and provenance-expiry state.
            var snapshotEnricher = new fuseraft.Infrastructure.Knowledge.KnowledgeSnapshotEnricher(
                adrRegistry:      knowledgeLayer.AdrRegistry,
                objectiveManager: objectiveManager,
                memoryStore:      new fuseraft.Infrastructure.Repository.RepositoryMemoryStore(FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalRepositoryMemory, projectSlug)),
                provenance:       knowledgeLayer.ProvenanceRegistry,
                manifestPath:     FuseraftPaths.LocalArchitectureManifest,
                projectRoot:      knowledgeSandbox);

            compactor = new ConversationCompactor(
                chatClientFactory.Create(summaryModel), compactionConfig,
                loggerFactory.CreateLogger<ConversationCompactor>(),
                resumptionNote, changeLogPath, intentLog, config.Events?.Path, evidenceStore,
                objectiveManager, snapshotEnricher, readCachePath,
                executionStatePath: executionStatePath,
                briefPath: config.Validation?.BriefPath);

            if ((compactionConfig.Mode ?? string.Empty).Equals(CompactionModes.Intent, StringComparison.OrdinalIgnoreCase)
                && intentLog is null)
            {
                loggerFactory.CreateLogger(nameof(OrchestratorBuilder)).LogWarning(
                    "Compaction.Mode is 'intent' but no ChangeTracking.IntentLogPath is configured — " +
                    "compaction will fall back to lossless or LLM mode at runtime. " +
                    "Set ChangeTracking.IntentLogPath to enable deterministic intent compaction.");
            }
        }

        // Build the post-session skill curator when curation is enabled.
        SkillCurator? skillCurator = null;
        if (config.SkillCuration?.Enabled == true)
        {
            var curatorModelCfg = config.SkillCuration.Model is { Length: > 0 } m
                ? chatClientFactory.Resolve(new ModelConfig { ModelId = m })
                : config.Agents[0].Model;
            skillCurator = new SkillCurator(
                chatClientFactory.Create(curatorModelCfg),
                config.SkillCuration,
                evidenceStore,
                loggerFactory.CreateLogger<SkillCurator>());
        }

        // Validate context budget config.
        if (config.ContextBudget is { } budget)
        {
            bool budgetNeedsCompactor = budget.CutoverAt > 0 || budget.MaxSingleTurnInputTokens > 0;
            if (budgetNeedsCompactor && compactor is null)
                throw new InvalidOperationException(
                    "ContextBudget.CutoverAt and ContextBudget.MaxSingleTurnInputTokens require a " +
                    "Compaction configuration. Add a Compaction section to your orchestration config " +
                    "so the compactor is available when the context budget triggers.");

            if (budget.WarnAt > 0 && budget.CutoverAt > 0 && budget.WarnAt >= budget.CutoverAt)
                throw new InvalidOperationException(
                    $"ContextBudget.WarnAt ({budget.WarnAt:N0}) must be less than " +
                    $"CutoverAt ({budget.CutoverAt:N0}).");

            // Warn when WarnTurnTokens >= CutoverAt: a turn that fires the per-turn warning
            // will simultaneously trigger compaction, making the warning a post-hoc note
            // rather than an advance signal. Lower WarnTurnTokens below CutoverAt to get
            // a meaningful early warning before the compaction threshold is crossed.
            if (config.WarnTurnTokens > 0 && budget.CutoverAt > 0 &&
                config.WarnTurnTokens >= budget.CutoverAt)
            {
                loggerFactory.CreateLogger(nameof(OrchestratorBuilder)).LogWarning(
                    "WarnTurnTokens ({WarnTurnTokens:N0}) is >= ContextBudget.CutoverAt ({CutoverAt:N0}). " +
                    "The per-turn warning fires in the same turn that triggers compaction — it cannot " +
                    "provide advance warning. Set WarnTurnTokens below CutoverAt to get an early signal " +
                    "before the compaction threshold is crossed.",
                    config.WarnTurnTokens, budget.CutoverAt);
            }
        }

        return (config, compactor, skillCurator);
    }

    // -------------------------------------------------------------------------
    // WireSkillsAndVerifier
    // -------------------------------------------------------------------------

    private static void WireSkillsAndVerifier(
        OrchestrationConfig config,
        ChatClientFactory chatClientFactory,
        ILoggerFactory loggerFactory,
        ConversationCompactor? compactor)
    {
        // Validate verifier config: the named agent must exist in the agent pool.
        // (Already validated in ValidateAndSelectStrategy; this is the wire-up hook
        // for any post-compactor verifier wiring that may be needed in the future.)

        // Validate context budget config cross-check with WarnTurnTokens.
        // (Already performed in ValidateAndSelectStrategy; no additional wiring needed here.)
        _ = compactor; // referenced for future expansion
    }

    // -------------------------------------------------------------------------
    // CreateOrchestrator
    // -------------------------------------------------------------------------

    private static (IOrchestrator Orchestrator, fuseraft.Infrastructure.Repository.RepositoryMemoryExtractor? RepoMemoryExtractor) CreateOrchestrator(
        OrchestrationConfig config,
        OrchestratorKindFlags flags,
        OrchestratorInfraServices infra,
        OrchestratorKnowledgeServices knowledge,
        OrchestratorSessionPaths sessionPaths,
        IHumanApprovalService? humanApprovalService)
    {
        var loggerFactory        = infra.LoggerFactory;
        var chatClientFactory    = infra.ChatClientFactory;
        var governanceKernel     = infra.GovernanceKernel;
        var changeTracker        = infra.ChangeTracker;
        var eventEmitter         = infra.EventEmitter;
        var knowledgeLayer       = knowledge.KnowledgeLayer;
        var objectiveManager     = knowledge.ObjectiveManager;
        var evidenceStore        = knowledge.EvidenceStore;
        var dependencyPlanner    = knowledge.DependencyPlanner;
        var memoryManager        = knowledge.MemoryManager;
        var projectSlug          = sessionPaths.ProjectSlug;
        var sessionId            = sessionPaths.SessionId;
        var executionStatePath   = sessionPaths.ExecutionStatePath;
        var investigationLogPath = sessionPaths.InvestigationLogPath;

        var aoLogger = loggerFactory.CreateLogger<AgentOrchestrator>();
        var goLogger = loggerFactory.CreateLogger<GraphOrchestrator>();

        var resolvedSandbox = config.Security?.FileSystemSandboxPath is { Length: > 0 } sbx
            ? FuseraftPaths.ExpandPath(sbx) : null;

        // Context Broker (Gap 8): adaptive context pipeline backed by the shared knowledge layer.
        var brokerMemoryStore = new fuseraft.Infrastructure.Repository.RepositoryMemoryStore(FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalRepositoryMemory, projectSlug));
        var contextBroker = new fuseraft.Orchestration.Context.ContextBroker(
            knowledgeLayer,
            brokerMemoryStore,
            knowledgeLayer.ProvenanceRegistry);

        // Shared assembler used by both the state machine (HandoffContext) and the
        // orchestrator (AgentConfig.Context). One instance so session ID updates propagate.
        // Sources the graph store and ADR registry from the shared knowledge layer so
        // adr_graph traversal sees the same state as the plugins and change tracker.
        var contextAssembler = new ContextAssembler(
            sandboxRoot:           resolvedSandbox,
            changeLogPath:         config.Validation?.ChangeLogPath,
            briefPath:             config.Validation?.BriefPath,
            graphStore:            knowledgeLayer.GraphStore,
            adrRegistry:           knowledgeLayer.AdrRegistry,
            objectiveManager:      objectiveManager,
            contextBroker:         contextBroker,
            executionStatePath:    executionStatePath,
            investigationLogPath:  investigationLogPath);
        if (!string.IsNullOrEmpty(sessionId))
            contextAssembler.SetSessionId(sessionId);

        var strategyFactory = new StrategyFactory(chatClientFactory.Create, eventEmitter, loggerFactory, governanceKernel, humanApprovalService, evidenceStore, knowledgeLayer.ProvenanceRegistry, config.TestSelector, resolvedSandbox, contextAssembler);

        var agentFactory = new AgentFactory(chatClientFactory, infra.PluginRegistry, config.Security, changeTracker, config.Scratchpad, config.Chatroom, governanceKernel, infra.IdentityRegistry, eventEmitter, loggerFactory, BuildSkillsProvider(), infra.ToolArtifactStore);

        // Unified context assembly pipeline — shared across all orchestrator types.
        // Provides always-on knowledge retrieval, relevance-ranked memory, and metrics
        // telemetry for every agent invocation regardless of which orchestrator is active.
        var repoMemoryStore = new fuseraft.Infrastructure.Repository.RepositoryMemoryStore(
            FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalRepositoryMemory, projectSlug));
        memoryManager?.AttachRepositoryMemory(repoMemoryStore);

        var graphExpander   = new fuseraft.Orchestration.Knowledge.GraphExpansionRetriever(knowledgeLayer.GraphStore);
        var knowledgeStore  = new fuseraft.Infrastructure.Repository.RepositoryKnowledgeStore(FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalKnowledgeFindings, projectSlug));
        var pipelineLogger  = loggerFactory.CreateLogger<fuseraft.Orchestration.Context.ContextAssemblyPipeline>();
        var contextPipeline = new fuseraft.Orchestration.Context.ContextAssemblyPipeline(
            knowledgeLayer:   knowledgeLayer,
            memoryManager:    memoryManager,
            contextAssembler: contextAssembler,
            graphExpander:    graphExpander,
            knowledgeStore:   knowledgeStore,
            eventEmitter:     eventEmitter,
            logger:           pipelineLogger);
        if (!string.IsNullOrEmpty(sessionId))
            contextPipeline.SetSessionId(sessionId);

        // MagenticOrchestrator handles the "magentic" selection type: a manager LLM drives
        // dynamic planning, speaker selection, and stall detection without hard-coded routing.
        //
        // GraphOrchestrator handles the "graph" selection type: declarative directed-graph
        // execution with per-node agents, keyword-driven edges, and optional back-edges.
        //
        // AdversarialOrchestrator handles the "adversarial" selection type: GAN-style
        // generate → critique → revise loops where critics receive isolated context windows.
        //
        // AgentOrchestrator is the general-purpose path: it drives any selection strategy
        // (sequential, llm, keyword, structured) through StrategyFactory and works with
        // any agent names and any team size.
        IOrchestrator orchestrator;

        if (flags.UseGraph)
        {
            orchestrator = new GraphOrchestrator(
                config, agentFactory, goLogger,
                changeTracker, eventEmitter, governanceKernel,
                flags.HitlMode ? humanApprovalService : null,
                contextPipeline, knowledgeStore,
                loggerFactory);
        }
        else if (flags.UseWorkflow)
        {
            var wfLogger = loggerFactory.CreateLogger<WorkflowOrchestrator>();
            orchestrator  = new WorkflowOrchestrator(
                config, agentFactory, wfLogger,
                changeTracker, eventEmitter, governanceKernel,
                contextPipeline);
        }
        else if (flags.UseAdversarial)
        {
            var advLogger = loggerFactory.CreateLogger<AdversarialOrchestrator>();
            orchestrator  = new AdversarialOrchestrator(
                config, agentFactory, advLogger,
                changeTracker, eventEmitter, governanceKernel);
        }
        else if (flags.UseMapReduce)
        {
            var mrLogger = loggerFactory.CreateLogger<MapReduceOrchestrator>();
            orchestrator  = new MapReduceOrchestrator(
                config, agentFactory, mrLogger,
                changeTracker, eventEmitter, governanceKernel,
                flags.HitlMode ? humanApprovalService : null,
                contextPipeline, knowledgeStore);
        }
        else if (flags.UseScatterGather)
        {
            var sgLogger = loggerFactory.CreateLogger<ScatterGatherOrchestrator>();
            orchestrator  = new ScatterGatherOrchestrator(
                config, agentFactory, sgLogger,
                changeTracker, eventEmitter, governanceKernel,
                flags.HitlMode ? humanApprovalService : null,
                contextPipeline, knowledgeStore);
        }
        else if (flags.UseMagentic)
        {
            var magCfg        = config.Selection.Magentic!;           // validated above
            var managerModel  = chatClientFactory.Resolve(magCfg.Model!);
            var managerClient = chatClientFactory.Create(managerModel);
            var magLogger     = loggerFactory.CreateLogger<MagenticOrchestrator>();

            orchestrator = new MagenticOrchestrator(
                config, agentFactory, managerClient, magLogger,
                flags.HitlMode ? humanApprovalService : null,
                changeTracker, eventEmitter, governanceKernel,
                contextPipeline, knowledgeStore);
        }
        else
        {
            orchestrator = new AgentOrchestrator(config, agentFactory, strategyFactory, aoLogger, changeTracker, eventEmitter, governanceKernel, memoryManager, contextAssembler, dependencyPlanner, contextPipeline, knowledgeStore);
        }

        // Repository memory extractor — runs after the session to generate candidates.
        // Requires an evidence store to query; skipped when evidence tracking is disabled.
        fuseraft.Infrastructure.Repository.RepositoryMemoryExtractor? repoMemoryExtractor = null;
        if (evidenceStore is not null)
        {
            var extractorStore = new fuseraft.Infrastructure.Repository.RepositoryMemoryStore(
                FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalRepositoryMemory, projectSlug));
            repoMemoryExtractor = new fuseraft.Infrastructure.Repository.RepositoryMemoryExtractor(
                evidenceStore, extractorStore);
        }

        // Wrap with SagaOrchestrator when the saga pattern is enabled.
        // The wrapper preserves the IOrchestrator contract so the rest of the pipeline
        // is unaffected; it adds compensating-rollback behaviour on failure.
        if (config.Saga?.Enabled == true)
            orchestrator = new SagaOrchestrator(orchestrator, config.Saga, compensators: null, eventEmitter);

        return (orchestrator, repoMemoryExtractor);
    }

    /// <summary>
    /// Makes a lightweight <c>GET /models</c> call to each unique API endpoint in
    /// <paramref name="config"/> to verify the keys are valid before the session starts.
    /// Throws <see cref="InvalidOperationException"/> if any key is missing or rejected.
    /// </summary>
    public static async Task ValidateApiKeysAsync(
        OrchestrationConfig config,
        CancellationToken cancellationToken = default)
    {
        // Collect all ModelConfigs: one per agent + optional selection-strategy model
        // + optional Magentic manager model.
        // Resolve aliases against the Models registry first so agents that reference
        // a named alias (e.g. "fast") get the endpoint and API key from the alias.
        var models = config.Agents.Select(a => ResolveAlias(a.Model, config.Models))
            .Concat(config.Selection.Model is not null
                ? [ResolveAlias(config.Selection.Model, config.Models)]
                : Array.Empty<ModelConfig>())
            .Concat(config.Selection.Magentic?.Model is not null
                ? [ResolveAlias(config.Selection.Magentic.Model, config.Models)]
                : Array.Empty<ModelConfig>())
            .Where(m => !string.IsNullOrWhiteSpace(m.ApiKeyEnvVar))  // skip Ollama (no key)
            .GroupBy(m => m.ApiKeyEnvVar)   // deduplicate: only probe each key once
            .Select(g => g.First())
            .ToList();

        var http = _validationHttp;

        foreach (var model in models)
        {
            var apiKey = Environment.GetEnvironmentVariable(model.ApiKeyEnvVar);
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException(
                    $"API key variable '{model.ApiKeyEnvVar}' is not set.");

            // Strip /chat/completions (or any path) to get the provider base URL.
            var uri    = new Uri(model.Endpoint.TrimEnd('/'));
            var baseUrl = $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}";

            // Use a per-request message so keys from different providers don't bleed
            // across iterations via DefaultRequestHeaders.
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(
                    $"Could not reach API endpoint '{baseUrl}': {ex.Message}", ex);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new InvalidOperationException(
                    $"API key from '{model.ApiKeyEnvVar}' was rejected by the provider (HTTP 401). " +
                    $"Verify the key is current and has the correct permissions.");
        }
    }

    private static ModelConfig ResolveAlias(
        ModelConfig model,
        IReadOnlyDictionary<string, ModelConfig> registry)
    {
        if (registry.TryGetValue(model.ModelId, out var alias))
        {
            return alias with
            {
                Temperature = model.Temperature ?? alias.Temperature,
                MaxTokens   = model.MaxTokens > 0 ? model.MaxTokens : alias.MaxTokens
            };
        }
        return model;
    }

    private static AgentSkillsProvider? BuildSkillsProvider()
    {
        // Project-native → project cross-client → user-native → user cross-client → built-in.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), ".fuseraft", "skills"),
            Path.Combine(Directory.GetCurrentDirectory(), ".agents",   "skills"),
            FuseraftPaths.GlobalSkills,
            Path.Combine(home, ".agents",   "skills"),
            Path.Combine(AppContext.BaseDirectory, "skills"),
        }.Where(Directory.Exists).ToArray();

        if (dirs.Length == 0) return null;

        return new AgentSkillsProviderBuilder()
            .UseFileSkills(dirs)
            .UseFileScriptRunner(RunSkillScriptAsync)
            .Build();
    }

    private static async Task<object?> RunSkillScriptAsync(
        AgentFileSkill skill,
        AgentFileSkillScript script,
        JsonElement? arguments,
        IServiceProvider? serviceProvider,
        CancellationToken cancellationToken)
    {
        var ext = Path.GetExtension(script.FullPath).ToLowerInvariant();
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var program = ext switch
        {
            ".py" => isWindows ? "python" : "python3",
            ".sh" => "bash",
            ".js" => "node",
            _     => null
        };
        if (program is null)
            return $"No runner registered for '{ext}' scripts.";

        var psi = new ProcessStartInfo
        {
            FileName               = program,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(script.FullPath);
        if (arguments.HasValue && arguments.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in arguments.Value.EnumerateObject())
            {
                var val = prop.Value.ToString();
                if (!string.IsNullOrEmpty(val))
                    psi.ArgumentList.Add(val);
            }
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {program}");

        // Read stdout and stderr concurrently — sequential reads deadlock if either pipe fills.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask  = proc.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask);
        await proc.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr  = await stderrTask;
        return string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\nstderr: {stderr}";
    }

}
