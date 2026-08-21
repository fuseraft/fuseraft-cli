using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.KeyStore;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration;

namespace fuseraft.Cli;

/// <summary>
/// Config loading, binding, and pre-processing: YAML/JSON load, schema-version validation,
/// env-var/session-id token expansion, agent-file resolution and merging, global-default and
/// OS-keychain API-key backfill. Extracted from <see cref="OrchestratorBuilder"/> — a config
/// pre-processing responsibility distinct from orchestrator construction, with some members
/// (<see cref="LoadConfig"/>, <see cref="LoadSecurityConfig"/>, <see cref="InterpolateSessionId"/>,
/// <see cref="VsCodeMode"/>) called from several other CLI commands beyond
/// <see cref="OrchestratorBuilder.BuildAsync"/>.
/// </summary>
public static class OrchestratorConfigLoader
{
    /// <summary>
    /// Set to <c>true</c> by <c>--vscode</c> flag. When true, <c>FUSERAFT_API_KEY</c>
    /// (injected by the VS Code extension) is preferred over the OS keychain for API
    /// key resolution. If the env var is absent the keychain is used as a fallback.
    /// </summary>
    public static bool VsCodeMode { get; set; }

    // -------------------------------------------------------------------------
    // LoadAndExpandConfig
    // -------------------------------------------------------------------------

    public static async Task<(OrchestrationConfig Config, string ProjectSlug)> LoadAndExpandConfig(
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

        ValidateIsolationConstraints(config, loggerFactory);

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
            return JsonSerializer.Deserialize<AgentConfig>(agentEl.GetRawText(), OrchestratorBuilder.BrownfieldJsonOpts)
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
            Capabilities           = inline.Capabilities.Count > 0                      ? inline.Capabilities           : baseConfig.Capabilities,
            MaxToolCallsPerTurn    = inline.MaxToolCallsPerTurn    != 0                 ? inline.MaxToolCallsPerTurn    : baseConfig.MaxToolCallsPerTurn,
            MaxInTurnContextTokens = inline.MaxInTurnContextTokens != 0                 ? inline.MaxInTurnContextTokens : baseConfig.MaxInTurnContextTokens,
            MaxInTurnToolPairs     = inline.MaxInTurnToolPairs     != 0                 ? inline.MaxInTurnToolPairs     : baseConfig.MaxInTurnToolPairs,
            SubAgentModel          = inline.SubAgentModel                               ?? baseConfig.SubAgentModel,
            SubAgentPlugins        = inline.SubAgentPlugins                             ?? baseConfig.SubAgentPlugins,
            RemoteAgent            = inline.RemoteAgent                                 ?? baseConfig.RemoteAgent,
            SkipExecutionState     = inline.SkipExecutionState || baseConfig.SkipExecutionState,
            Context                = inline.Context is { Count: > 0 }                  ? inline.Context                : baseConfig.Context,
        };

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

    /// <summary>
    /// Resolves <paramref name="path"/> relative to <paramref name="sandboxRoot"/> unless it is
    /// already absolute. Expands <c>~</c> home-directory tokens before the rooted check.
    /// Used to normalise validation and change-tracking paths against a configured sandbox root.
    /// </summary>
    public static string ResolveSandboxPath(string path, string sandboxRoot) =>
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

    // Magentic's manager/ledger loop structurally depends on every participant seeing the same
    // shared transcript to coordinate — Isolation.Fresh (which never reads SharedHistory) would
    // silently starve the manager of the progress signal it needs. Reject rather than degrade
    // quietly; the fix (drop Isolation: Fresh or switch orchestrator type) is a one-line config
    // change, not a runtime workaround.
    //
    // Separately, warn (do not fail) when a Fresh agent — the default — declares no Context:
    // sources at all: such an agent receives only the synthesized handoff directive each turn,
    // which is fine for a terminal/leaf agent but likely a misconfiguration for one that needs
    // durable state (brief.json, prior changes, etc.) across turns.
    internal static void ValidateIsolationConstraints(OrchestrationConfig config, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(OrchestratorBuilder));

        if (string.Equals(config.Selection.Type, OrchestratorTypes.Magentic, StringComparison.OrdinalIgnoreCase))
        {
            var freshAgents = config.Agents
                .Where(a => a.Isolation == AgentIsolation.Fresh)
                .Select(a => a.Name)
                .ToList();
            if (freshAgents.Count > 0)
                throw new InvalidOperationException(
                    $"Selection.Type 'magentic' requires every agent to use Isolation: Shared or " +
                    $"Isolation: Fork — the manager's ledger loop depends on shared visibility of " +
                    $"progress across all participants. Agent(s) declaring Isolation: Fresh (the " +
                    $"default): {string.Join(", ", freshAgents)}. Set 'Isolation: Shared' explicitly " +
                    $"on these agents, or on the whole roster if none should isolate.");
        }

        foreach (var agent in config.Agents)
        {
            if (agent.Isolation == AgentIsolation.Fresh && agent.Context is not { Count: > 0 })
                logger.LogWarning(
                    "Agent '{Agent}' uses Isolation: Fresh (the default) with no Context: sources " +
                    "declared — it will receive only the synthesized handoff directive each turn, " +
                    "nothing else. This is fine for a terminal/leaf agent; otherwise declare a " +
                    "Context: block (session_context, brief_field:*, changes_recent:N, own_history:N, " +
                    "etc.) or set 'Isolation: Shared' if this agent needs the group transcript.",
                    agent.Name);
        }
    }
}
