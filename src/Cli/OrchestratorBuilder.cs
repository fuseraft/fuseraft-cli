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
using fuseraft.Core.Skills;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.KeyStore;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration;
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
    fuseraft.Cli.Telemetry.SessionMetrics?    SessionMetrics    = null,
    AdaptiveTrimTracker?          AdaptiveTrimTracker = null,
    Action<string?>?              RebindSessionScopedState = null)
{
    /// <summary>Builds the config's agents outside the orchestrator, e.g. for <c>fuseraft serve --a2a</c>.</summary>
    public AgentFactory? AgentFactory { get; init; }
}

/// <summary>
/// Which orchestrator kind <c>Selection.Type</c> resolved to, bundled so
/// <c>ValidateAndSelectStrategy</c> and <c>CreateOrchestrator</c> share one instance instead
/// of each taking the same 6-7 bools as separate positional parameters.
/// </summary>
internal sealed record OrchestratorKindFlags(
    bool HitlMode,
    bool UseMagentic,
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
    fuseraft.Infrastructure.Tools.ToolResultArtifactStore ToolArtifactStore,
    AdaptiveTrimTracker AdaptiveTrimTracker);

/// <summary>
/// Knowledge/memory/evidence collaborators that feed <c>ContextBroker</c>/
/// <c>ContextAssembler</c>/<c>ContextAssemblyPipeline</c> construction and the default
/// <c>AgentOrchestrator</c> branch in <c>CreateOrchestrator</c>.
/// </summary>
internal sealed record OrchestratorKnowledgeServices(
    fuseraft.Infrastructure.Knowledge.KnowledgeLayer KnowledgeLayer,
    fuseraft.Infrastructure.Objectives.ObjectiveManager ObjectiveManager,
    EvidenceStore? EvidenceStore,
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
///
/// <para>
/// <b>Collaborators</b> (all in <c>fuseraft.Cli</c>): config loading, binding, and
/// pre-processing is owned by <see cref="OrchestratorConfigLoader"/>. System-prompt assembly
/// is owned by <see cref="SystemPromptBuilder"/>. Provider API-key connectivity probing is
/// owned by <see cref="ApiKeyValidator"/>. This class retains the construction pipeline itself
/// (<see cref="BuildAsync"/> and its named steps) plus the skills-provider wiring
/// (<c>BuildSkillsProvider</c>/<c>RunSkillScriptAsync</c>, too small a pair to warrant their
/// own file).
/// </para>
/// </summary>
public static class OrchestratorBuilder
{
    // Internal (not private) — shared with SystemPromptBuilder and OrchestratorConfigLoader,
    // which also deserialize brownfield JSON (ConventionProfile / agent files).
    internal static readonly JsonSerializerOptions BrownfieldJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        Converters                  = { new JsonStringEnumConverter() },
    };

    // Matches the snake_case convention used by every other JSONL log in fuseraft (events.jsonl,
    // provider_errors.jsonl, etc.) so the hash-chain audit log reads consistently on disk.
    private static readonly JsonSerializerOptions AuditJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
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

        var (configAfterSecurity, profiles, shellApprover, toolActionApprover, fileWriteApprover) = ResolveSecurityConfig(
            config, pluginRegistry, hitlMode, humanApprovalService, loggerFactory);
        config = configAfterSecurity;

        config = await SystemPromptBuilder.BuildSystemPrompt(
            config, configPath, sessionId, specContent, loggerFactory, cancellationToken);

        var infra = await InitInfrastructure(
            config, pluginRegistry, loggerFactory, sessionId, projectSlug,
            profiles, shellApprover, toolActionApprover, fileWriteApprover, cancellationToken);
        config = infra.Config;

        var (governanceKernel, chatClientFactory, identityRegistry) =
            InitGovernanceKernel(
                config, loggerFactory, configPath, projectSlug,
                pluginRegistry, infra.EventEmitter);

        bool useMagentic      = config.Selection.Type.Equals(OrchestratorTypes.Magentic,      StringComparison.OrdinalIgnoreCase);
        bool useAdversarial   = config.Selection.Type.Equals(OrchestratorTypes.Adversarial,   StringComparison.OrdinalIgnoreCase);
        bool useMapReduce     = config.Selection.Type.Equals(OrchestratorTypes.MapReduce,     StringComparison.OrdinalIgnoreCase);
        bool useScatterGather = config.Selection.Type.Equals(OrchestratorTypes.ScatterGather, StringComparison.OrdinalIgnoreCase);
        var kindFlags = new OrchestratorKindFlags(
            hitlMode, useMagentic, useAdversarial, useMapReduce, useScatterGather);

        var (configAfterStrategy, compactor, skillCurator) = await ValidateAndSelectStrategy(
            config, loggerFactory, chatClientFactory, kindFlags,
            infra.KnowledgeLayer, infra.ObjectiveManager, infra.KnowledgeSandbox, projectSlug,
            infra.IntentLog, infra.EvidenceStore, infra.ExecutionStatePath, infra.InvestigationLogPath,
            sessionId, readCachePath: infra.ReadCachePath, cancellationToken);
        config = configAfterStrategy;

        WireSkillsAndVerifier(config, chatClientFactory, loggerFactory, compactor);

        // Shared with SessionRunner (via OrchestratorBuildResult below) so a provider call that
        // only survived via adaptive context-trim can force a real compaction before the next
        // turn — see AgentMiddlewareBuilder's adaptive-retry loop and CompactionCoordinator.
        var adaptiveTrimTracker = new AdaptiveTrimTracker();

        var infraServices = new OrchestratorInfraServices(
            loggerFactory, chatClientFactory, pluginRegistry, governanceKernel,
            infra.ChangeTracker, infra.EventEmitter, identityRegistry, infra.ToolArtifactStore,
            adaptiveTrimTracker);
        var knowledgeServices = new OrchestratorKnowledgeServices(
            infra.KnowledgeLayer, infra.ObjectiveManager, infra.EvidenceStore,
            MemoryManager.FromConfig(config.Memory));
        var sessionPaths = new OrchestratorSessionPaths(
            projectSlug, sessionId, infra.ExecutionStatePath, infra.InvestigationLogPath);

        var (orchestrator, repoMemoryExtractor, agentFactory) = CreateOrchestrator(
            config, kindFlags, infraServices, knowledgeServices, sessionPaths, humanApprovalService);

        return new OrchestratorBuildResult(orchestrator, config, infra.McpManager, compactor, infra.ChangeTracker, infra.EventEmitter, governanceKernel, skillCurator, repoMemoryExtractor, chatClientFactory, infra.SessionMetrics, adaptiveTrimTracker, infra.RebindSessionScopedState)
        {
            AgentFactory = agentFactory,
        };
    }

    // -------------------------------------------------------------------------
    // ResolveSecurityConfig
    // -------------------------------------------------------------------------

    private static (OrchestrationConfig Config, IReadOnlyDictionary<string, ApiProfileConfig>? Profiles, Func<string, Task<bool>>? ShellApprover, Func<string, string, string, Task<bool>>? ToolActionApprover, Func<string, string, string, string, Task<bool>>? FileWriteApprover) ResolveSecurityConfig(
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
            ? ApprovalScope.Guard(humanApprovalService.PromptShellCommandAsync)
            : null;
        Func<string, string, string, Task<bool>>? toolActionApprover = hitlMode && humanApprovalService is not null
            ? ApprovalScope.Guard(humanApprovalService.PromptToolActionAsync)
            : null;
        // Diff-aware counterpart to toolActionApprover, used only by write_file/patch_file —
        // see IHumanApprovalService.PromptFileWriteAsync and ReplCommand.cs's approveFileWrite
        // closure, kept in parity here for `fuseraft run --hitl`.
        Func<string, string, string, string, Task<bool>>? fileWriteApprover = hitlMode && humanApprovalService is not null
            ? ApprovalScope.Guard(humanApprovalService.PromptFileWriteAsync)
            : null;

        pluginRegistry.Configure(config.Security, profiles, shellApprover, toolActionApprover: toolActionApprover, fileWriteApprover: fileWriteApprover, fileLedger: FileLedger.Shared);

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

        return (config, profiles, shellApprover, toolActionApprover, fileWriteApprover);
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
        string? ReadCachePath,
        Action<string?> RebindSessionScopedState);

    // A global MCP server (saved in ~/.fuseraft/config, or provided by the launcher) serves every run on the machine, so
    // it's connected only for a config whose agents name it in Plugins, and one that's down costs that agent its tools
    // rather than failing the run.
    internal static async Task ConnectManagedMcpServersAsync(OrchestrationConfig config, IReadOnlyList<McpServerConfig> managed,
        McpSessionManager mcpManager, PluginRegistry pluginRegistry, ILogger logger, CancellationToken cancellationToken)
    {
        var wanted = ManagedMcpServers.ReferencedBy(managed, config.McpServers, config.Agents.SelectMany(a => a.Plugins));
        foreach (var server in wanted)
        {
            try
            {
                var (_, tools) = await mcpManager.ConnectSingleAsync(server, cancellationToken);
                pluginRegistry.RegisterAIFunctions(server.Name, tools);
                logger.LogInformation("Connected global MCP server '{Name}' ({Count} tool(s)).", server.Name, tools.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Registered empty, so an agent that lists it still builds instead of failing on an unknown plugin.
                pluginRegistry.RegisterAIFunctions(server.Name, []);
                logger.LogWarning("Could not connect global MCP server '{Name}': {Error}. Agents that list it run without its tools.",
                    server.Name, ex.Message);
            }
        }
    }

    private static async Task<InfrastructureResult> InitInfrastructure(
        OrchestrationConfig config,
        PluginRegistry pluginRegistry,
        ILoggerFactory loggerFactory,
        string? sessionId,
        string projectSlug,
        IReadOnlyDictionary<string, ApiProfileConfig>? profiles,
        Func<string, Task<bool>>? shellApprover,
        Func<string, string, string, Task<bool>>? toolActionApprover,
        Func<string, string, string, string, Task<bool>>? fileWriteApprover,
        CancellationToken cancellationToken)
    {
        // Connect to MCP servers and register their tools before building agents.
        var mcpManager = new McpSessionManager(loggerFactory);
        if (config.McpServers.Count > 0)
            await mcpManager.InitializeAsync(config.McpServers, pluginRegistry, cancellationToken);
        var mcpLogger  = loggerFactory.CreateLogger("fuseraft.Mcp");
        var managedMcp = ManagedMcpServers.Merge(ReplMcpServerStore.Load(), ManagedMcpServers.Load(out var managedMcpError));
        if (managedMcpError is not null) mcpLogger.LogWarning("{Error}", managedMcpError);
        await ConnectManagedMcpServersAsync(config, managedMcp, mcpManager, pluginRegistry, mcpLogger, cancellationToken);

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

            changeTracker = new ChangeTracker(ctConfig.Path, eventEmitter, evidenceStore, intentLog, loggerFactory.CreateLogger<ChangeTracker>(), knowledgeLayer.GraphBuilder, stateProjector, FileLedger.Shared);
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
        pluginRegistry.Configure(config.Security ?? new SecurityConfig(), profiles, shellApprover, fileVersionStore, sessionReadCache, onCacheHit: sessionMetrics.RecordCacheHit, eventSink: stateProjector, toolActionApprover: toolActionApprover, fileWriteApprover: fileWriteApprover, fileLedger: FileLedger.Shared);

        // Session context plugin: shared handoff notes that agents write before routing
        // and read on re-entry. Stored in the global session directory.
        var ctxSummaryPath = sessionId is { Length: > 0 }
            ? FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalSessionContext, sessionId,  projectSlug)
            : FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalSessionContext, "default", projectSlug);
        var sessionContextPlugin = new fuseraft.Infrastructure.Plugins.SessionContextPlugin(ctxSummaryPath);
        pluginRegistry.Register("SessionContext", () => sessionContextPlugin);

        // Rebinds every session-scoped collaborator built above to a different session ID's
        // paths in place, without touching PluginRegistry's cached plugin instances or
        // rebuilding the orchestrator. `fuseraft run` never needs this (one process = one
        // session, sessionId is fixed for BuildAsync's whole lifetime); `fuseraft serve` calls
        // it once per dispatched task — see ServeHost.ProcessTaskAsync — because it builds this
        // orchestrator exactly once and reuses it, and everything above was otherwise
        // permanently bound to whichever sessionId (or none) was in scope at daemon startup.
        void RebindSessionScopedState(string? newSessionId)
        {
            var newReadCachePath = newSessionId is { Length: > 0 }
                ? FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalSessionReadCache, newSessionId, projectSlug)
                : null;
            sessionReadCache.Rebind(newReadCachePath);
            FileLedger.Shared.Clear();

            var newToolArtifactsDir = newSessionId is { Length: > 0 }
                ? FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalSessionToolArtifacts, newSessionId, projectSlug)
                : null;
            toolArtifactStore.Rebind(newToolArtifactsDir);

            var newCtxSummaryPath = newSessionId is { Length: > 0 }
                ? FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalSessionContext, newSessionId, projectSlug)
                : FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalSessionContext, "default", projectSlug);
            sessionContextPlugin.Rebind(newCtxSummaryPath);
        }

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
            toolArtifactStore, sessionMetrics, objectiveManager, knowledgeSandbox, readCachePath,
            RebindSessionScopedState);
    }

    // -------------------------------------------------------------------------
    // InitGovernanceKernel
    // -------------------------------------------------------------------------

    private static (GovernanceKernel GovernanceKernel, ChatClientFactory ChatClientFactory, IdentityRegistry IdentityRegistry) InitGovernanceKernel(
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
        // PolicyViolation is NOT bridged here — StateMachineSelectionStrategy emits gate failures
        // directly to preserve the richer per-turn context (consecutive count, etc.).
        if (eventEmitter is not null)
        {
            governanceKernel.OnEvent(GovernanceEventType.ToolCallBlocked, async evt =>
                await eventEmitter.EmitAsync(EventTypes.ToolBlocked, evt.AgentId,
                    payload: new { policy = evt.PolicyName, data = evt.Data }));
        }

        // Hash-chain audit log: subscribe to all governance events so every denial
        // and policy check is tamper-evidently recorded for the session's lifetime, and
        // persist each entry to disk as it's appended so the chain survives the process.
        var auditLogger  = new AuditLogger();
        var auditLogPath = config.Events is { } auditEventsPath
            ? Path.Combine(Path.GetDirectoryName(auditEventsPath.Path) ?? FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalLogs, projectSlug), "audit-chain.jsonl")
            : FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalAuditLog, projectSlug);
        var auditFileLock = new object();

        void PersistAuditEntry(AuditEntry entry)
        {
            try
            {
                lock (auditFileLock)
                {
                    var dir = Path.GetDirectoryName(auditLogPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(auditLogPath, JsonSerializer.Serialize(entry, AuditJsonOpts) + "\n");
                }
            }
            catch
            {
                // Best-effort — audit file I/O must never disrupt the session.
            }
        }

        // Link this session's chain to the project's most recent prior session, so the audit
        // trail isn't just N disconnected per-session chains. AuditLogger hardcodes the first
        // entry's previous_hash to "" with no way to override it, so the link is carried in the
        // entry's own (hashed) action text instead of the previous_hash field — `fuseraft log
        // audit --verify` confirms the claimed hash really is some sibling session's chain tip.
        // Only on a genuinely new file: a resumed session re-appends to an existing chain, and
        // must not re-inject a link entry into the middle of it.
        if (!File.Exists(auditLogPath))
        {
            var previousTipHash = FindPreviousSessionTipHash(projectSlug, auditLogPath);
            if (previousTipHash is not null)
                PersistAuditEntry(auditLogger.Log("system", $"SessionLinked:prev={previousTipHash}", "allow"));
        }

        governanceKernel.OnAllEvents(evt =>
        {
            var action   = evt.PolicyName is not null ? $"{evt.Type}:{evt.PolicyName}" : evt.Type.ToString();
            var decision = evt.Type is GovernanceEventType.PolicyViolation
                               or GovernanceEventType.ToolCallBlocked
                               or GovernanceEventType.TrustFailed
                ? "deny" : "allow";
            PersistAuditEntry(auditLogger.Log(evt.AgentId, action, decision));
        });

        // Reasoning audit: each turn's reasoning block is SHA-256-hashed and appended to the
        // audit chain so the association between model thinking and actions is tamper-evident
        // without exposing the full reasoning text in the audit record.
        if (eventEmitter is not null)
        {
            eventEmitter.RegisterHook(new ReasoningAuditHook(auditLogger, PersistAuditEntry));
        }

        var identityRegistry  = new IdentityRegistry();
        var providerErrorLog  = config.Events is { } evtPath
            ? Path.Combine(Path.GetDirectoryName(evtPath.Path) ?? FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalLogs, projectSlug), "provider_errors.jsonl")
            : FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalProviderErrors, projectSlug);
        var userConfig        = UserConfigStore.Load().Config;
        FileLedger.Shared.TrackedFiles = userConfig?.Files?.TrackedFiles ?? FilesConfig.DefaultTrackedFiles;
        var chatClientFactory = new ChatClientFactory(config.Models.Count > 0 ? config.Models : null, providerErrorLog, eventEmitter, loggerFactory,
            TransportOptions.From(userConfig));

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

        return (governanceKernel, chatClientFactory, identityRegistry);
    }

    /// <summary>
    /// Finds the SHA-256 chain-tip hash of this project's most recently written prior session,
    /// for stamping into a new session's first audit entry (see the "Link this session's chain"
    /// block in <see cref="InitGovernanceKernel"/>). Scans sibling directories of
    /// <paramref name="currentAuditLogPath"/> under <c>~/.fuseraft/sessions/{projectSlug}/</c>
    /// for an <c>audit-chain.jsonl</c>, picks the most recently modified one, and reads its
    /// last line's <c>hash</c> field. Returns <c>null</c> when this is the project's first
    /// session or no prior chain file can be read.
    /// </summary>
    private static string? FindPreviousSessionTipHash(string projectSlug, string currentAuditLogPath)
    {
        var sessionsRoot = FuseraftPaths.GlobalProjectSessions(projectSlug);
        if (!Directory.Exists(sessionsRoot)) return null;

        var currentDir = Path.GetDirectoryName(Path.GetFullPath(currentAuditLogPath));

        var candidate = Directory.GetDirectories(sessionsRoot)
            .Where(d => !string.Equals(Path.GetFullPath(d), currentDir, StringComparison.OrdinalIgnoreCase))
            .Select(d => Path.Combine(d, "audit-chain.jsonl"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        return candidate is null ? null : ReadTipHash(candidate);
    }

    private static string? ReadTipHash(string auditLogPath)
    {
        try
        {
            var lastLine = File.ReadLines(auditLogPath).LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (lastLine is null) return null;
            using var doc = JsonDocument.Parse(lastLine);
            return doc.RootElement.TryGetProperty("hash", out var h) ? h.GetString() : null;
        }
        catch
        {
            return null;
        }
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
        // The team compiler has already validated the flow; only the manager model needs
        // resolving here, since that needs the chat client factory.
        if (config.Selection.Magentic?.Model is { } managerModel)
        {
            try { chatClientFactory.Resolve(managerModel); }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"Magentic manager has an unresolvable model: {ex.Message}", ex);
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

        ConversationCompactor? compactor = null;
        if (config.Compaction is { } compactionConfig)
        {
            if (compactionConfig.TriggerTurnCount <= 0)
                throw new InvalidOperationException(
                    $"advanced.compaction.triggerTurnCount must be a positive integer (got {compactionConfig.TriggerTurnCount}). " +
                    "A value of 0 or less would compact the conversation on every turn.");

            if (compactionConfig.KeepRecentTurns < 1)
                throw new InvalidOperationException(
                    "advanced.compaction.keepRecentTurns must be at least 1.");

            if (compactionConfig.KeepRecentTurns >= compactionConfig.TriggerTurnCount)
                throw new InvalidOperationException(
                    $"advanced.compaction.keepRecentTurns ({compactionConfig.KeepRecentTurns}) must be " +
                    $"less than advanced.compaction.triggerTurnCount ({compactionConfig.TriggerTurnCount}).");

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
                    "advanced.compaction.mode is 'intent' but no ChangeTracking.IntentLogPath is configured — " +
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
                    "advanced.contextBudget.cutoverAt and maxSingleTurnInputTokens require " +
                    "compaction. Add advanced.compaction to your team file " +
                    "so the compactor is available when the context budget triggers.");

            if (budget.WarnAt > 0 && budget.CutoverAt > 0 && budget.WarnAt >= budget.CutoverAt)
                throw new InvalidOperationException(
                    $"advanced.contextBudget.warnAt ({budget.WarnAt:N0}) must be less than " +
                    $"CutoverAt ({budget.CutoverAt:N0}).");

            // Warn when WarnTurnTokens >= CutoverAt: a turn that fires the per-turn warning
            // will simultaneously trigger compaction, making the warning a post-hoc note
            // rather than an advance signal. Lower WarnTurnTokens below CutoverAt to get
            // a meaningful early warning before the compaction threshold is crossed.
            if (config.WarnTurnTokens > 0 && budget.CutoverAt > 0 &&
                config.WarnTurnTokens >= budget.CutoverAt)
            {
                loggerFactory.CreateLogger(nameof(OrchestratorBuilder)).LogWarning(
                    "advanced.warnTurnTokens ({WarnTurnTokens:N0}) is >= advanced.contextBudget.cutoverAt ({CutoverAt:N0}). " +
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

    private static (IOrchestrator Orchestrator, fuseraft.Infrastructure.Repository.RepositoryMemoryExtractor? RepoMemoryExtractor, AgentFactory AgentFactory) CreateOrchestrator(
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
        var memoryManager        = knowledge.MemoryManager;
        var projectSlug          = sessionPaths.ProjectSlug;
        var sessionId            = sessionPaths.SessionId;
        var executionStatePath   = sessionPaths.ExecutionStatePath;
        var investigationLogPath = sessionPaths.InvestigationLogPath;

        var aoLogger = loggerFactory.CreateLogger<AgentOrchestrator>();
        var resolvedSandbox = config.Security?.FileSystemSandboxPath is { Length: > 0 } sbx
            ? FuseraftPaths.ExpandPath(sbx) : null;

        // Context Broker (Gap 8): adaptive context pipeline backed by the shared knowledge layer.
        var brokerMemoryStore = new fuseraft.Infrastructure.Repository.RepositoryMemoryStore(FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalRepositoryMemory, projectSlug));
        var contextBroker = new fuseraft.Orchestration.Context.ContextBroker(
            knowledgeLayer,
            brokerMemoryStore,
            knowledgeLayer.ProvenanceRegistry);

        // Shared assembler for agents' context sources (AgentConfig.Context). One instance so session ID updates propagate.
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

        var strategyFactory = new StrategyFactory(eventEmitter, loggerFactory, governanceKernel, evidenceStore, config.TestSelector, resolvedSandbox, humanApprovalService);

        var agentFactory = new AgentFactory(chatClientFactory, infra.PluginRegistry, config.Security, changeTracker, config.Scratchpad, config.Chatroom, governanceKernel, infra.IdentityRegistry, eventEmitter, loggerFactory, BuildSkillsProvider(loggerFactory), infra.ToolArtifactStore, infra.AdaptiveTrimTracker, FileLedger.Shared);

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

        // Each pattern has its own orchestrator; a Steps flow runs on AgentOrchestrator with
        // the state machine selector.
        IOrchestrator orchestrator;

        if (flags.UseAdversarial)
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
            orchestrator = new AgentOrchestrator(config, agentFactory, strategyFactory, aoLogger, changeTracker, eventEmitter, governanceKernel, memoryManager, contextAssembler, contextPipeline, knowledgeStore);
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


        return (orchestrator, repoMemoryExtractor, agentFactory);
    }

    private static AgentSkillsProvider? BuildSkillsProvider(ILoggerFactory loggerFactory)
    {
        var dirs = FuseraftSkillsSources.GetDefaultSearchDirs();
        if (!dirs.Any(Directory.Exists)) return null;

        // Without a logger factory, AgentFileSkillsSource discards its diagnostics (invalid
        // frontmatter, a skill 'name:' that doesn't match its directory name, symlink/path-
        // traversal rejections, ...) — a skill can silently vanish from the catalog with no
        // trace anywhere. Wiring the real factory surfaces those through the same logging
        // pipeline as the rest of the orchestrator.
        return new AgentSkillsProviderBuilder()
            .UseFileSkills(dirs, FuseraftSkillsSources.CreateSourceOptions())
            .UseFileScriptRunner(FuseraftSkillsSources.RunScriptAsync)
            .UseOptions(FuseraftSkillsSources.DisableApproval)
            .UseLoggerFactory(loggerFactory)
            .Build();
    }
}
