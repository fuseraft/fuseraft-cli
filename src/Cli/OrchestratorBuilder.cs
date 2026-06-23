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
    fuseraft.Orchestration.DependencyPlanner? DependencyPlanner = null,
    fuseraft.Cli.Telemetry.SessionMetrics?    SessionMetrics    = null);

/// <summary>
/// Builds a ready-to-use <see cref="IOrchestrator"/> directly from a config file path,
/// without requiring a full DI host. Used by CLI commands that load config at runtime.
/// </summary>
public static class OrchestratorBuilder
{
    /// <summary>
    /// Set to <c>true</c> by <c>--vscode</c> flag. When true, <c>FUSERAFT_API_KEY</c>
    /// (injected by the VS Code extension) is preferred over the OS keychain for API
    /// key resolution. If the env var is absent the keychain is used as a fallback.
    /// </summary>
    public static bool VsCodeMode { get; set; }

    // Shared client for API-key validation probes — created once, never disposed.
    private static readonly HttpClient _validationHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly JsonSerializerOptions BrownfieldJsonOpts = new()
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

        var (config, projectSlug) = await LoadAndExpandConfig(
            configPath, loggerFactory, sessionId, noReplan, cancellationToken);

        var (configAfterSecurity, profiles, shellApprover) = ResolveSecurityConfig(
            config, pluginRegistry, hitlMode, humanApprovalService, loggerFactory);
        config = configAfterSecurity;

        config = await BuildSystemPrompt(
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

        var (configAfterStrategy, compactor, skillCurator) = await ValidateAndSelectStrategy(
            config, loggerFactory, chatClientFactory, useMagentic, useGraph, useWorkflow, useAdversarial, useMapReduce, useScatterGather,
            infra.KnowledgeLayer, infra.ObjectiveManager, infra.KnowledgeSandbox, projectSlug,
            infra.IntentLog, infra.EvidenceStore, infra.ExecutionStatePath, infra.InvestigationLogPath,
            sessionId, readCachePath: infra.ReadCachePath, cancellationToken);
        config = configAfterStrategy;

        WireSkillsAndVerifier(config, chatClientFactory, loggerFactory, compactor);

        var orchestrator = CreateOrchestrator(
            config, loggerFactory, chatClientFactory, pluginRegistry,
            governanceKernel, humanApprovalService, hitlMode, useMagentic, useGraph, useWorkflow, useAdversarial, useMapReduce, useScatterGather,
            infra.ChangeTracker, infra.EventEmitter, infra.KnowledgeLayer, infra.ObjectiveManager,
            infra.KnowledgeSandbox, projectSlug, sessionId,
            infra.ExecutionStatePath, infra.InvestigationLogPath,
            infra.EvidenceStore, dependencyPlanner, MemoryManager.FromConfig(config.Memory),
            identityRegistry, infra.ToolArtifactStore,
            out var repoMemoryExtractor);

        return new OrchestratorBuildResult(orchestrator, config, infra.McpManager, compactor, infra.ChangeTracker, infra.EventEmitter, governanceKernel, skillCurator, repoMemoryExtractor, dependencyPlanner, infra.SessionMetrics);
    }

    // -------------------------------------------------------------------------
    // LoadAndExpandConfig
    // -------------------------------------------------------------------------

    private static async Task<(OrchestrationConfig Config, string ProjectSlug)> LoadAndExpandConfig(
        string configPath,
        ILoggerFactory loggerFactory,
        string? sessionId,
        bool noReplan,
        CancellationToken cancellationToken)
    {
        var configuration = YamlConfigLoader.IsYamlPath(configPath)
            ? YamlConfigLoader.LoadAsConfiguration(configPath)
            : new ConfigurationBuilder()
                .AddJsonFile(Path.GetFullPath(configPath), optional: false)
                .Build();

        var config = BindConfig(configPath, configuration);

        ValidateSchemaVersion(config, loggerFactory);

        if (config.Agents.Count == 0)
            throw new InvalidOperationException("Config must define at least one agent.");

        // Expand ${ENV_VAR} tokens in security and API profile config before use.
        config = ExpandEnvVars(config);

        var projectSlug = FuseraftPaths.ProjectSlug(Directory.GetCurrentDirectory());

        // Expand {session_id} across all path-bearing and instruction fields so every
        // downstream consumer receives pre-interpolated values without needing to know
        // about the token.
        if (sessionId is { Length: > 0 })
            config = InterpolateSessionId(config, sessionId, projectSlug);

        // --no-replan: strip all state-machine transitions whose Signal contains "REPLAN"
        // so the session never routes back to the planning phase. Useful in CI or when the
        // developer agent has already planned and a replan loop would just burn tokens.
        if (noReplan && config.Selection.StateMachine is { } smForReplan)
        {
            var prunedStates = smForReplan.States.ToDictionary(
                kv => kv.Key,
                kv => kv.Value with
                {
                    Transitions = kv.Value.Transitions
                        .Where(t => t.Signal is null ||
                                    !t.Signal.Contains("REPLAN", StringComparison.OrdinalIgnoreCase))
                        .ToList()
                });
            config = config with
            {
                Selection = config.Selection with
                {
                    StateMachine = smForReplan with { States = prunedStates }
                }
            };
        }

        // Fill in Endpoint and ApiKeyEnvVar from ~/.fuseraft/config for any agent
        // model that doesn't declare them explicitly.
        config = ApplyGlobalDefaults(config);

        // For models still missing both ApiKey and ApiKeyEnvVar, inject the key
        // stored in the OS keychain so users don't have to set an env var at all.
        config = await ApplyKeychainKeyAsync(config, cancellationToken);

        return (config, projectSlug);
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
                        BriefPath      = ResolveSandboxPath(v.BriefPath,      sandboxRoot),
                        TestReportPath = ResolveSandboxPath(v.TestReportPath, sandboxRoot),
                        ChangeLogPath  = v.ChangeLogPath is not null ? ResolveSandboxPath(v.ChangeLogPath, sandboxRoot) : null,
                    }
                };

            if (config.ChangeTracking is { } ct)
                config = config with
                {
                    ChangeTracking = ct with { Path = ResolveSandboxPath(ct.Path, sandboxRoot) }
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
                    DiscoveryBriefPath    = ResolveSandboxPath(bf.DiscoveryBriefPath,    bfRoot),
                    ConventionProfilePath = ResolveSandboxPath(bf.ConventionProfilePath, bfRoot),
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
    // BuildSystemPrompt
    // -------------------------------------------------------------------------

    private static async Task<OrchestrationConfig> BuildSystemPrompt(
        OrchestrationConfig config,
        string configPath,
        string? sessionId,
        string? specContent,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // Prepend the base system prompt to every agent's instructions.
        // Source priority: SystemPromptPath > SystemPrompt > embedded FUSERAFT.md.
        var basePrompt = ResolveBasePrompt(config, configPath);
        if (basePrompt is not null)
        {
            config = config with
            {
                Agents = config.Agents
                    .Select(a => a with
                    {
                        Instructions = basePrompt + "\n\n" + a.Instructions.TrimStart()
                    })
                    .ToList()
            };
        }

        // Inject the user-supplied spec into every agent's system prompt so all agents
        // remain anchored to it even after context compaction (spec-anchored SDD).
        if (!string.IsNullOrWhiteSpace(specContent))
        {
            var specBlock =
                "## Project Spec (authoritative)\n\n" +
                "The following specification is the single source of truth for this session. " +
                "All plans, brief.json, and implementation decisions must conform to it.\n\n" +
                specContent.Trim();
            config = config with
            {
                Agents = config.Agents
                    .Select(a => a with
                    {
                        Instructions = a.Instructions.TrimEnd() + "\n\n" + specBlock
                    })
                    .ToList()
            };
        }

        // Orient every agent to the local .fuseraft/ folder layout so they never
        // scan it with list_files to discover what is there — they already know.
        // Each agent only sees artifact paths for the plugins it actually has.
        config = config with
        {
            Agents = config.Agents
                .Select(a =>
                {
                    var artifacts = BuildPluginArtifacts(a.Plugins, config, sessionId);
                    var block = FuseraftPaths.BuildFolderOrientationBlock(sessionId ?? "default", pluginArtifacts: artifacts);
                    return a with { Instructions = a.Instructions.TrimEnd() + "\n\n" + block };
                })
                .ToList()
        };

        // Inject OS and recommended shell so agents never have to guess.
        var osBlock = FuseraftPaths.BuildOsEnvironmentBlock();
        config = config with
        {
            Agents = config.Agents
                .Select(a => a with
                {
                    Instructions = a.Instructions.TrimEnd() + "\n\n" + osBlock
                })
                .ToList()
        };

        // Inject .gitignore so agents know which paths to avoid writing to.
        var gitIgnoreBlock = BuildGitIgnoreBlock();
        if (gitIgnoreBlock is not null)
        {
            config = config with
            {
                Agents = config.Agents
                    .Select(a => a with
                    {
                        Instructions = a.Instructions.TrimEnd() + "\n\n" + gitIgnoreBlock
                    })
                    .ToList()
            };
        }

        // Project root orientation: when a sandbox root is configured, inject a prompt block
        // telling agents the canonical root path and warning against double-nested paths.
        // This is the primary prompt-level defence against the vsl/vsl/… path confusion
        // pattern observed in long sessions.
        if (config.Security?.FileSystemSandboxPath is { Length: > 0 } sbxForBlock)
        {
            var sandboxExpanded = FuseraftPaths.ExpandPath(sbxForBlock);
            var projectRootBlock = BuildProjectRootBlock(sandboxExpanded);
            config = config with
            {
                Agents = config.Agents
                    .Select(a => a with
                    {
                        Instructions = a.Instructions.TrimEnd() + "\n\n" + projectRootBlock
                    })
                    .ToList()
            };
        }

        // Inject context items into every agent's system prompt so agents know what
        // reference material is available without burning a tool call on discovery.
        var contextStore = new fuseraft.Infrastructure.Context.ContextStore();
        var contextSummary = await contextStore.BuildPromptSummaryAsync(cancellationToken);
        if (contextSummary is not null)
        {
            config = config with
            {
                Agents = config.Agents
                    .Select(a => a with
                    {
                        Instructions = a.Instructions.TrimEnd() + "\n\n" + contextSummary
                    })
                    .ToList()
            };
        }

        // Brownfield: when a convention profile exists on disk, inject its contents into
        // every agent's system prompt so agents follow project conventions automatically.
        if (config.Brownfield is { ConventionProfilePath: { } conventionPath }
            && File.Exists(conventionPath))
        {
            try
            {
                var profileJson    = await File.ReadAllTextAsync(conventionPath, cancellationToken);
                var profile        = JsonSerializer.Deserialize<ConventionProfile>(profileJson, BrownfieldJsonOpts);
                var conventionBlock = BuildConventionBlock(profile);
                if (conventionBlock is not null)
                {
                    config = config with
                    {
                        Agents = config.Agents
                            .Select(a => a with
                            {
                                Instructions = a.Instructions.TrimEnd() + "\n\n" + conventionBlock
                            })
                            .ToList()
                    };
                }
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger(nameof(OrchestratorBuilder)).LogWarning(
                    "Could not load convention profile from '{Path}': {Message}",
                    conventionPath, ex.Message);
            }
        }

        // Brownfield: when TestSelector is configured, inject the discovery command template into
        // every agent's system prompt so agents run targeted tests without a tool call to find them.
        if (config.TestSelector is { FindRelatedCommand.Length: > 0 } tsCfg)
        {
            var tsBlock = BuildTestSelectorBlock(tsCfg);
            config = config with
            {
                Agents = config.Agents
                    .Select(a => a with
                    {
                        Instructions = a.Instructions.TrimEnd() + "\n\n" + tsBlock
                    })
                    .ToList()
            };
        }

        // Also emit a startup warning when a change envelope is declared without a sandbox —
        // the envelope is enforced by SandboxEnforcementFilter which requires a sandbox root.
        if (config.Security?.ChangeEnvelope is { Count: > 0 }
            && string.IsNullOrEmpty(config.Security.FileSystemSandboxPath))
        {
            loggerFactory.CreateLogger(nameof(OrchestratorBuilder)).LogWarning(
                "Security.ChangeEnvelope is configured but Security.FileSystemSandboxPath is not set. " +
                "The change envelope will not be enforced. Add a FileSystemSandboxPath to enable it.");
        }

        // Warn when FileSystemPermissions is configured without a sandbox root.
        if (config.Security?.FileSystemPermissions is not null
            && string.IsNullOrEmpty(config.Security.FileSystemSandboxPath))
        {
            loggerFactory.CreateLogger(nameof(OrchestratorBuilder)).LogWarning(
                "Security.FileSystemPermissions is configured but Security.FileSystemSandboxPath is not set. " +
                "Filesystem permission globs will not be enforced. Add a FileSystemSandboxPath to enable them.");
        }

        return config;
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
        // concurrent-write conflicts via stat_file + write_file(baseVersion: N).
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
        // so write_file, stat_file, and read_file participate in version-aware conflict
        // detection and cross-turn read deduplication. Thread the cache-hit callback so
        // SessionMetrics can count duplicate reads across the session.
        pluginRegistry.Configure(config.Security ?? new SecurityConfig(), profiles, shellApprover, fileVersionStore, sessionReadCache, onCacheHit: sessionMetrics.RecordCacheHit, eventSink: stateProjector);

        // Session context plugin: shared handoff notes that agents write before routing
        // and read on re-entry. Stored in the global session directory.
        var ctxSummaryPath = sessionId is { Length: > 0 }
            ? FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalSessionContext, sessionId,  projectSlug)
            : FuseraftPaths.ExpandSessionPaths(FuseraftPaths.LocalSessionContext, "default", projectSlug);
        pluginRegistry.Register("SessionContext", () => new fuseraft.Infrastructure.Plugins.SessionContextPlugin(ctxSummaryPath));

        // Narrow, fixed-path artifact writers for recon-style agents (brownfield's
        // Archaeologist, greenfield/swe's Preflight) so they can be locked to FileSystem:[read]
        // via Capabilities while still persisting their own findings. One ArtifactPlugin class
        // registered three times — see ArtifactPlugin's doc comment for why each registration
        // still gives its agent exactly one, uniquely-named write function.
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
        bool useMagentic,
        bool useGraph,
        bool useWorkflow,
        bool useAdversarial,
        bool useMapReduce,
        bool useScatterGather,
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
        if (useMapReduce)
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

        if (useScatterGather)
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
        if (useGraph)
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
        if (useWorkflow)
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
            bool suppressResumptionNote = useMagentic || useAdversarial || useMapReduce || useScatterGather;
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

    private static IOrchestrator CreateOrchestrator(
        OrchestrationConfig config,
        ILoggerFactory loggerFactory,
        ChatClientFactory chatClientFactory,
        PluginRegistry pluginRegistry,
        GovernanceKernel governanceKernel,
        IHumanApprovalService? humanApprovalService,
        bool hitlMode,
        bool useMagentic,
        bool useGraph,
        bool useWorkflow,
        bool useAdversarial,
        bool useMapReduce,
        bool useScatterGather,
        ChangeTracker? changeTracker,
        EventEmitter? eventEmitter,
        fuseraft.Infrastructure.Knowledge.KnowledgeLayer knowledgeLayer,
        fuseraft.Infrastructure.Objectives.ObjectiveManager objectiveManager,
        string knowledgeSandbox,
        string projectSlug,
        string? sessionId,
        string? executionStatePath,
        string? investigationLogPath,
        EvidenceStore? evidenceStore,
        fuseraft.Orchestration.DependencyPlanner? dependencyPlanner,
        MemoryManager? memoryManager,
        IdentityRegistry identityRegistry,
        fuseraft.Infrastructure.Tools.ToolResultArtifactStore toolArtifactStore,
        out fuseraft.Infrastructure.Repository.RepositoryMemoryExtractor? repoMemoryExtractor)
    {
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

        var agentFactory = new AgentFactory(chatClientFactory, pluginRegistry, config.Security, changeTracker, config.Scratchpad, config.Chatroom, governanceKernel, identityRegistry, eventEmitter, loggerFactory, BuildSkillsProvider(), toolArtifactStore);

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

        if (useGraph)
        {
            orchestrator = new GraphOrchestrator(
                config, agentFactory, goLogger,
                changeTracker, eventEmitter, governanceKernel,
                hitlMode ? humanApprovalService : null,
                contextPipeline, knowledgeStore,
                loggerFactory);
        }
        else if (useWorkflow)
        {
            var wfLogger = loggerFactory.CreateLogger<WorkflowOrchestrator>();
            orchestrator  = new WorkflowOrchestrator(
                config, agentFactory, wfLogger,
                changeTracker, eventEmitter);
        }
        else if (useAdversarial)
        {
            var advLogger = loggerFactory.CreateLogger<AdversarialOrchestrator>();
            orchestrator  = new AdversarialOrchestrator(
                config, agentFactory, advLogger,
                changeTracker, eventEmitter, governanceKernel,
                hitlMode ? humanApprovalService : null);
        }
        else if (useMapReduce)
        {
            var mrLogger = loggerFactory.CreateLogger<MapReduceOrchestrator>();
            orchestrator  = new MapReduceOrchestrator(
                config, agentFactory, mrLogger,
                changeTracker, eventEmitter, governanceKernel,
                hitlMode ? humanApprovalService : null);
        }
        else if (useScatterGather)
        {
            var sgLogger = loggerFactory.CreateLogger<ScatterGatherOrchestrator>();
            orchestrator  = new ScatterGatherOrchestrator(
                config, agentFactory, sgLogger,
                changeTracker, eventEmitter, governanceKernel,
                hitlMode ? humanApprovalService : null);
        }
        else if (useMagentic)
        {
            var magCfg        = config.Selection.Magentic!;           // validated above
            var managerModel  = chatClientFactory.Resolve(magCfg.Model!);
            var managerClient = chatClientFactory.Create(managerModel);
            var magLogger     = loggerFactory.CreateLogger<MagenticOrchestrator>();

            orchestrator = new MagenticOrchestrator(
                config, agentFactory, managerClient, magLogger,
                hitlMode ? humanApprovalService : null,
                changeTracker, eventEmitter, governanceKernel,
                contextPipeline, knowledgeStore);
        }
        else
        {
            orchestrator = new AgentOrchestrator(config, agentFactory, strategyFactory, aoLogger, changeTracker, eventEmitter, governanceKernel, memoryManager, contextAssembler, dependencyPlanner, contextPipeline, knowledgeStore);
        }

        // Repository memory extractor — runs after the session to generate candidates.
        // Requires an evidence store to query; skipped when evidence tracking is disabled.
        repoMemoryExtractor = null;
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

        return orchestrator;
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

    /// <summary>
    /// Reads only the <c>Orchestration.Security</c> section from <paramref name="configPath"/>
    /// without binding or resolving agents. Used by lightweight callers (e.g. the REPL) that
    /// need security settings without paying the cost of full config loading.
    /// Returns <c>null</c> when the file does not exist or has no Security section.
    /// </summary>
    public static SecurityConfig? LoadSecurityConfig(string configPath)
    {
        if (!File.Exists(configPath)) return null;

        var configuration = YamlConfigLoader.IsYamlPath(configPath)
            ? YamlConfigLoader.LoadAsConfiguration(configPath)
            : new ConfigurationBuilder()
                .AddJsonFile(Path.GetFullPath(configPath), optional: false)
                .Build();

        return configuration.GetSection("Orchestration:Security").Get<SecurityConfig>();
    }

    /// <summary>
    /// Tries to load <paramref name="configPath"/> without constructing full services.
    /// Returns the parsed <see cref="OrchestrationConfig"/> for display purposes.
    /// </summary>
    public static OrchestrationConfig LoadConfig(string configPath)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Config file not found: {configPath}");

        var configuration = YamlConfigLoader.IsYamlPath(configPath)
            ? YamlConfigLoader.LoadAsConfiguration(configPath)
            : new ConfigurationBuilder()
                .AddJsonFile(Path.GetFullPath(configPath), optional: false)
                .Build();

        return BindConfig(configPath, configuration);
    }

    // Resolves the base system prompt prepended to every agent.
    // Priority: SystemPromptPath (file) > SystemPrompt (inline) > embedded FUSERAFT.md.
    private static string? ResolveBasePrompt(OrchestrationConfig config, string configPath)
    {
        if (!string.IsNullOrWhiteSpace(config.SystemPromptPath))
        {
            var configDir  = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
            var promptPath = Path.IsPathRooted(config.SystemPromptPath)
                ? config.SystemPromptPath
                : Path.GetFullPath(config.SystemPromptPath, configDir);
            return File.ReadAllText(promptPath).Trim();
        }

        if (!string.IsNullOrWhiteSpace(config.SystemPrompt))
            return config.SystemPrompt.Trim();

        // Fall back to the embedded FUSERAFT.md.
        var asm  = typeof(OrchestratorBuilder).Assembly;
        var name = asm.GetManifestResourceNames()
                      .FirstOrDefault(n => n.EndsWith("FUSERAFT.md", StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;

        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }

    // Fills in ModelId, Endpoint, and ApiKeyEnvVar from ~/.fuseraft/config on any model
    // config that doesn't set them explicitly. This lets the global config act as a
    // default provider so agent files work without repeating connection details.
    // Per-agent explicit values always win; only empty fields are filled.
    private static OrchestrationConfig ApplyGlobalDefaults(OrchestrationConfig config)
    {
        var (globalCfg, _) = UserConfigStore.Load();
        var globalModelId      = globalCfg is not null && !string.IsNullOrWhiteSpace(globalCfg.ModelId)      ? globalCfg.ModelId      : null;
        var globalEndpoint     = globalCfg is not null && !string.IsNullOrWhiteSpace(globalCfg.Endpoint)     ? globalCfg.Endpoint     : null;
        var globalApiKeyEnvVar = globalCfg is not null && !string.IsNullOrWhiteSpace(globalCfg.ApiKeyEnvVar) ? globalCfg.ApiKeyEnvVar : null;

        if (globalModelId is null && globalEndpoint is null && globalApiKeyEnvVar is null) return config;

        ModelConfig Fill(ModelConfig m) => m with
        {
            ModelId      = string.IsNullOrWhiteSpace(m.ModelId)      && globalModelId      is not null ? globalModelId      : m.ModelId,
            Endpoint     = string.IsNullOrWhiteSpace(m.Endpoint)     && globalEndpoint     is not null ? globalEndpoint     : m.Endpoint,
            ApiKeyEnvVar = string.IsNullOrWhiteSpace(m.ApiKeyEnvVar) && globalApiKeyEnvVar is not null ? globalApiKeyEnvVar : m.ApiKeyEnvVar,
        };

        var agents = config.Agents.Select(a => a with { Model = Fill(a.Model) }).ToList();

        var models = config.Models.ToDictionary(kv => kv.Key, kv => Fill(kv.Value));

        var sel = config.Selection with
        {
            Model    = config.Selection.Model    is not null ? Fill(config.Selection.Model)    : null,
            Magentic = config.Selection.Magentic is not null
                ? config.Selection.Magentic with { Model = config.Selection.Magentic.Model is not null ? Fill(config.Selection.Magentic.Model) : null }
                : null,
        };

        return config with { Agents = agents, Models = models, Selection = sel };
    }

    // Injects the OS keychain key as a literal ApiKey on every model config that has
    // neither ApiKey nor ApiKeyEnvVar set. The keychain is read at most once per call.
    // Models that already have either field set are left untouched.
    private static async Task<OrchestrationConfig> ApplyKeychainKeyAsync(
        OrchestrationConfig config,
        CancellationToken cancellationToken = default)
    {
        // Quick check: any model actually needs a key?
        bool NeedsKey(ModelConfig m) =>
            string.IsNullOrWhiteSpace(m.ApiKey) && string.IsNullOrWhiteSpace(m.ApiKeyEnvVar);

        bool anyAgentNeedsKey = config.Agents.Any(a => NeedsKey(a.Model))
            || config.Models.Values.Any(NeedsKey)
            || (config.Selection.Model    is not null && NeedsKey(config.Selection.Model))
            || (config.Selection.Magentic?.Model is not null && NeedsKey(config.Selection.Magentic.Model));

        if (!anyAgentNeedsKey) return config;

        // In VS Code mode prefer FUSERAFT_API_KEY (injected by the extension from
        // ~/.fuseraft/config) but fall back to the OS keychain so that runs stay
        // functional after a legacy-key migration has removed the plaintext apiKey
        // field from the config (which causes the extension to stop injecting the
        // env var).
        string? keychainKey;
        if (VsCodeMode)
        {
            var envKey = Environment.GetEnvironmentVariable("FUSERAFT_API_KEY");
            keychainKey = !string.IsNullOrWhiteSpace(envKey)
                ? envKey
                : await ApiKeyStoreFactory.Create().RetrieveAsync();
        }
        else
        {
            keychainKey = await ApiKeyStoreFactory.Create().RetrieveAsync();
        }
        if (string.IsNullOrWhiteSpace(keychainKey)) return config;

        ModelConfig Fill(ModelConfig m) =>
            NeedsKey(m) ? m with { ApiKey = keychainKey } : m;

        var agents = config.Agents.Select(a => a with { Model = Fill(a.Model) }).ToList();
        var models  = config.Models.ToDictionary(kv => kv.Key, kv => Fill(kv.Value));
        var sel     = config.Selection with
        {
            Model    = config.Selection.Model    is not null ? Fill(config.Selection.Model)    : null,
            Magentic = config.Selection.Magentic is not null
                ? config.Selection.Magentic with { Model = config.Selection.Magentic.Model is not null ? Fill(config.Selection.Magentic.Model) : null }
                : null,
        };

        return config with { Agents = agents, Models = models, Selection = sel };
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

    // Separates binding from loading so both BuildAsync and LoadConfig get the same
    // helpful error message when a field type doesn't match the schema.
    private static OrchestrationConfig BindConfig(string configPath, IConfiguration configuration)
    {
        OrchestrationConfig? config;
        try
        {
            config = configuration.GetSection("Orchestration").Get<OrchestrationConfig>();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to bind '{configPath}': {ex.Message} Check that all field types match the expected schema.", ex);
        }

        config = config
            ?? throw new InvalidOperationException($"File '{configPath}' is missing the top-level 'Orchestration' key.");

        var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
        return ResolveAgentFiles(config, configDir);
    }

    // Resolves AgentFile references in the Agents list. For each agent that declares
    // AgentFile, the referenced YAML is loaded as the base AgentConfig and the inline
    // fields are merged on top (inline wins for non-default values).
    private static OrchestrationConfig ResolveAgentFiles(OrchestrationConfig config, string configDir)
    {
        if (config.Agents.All(a => a.AgentFile is null)) return config;

        var resolved = config.Agents.Select(agent =>
        {
            if (agent.AgentFile is null) return agent;

            var filePath = Path.IsPathRooted(agent.AgentFile)
                ? agent.AgentFile
                : Path.GetFullPath(Path.Combine(configDir, agent.AgentFile));

            if (!File.Exists(filePath))
                throw new FileNotFoundException(
                    $"AgentFile not found: '{filePath}'" +
                    (string.IsNullOrEmpty(agent.Name) ? "" : $" (agent '{agent.Name}')"));

            var baseAgent = LoadAgentFile(filePath);
            return MergeAgentConfig(baseAgent, agent);
        }).ToList();

        return config with { Agents = resolved };
    }

    // Loads an agent definition from a YAML file. Supports both bare format (whole
    // file is the AgentConfig object) and wrapped format (top-level "Agent:" key).
    private static AgentConfig LoadAgentFile(string path)
    {
        string yaml;
        try { yaml = File.ReadAllText(path); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Cannot read agent file '{path}': {ex.Message}", ex);
        }

        string json;
        try { json = YamlConfigLoader.ConvertYamlToJson(yaml); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Agent file '{path}' has invalid YAML: {ex.Message}", ex);
        }

        try
        {
            using var doc  = JsonDocument.Parse(json);
            var root       = doc.RootElement;
            // Unwrap "Agent:" top-level key if present.
            var agentEl    = root.TryGetProperty("Agent", out var wrapped) ? wrapped : root;
            return JsonSerializer.Deserialize<AgentConfig>(agentEl.GetRawText(), BrownfieldJsonOpts)
                ?? throw new InvalidOperationException($"Agent file '{path}' deserialized to null.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to parse agent file '{path}': {ex.Message}", ex);
        }
    }

    // Merges an inline AgentConfig on top of a base loaded from AgentFile.
    // Inline wins when its value differs from the C# default for that field type
    // (non-empty string, non-empty collection, non-null, non-zero numeric, true bool).
    // This lets a shared agent file define defaults while individual configs override only
    // what differs (e.g. a different Model or an extra Plugin).
    private static AgentConfig MergeAgentConfig(AgentConfig baseConfig, AgentConfig inline) =>
        baseConfig with
        {
            AgentFile              = null,  // resolved — no file reference on the merged result
            Name                   = !string.IsNullOrEmpty(inline.Name)                 ? inline.Name                   : baseConfig.Name,
            Instructions           = !string.IsNullOrEmpty(inline.Instructions)         ? inline.Instructions           : baseConfig.Instructions,
            Description            = inline.Description                                 ?? baseConfig.Description,
            Model                  = !string.IsNullOrEmpty(inline.Model?.ModelId)       ? inline.Model                  : baseConfig.Model,
            Plugins                = inline.Plugins.Count > 0                           ? inline.Plugins                : baseConfig.Plugins,
            FunctionChoice         = inline.FunctionChoice != "auto"                    ? inline.FunctionChoice         : baseConfig.FunctionChoice,
            TrustScore             = inline.TrustScore     != 0.7                       ? inline.TrustScore             : baseConfig.TrustScore,
            ContextWindow          = inline.ContextWindow                               ?? baseConfig.ContextWindow,
            Capabilities           = inline.Capabilities.Count > 0                     ? inline.Capabilities           : baseConfig.Capabilities,
            MaxToolCallsPerTurn    = inline.MaxToolCallsPerTurn    != 0                 ? inline.MaxToolCallsPerTurn    : baseConfig.MaxToolCallsPerTurn,
            MaxInTurnContextTokens = inline.MaxInTurnContextTokens != 0                 ? inline.MaxInTurnContextTokens : baseConfig.MaxInTurnContextTokens,
            MaxInTurnToolPairs     = inline.MaxInTurnToolPairs     != 0                 ? inline.MaxInTurnToolPairs     : baseConfig.MaxInTurnToolPairs,
#pragma warning disable CS0618 // EnableMemory is obsolete but still merged for backward-compat configs
            EnableMemory           = inline.EnableMemory || baseConfig.EnableMemory,
#pragma warning restore CS0618
            SubAgentModel          = inline.SubAgentModel                               ?? baseConfig.SubAgentModel,
            SubAgentPlugins        = inline.SubAgentPlugins                             ?? baseConfig.SubAgentPlugins,
            RemoteAgent            = inline.RemoteAgent                                 ?? baseConfig.RemoteAgent,
            SkipExecutionState     = inline.SkipExecutionState || baseConfig.SkipExecutionState,
            Context                = inline.Context is { Count: > 0 }                  ? inline.Context                : baseConfig.Context,
        };

    private static string BuildTestSelectorBlock(TestSelectorConfig ts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TEST SELECTOR (incremental test discovery — use this instead of running the full suite):");
        sb.AppendLine($"  FindRelatedCommand: {ts.FindRelatedCommand}");
        if (!string.IsNullOrWhiteSpace(ts.FullSuiteCommand))
            sb.AppendLine($"  FullSuiteCommand:   {ts.FullSuiteCommand}");
        sb.AppendLine();
        sb.Append("For each file you changed, substitute its path for {file} in FindRelatedCommand to discover related tests, then run those tests. Fall back to FullSuiteCommand when no related tests are found.");
        return sb.ToString();
    }

    private static string BuildProjectRootBlock(string sandboxRoot)
    {
        var dirName = Path.GetFileName(sandboxRoot.TrimEnd(Path.DirectorySeparatorChar));
        var sb = new StringBuilder();
        sb.AppendLine("## Project Root (Sandbox)");
        sb.AppendLine($"Sandbox root: {sandboxRoot}");
        sb.AppendLine("All file paths must be relative to this root or absolute. Never include the project directory name as a prefix in a relative path.");
        sb.AppendLine($"  Correct:  src/module/file.py  or  {dirName}/src/module/file.py (absolute)");
        sb.AppendLine($"  Wrong:    {dirName}/{dirName}/src/module/file.py  ← double-nested, file will not exist");
        sb.Append("Files you have already read this session are cached. If the file is unchanged you will see a hint instead of the full content — use grep_in_file for targeted lookup or pass startLine/maxLines for a specific section.");
        return sb.ToString();
    }

    /// <summary>
    /// Produces artifact path descriptors for the plugins an agent actually has, so the
    /// folder orientation block injected into that agent's system prompt only references
    /// paths it can meaningfully use.
    /// </summary>
    private static IEnumerable<(string Path, string Label)> BuildPluginArtifacts(
        List<string> pluginNames,
        OrchestrationConfig config,
        string? sessionId)
    {
        var sid = sessionId ?? "default";
        foreach (var name in pluginNames)
        {
            if (name.Equals("Changes", StringComparison.OrdinalIgnoreCase))
            {
                if (config.ChangeTracking?.Path is { } changesPath)
                    yield return (changesPath, ChangesPlugin.Label);
            }
            else if (name.Equals("SessionContext", StringComparison.OrdinalIgnoreCase))
            {
                yield return (FuseraftPaths.ExpandSessionId(FuseraftPaths.LocalSessionContext, sid), SessionContextPlugin.Label);
            }
            else if (name.Equals("Chatroom", StringComparison.OrdinalIgnoreCase))
            {
                yield return (FuseraftPaths.ExpandSessionId(config.Chatroom?.Path ?? FuseraftPaths.LocalChatroom, sid), ChatroomPlugin.Label);
            }
            else if (name.Equals("Scratchpad", StringComparison.OrdinalIgnoreCase))
            {
                var scratchPath = sessionId is { Length: > 0 }
                    ? FuseraftPaths.ExpandSessionId(FuseraftPaths.LocalSessionScratchpad, sessionId)
                    : FuseraftPaths.ExpandPath(config.Scratchpad?.BasePath ?? FuseraftPaths.GlobalScratchpad);
                yield return (scratchPath, ScratchpadPlugin.Label);
            }
        }
    }

    private static string? BuildGitIgnoreBlock()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), ".gitignore");
        if (!File.Exists(path)) return null;

        const int maxLines = 100;
        var lines     = File.ReadAllLines(path);
        var truncated = lines.Length > maxLines;
        var content   = string.Join('\n', truncated ? lines[..maxLines] : lines);

        var sb = new StringBuilder();
        sb.AppendLine("## .gitignore");
        sb.AppendLine("Avoid writing to paths matched by these patterns. Treat matched paths as non-source (generated, vendored, or sensitive) — read them only when the task explicitly requires it.");
        if (truncated)
            sb.AppendLine($"(truncated to {maxLines} of {lines.Length} lines)");
        sb.AppendLine("```");
        sb.AppendLine(content);
        sb.Append("```");
        return sb.ToString();
    }

    private static string? BuildConventionBlock(ConventionProfile? profile)
    {
        if (profile is null) return null;

        var sb = new StringBuilder();
        sb.AppendLine("PROJECT CONVENTIONS (detected by Archaeologist — follow these in all code you write):");

        if (!string.IsNullOrWhiteSpace(profile.Language))
            sb.AppendLine($"  Language/ecosystem: {profile.Language}");

        if (!string.IsNullOrWhiteSpace(profile.BuildCommand))
            sb.AppendLine($"  Build command: {profile.BuildCommand}");

        if (!string.IsNullOrWhiteSpace(profile.TestCommand))
            sb.AppendLine($"  Test command:  {profile.TestCommand}");

        AppendList(sb, "  Naming:     ", profile.NamingPatterns);
        AppendList(sb, "  Error handling: ", profile.ErrorHandling);
        AppendList(sb, "  Forbidden:  ", profile.ForbiddenPatterns);
        AppendList(sb, "  Tests:      ", profile.TestPatterns);
        AppendList(sb, "  Structure:  ", profile.StructuralNotes);

        var result = sb.ToString().TrimEnd();
        return result.Length > "PROJECT CONVENTIONS (detected by Archaeologist — follow these in all code you write):".Length
            ? result
            : null;
    }

    private static void AppendList(StringBuilder sb, string label, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;
        foreach (var item in items)
            sb.AppendLine($"{label}{item}");
    }

    /// <summary>
    /// Expands <c>${ENV_VAR}</c> tokens in the security and API profile sections of the config.
    /// Expansion is performed at startup so that secrets stay in environment variables and
    /// never appear in agent instructions or conversation history.
    /// </summary>
    private static OrchestrationConfig ExpandEnvVars(OrchestrationConfig config)
    {
        // Expand HttpAllowedHosts so ${SNOW_INSTANCE} style entries work.
        var expandedHosts = config.Security.HttpAllowedHosts
            .Select(ProcessHelper.ExpandEnvTokens)
            .ToList();

        var expandedSecurity = config.Security with { HttpAllowedHosts = expandedHosts };

        // Expand ApiProfiles: BaseUrl and every header value.
        var expandedProfiles = config.ApiProfiles
            .ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value with
                {
                    BaseUrl        = ProcessHelper.ExpandEnvTokens(kvp.Value.BaseUrl),
                    DefaultHeaders = kvp.Value.DefaultHeaders
                        .ToDictionary(
                            h => h.Key,
                            h => ProcessHelper.ExpandEnvTokens(h.Value),
                            StringComparer.OrdinalIgnoreCase),
                },
                StringComparer.OrdinalIgnoreCase);

        return config with
        {
            Security    = expandedSecurity,
            ApiProfiles = expandedProfiles,
        };
    }

    internal static OrchestrationConfig InterpolateSessionId(OrchestrationConfig config, string sessionId, string projectSlug)
    {
        string  E(string  s) => FuseraftPaths.ExpandSessionPaths(s, sessionId, projectSlug);
        string? En(string? s) => s is null ? null : E(s);
        string  Et(string s) => FuseraftPaths.ExpandTextTokens(s, sessionId, projectSlug);

        return config with
        {
            Agents = config.Agents
                .Select(a => a with { Instructions = Et(a.Instructions) })
                .ToList(),

            Validation = config.Validation is { } v
                ? v with
                {
                    BriefPath      = E(v.BriefPath),
                    TestReportPath = E(v.TestReportPath),
                    ChangeLogPath  = En(v.ChangeLogPath),
                }
                : null,

            Contracts = config.Contracts is { Count: > 0 } contracts
                ? contracts
                    .Select(c => c with
                    {
                        Requires = c.Requires
                            .Select(p => p with
                            {
                                Path          = En(p.Path),
                                Source        = En(p.Source),
                                PatternSource = En(p.PatternSource),
                            })
                            .ToList(),
                    })
                    .ToList()
                : config.Contracts,

            Brownfield = config.Brownfield is { } bf
                ? bf with
                {
                    DiscoveryBriefPath    = E(bf.DiscoveryBriefPath),
                    ConventionProfilePath = E(bf.ConventionProfilePath),
                }
                : null,

            Chatroom = config.Chatroom is { } ch
                ? ch with { Path = E(ch.Path) }
                : null,

            ChangeTracking = config.ChangeTracking is { } ct
                ? ct with { Path = E(ct.Path), IntentLogPath = E(ct.ResolveIntentLogPath()) }
                : null,

            Events = config.Events is { } ev
                ? ev with { Path = E(ev.Path) }
                : null,

            EvidenceStore = config.EvidenceStore is { } es
                ? es with { Path = E(es.Path) }
                : null,
        };
    }

    private static AgentSkillsProvider? BuildSkillsProvider()
    {
        // Project-native → project cross-client → user-native → user cross-client → built-in.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), ".fuseraft", "skills"),
            Path.Combine(Directory.GetCurrentDirectory(), ".agents",   "skills"),
            Path.Combine(home, ".fuseraft", "skills"),
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

    /// <summary>
    /// Resolves <paramref name="path"/> relative to <paramref name="sandboxRoot"/> unless it is
    /// already absolute. Expands <c>~</c> home-directory tokens before the rooted check.
    /// Used to normalise validation and change-tracking paths against a configured sandbox root.
    /// </summary>
    private static string ResolveSandboxPath(string path, string sandboxRoot) =>
        Path.IsPathRooted(ProcessHelper.ExpandHome(path))
            ? path
            : Path.GetFullPath(ProcessHelper.ExpandHome(path), sandboxRoot);

    // Known config schema versions. Any version not in this set triggers a warning.
    private static readonly IReadOnlySet<string> KnownSchemaVersions =
        new HashSet<string>(StringComparer.Ordinal) { "2026-05" };

    private static void ValidateSchemaVersion(OrchestrationConfig config, ILoggerFactory loggerFactory)
    {
        if (config.SchemaVersion is null) return;

        var logger = loggerFactory.CreateLogger(nameof(OrchestratorBuilder));
        if (!KnownSchemaVersions.Contains(config.SchemaVersion))
            logger.LogWarning(
                "Config declares schema_version '{SchemaVersion}' which is not recognized by this build of fuseraft-cli. " +
                "Some fields may be silently ignored or default incorrectly. " +
                "Known versions: {KnownVersions}",
                config.SchemaVersion,
                string.Join(", ", KnownSchemaVersions));
        else
            logger.LogDebug("Config schema_version '{SchemaVersion}' is valid.", config.SchemaVersion);
    }
}
