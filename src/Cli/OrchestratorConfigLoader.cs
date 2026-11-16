using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Chat;
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
        var config = Team.TeamConfigLoader.LoadAndCompile(configPath);

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

        // --no-replan: drop every edge that leads back to the Start step, so a run never
        // returns to planning. Useful in CI, where a replan loop would just burn tokens.
        if (noReplan && config.Selection.StateMachine is { } smForReplan)
        {
            var prunedStates = smForReplan.States.ToDictionary(
                kv => kv.Key,
                kv => kv.Value with
                {
                    Transitions = kv.Value.Transitions
                        .Where(t => kv.Key == smForReplan.Initial || t.To != smForReplan.Initial)
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
        return Team.TeamConfigLoader.Load(configPath).Security;
    }

    /// <summary>
    /// Tries to load <paramref name="configPath"/> without constructing full services.
    /// Returns the parsed <see cref="OrchestrationConfig"/> for display purposes.
    /// </summary>
    public static OrchestrationConfig LoadConfig(string configPath)
    {
        return Team.TeamConfigLoader.LoadAndCompile(configPath);
    }

    // Fills in ModelId, Endpoint, and ApiKeyEnvVar from ~/.fuseraft/config on any model
    // config that doesn't set them explicitly. This lets the global config act as a
    // default provider so agent files work without repeating connection details.
    // Per-agent explicit values always win; only empty fields are filled.
    private static OrchestrationConfig ApplyGlobalDefaults(OrchestrationConfig config) =>
        ApplyGlobalDefaults(config, UserConfigStore.Load().Item1);

    internal static OrchestrationConfig ApplyGlobalDefaults(OrchestrationConfig config, UserConfig? globalCfg)
    {
        var globalModelId      = globalCfg is not null && !string.IsNullOrWhiteSpace(globalCfg.ModelId)      ? globalCfg.ModelId      : null;
        var globalEndpoint     = globalCfg is not null && !string.IsNullOrWhiteSpace(globalCfg.Endpoint)     ? globalCfg.Endpoint     : null;
        var globalApiKeyEnvVar = globalCfg is not null && !string.IsNullOrWhiteSpace(globalCfg.ApiKeyEnvVar) ? globalCfg.ApiKeyEnvVar : null;

        // A project config's own Telemetry section always wins; the global default only fills
        // in when the project declares none at all.
        var telemetry = config.Telemetry ?? globalCfg?.Telemetry;

        if (globalModelId is null && globalEndpoint is null && globalApiKeyEnvVar is null)
            return config with { Telemetry = telemetry };

        // The global endpoint and key variable belong to the global provider, so they only fill a
        // model on that provider, or one that can't name its own endpoint. A claude-* model must not
        // inherit an xAI default.
        var globalOrigin = ProviderKeys.Origin(ChatClientFactory.EndpointFor(
            globalModelId ?? string.Empty, globalCfg?.Provider ?? string.Empty, globalEndpoint ?? string.Empty));

        ModelConfig Fill(ModelConfig m)
        {
            var modelId = string.IsNullOrWhiteSpace(m.ModelId) && globalModelId is not null ? globalModelId : m.ModelId;
            var own = ProviderKeys.Origin(ChatClientFactory.EndpointFor(modelId, m.Provider, m.Endpoint));
            var onGlobalProvider = own.Length == 0 || own == globalOrigin;
            return m with
            {
                ModelId      = modelId,
                Endpoint     = string.IsNullOrWhiteSpace(m.Endpoint)     && globalEndpoint     is not null && onGlobalProvider ? globalEndpoint     : m.Endpoint,
                ApiKeyEnvVar = string.IsNullOrWhiteSpace(m.ApiKeyEnvVar) && globalApiKeyEnvVar is not null && onGlobalProvider ? globalApiKeyEnvVar : m.ApiKeyEnvVar,
            };
        }

        var agents = config.Agents.Select(a => a with { Model = Fill(a.Model) }).ToList();

        var models = config.Models.ToDictionary(kv => kv.Key, kv => Fill(kv.Value));

        var sel = config.Selection with
        {
            Magentic = config.Selection.Magentic is not null
                ? config.Selection.Magentic with { Model = config.Selection.Magentic.Model is not null ? Fill(config.Selection.Magentic.Model) : null }
                : null,
        };

        return config with { Agents = agents, Models = models, Selection = sel, Telemetry = telemetry };
    }

    // Injects an OS keychain key as a literal ApiKey on every model config that has neither
    // ApiKey nor ApiKeyEnvVar set: the key saved for that model's endpoint, so a model on one
    // provider never gets another's. Models that already have either field set are left untouched.
    private static async Task<OrchestrationConfig> ApplyKeychainKeyAsync(
        OrchestrationConfig config,
        CancellationToken cancellationToken = default)
    {
        bool NeedsKey(ModelConfig m) =>
            string.IsNullOrWhiteSpace(m.ApiKey) && string.IsNullOrWhiteSpace(m.ApiKeyEnvVar);

        var all = config.Agents.Select(a => a.Model)
            .Concat(config.Models.Values)
            .Append(config.Selection.Magentic?.Model)
            .OfType<ModelConfig>()
            .Where(NeedsKey)
            .ToList();
        if (all.Count == 0) return config;

        // In VS Code mode prefer FUSERAFT_API_KEY (injected by the extension from
        // ~/.fuseraft/config) but fall back to the OS keychain so that runs stay
        // functional after a legacy-key migration has removed the plaintext apiKey
        // field from the config (which causes the extension to stop injecting the
        // env var). The host chose where this run goes, so its key isn't scoped.
        var envKey = VsCodeMode ? Environment.GetEnvironmentVariable("FUSERAFT_API_KEY") : null;
        Func<ModelConfig, ModelConfig> fill;
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            fill = m => NeedsKey(m) ? m with { ApiKey = envKey } : m;
        }
        else
        {
            var store = ApiKeyStoreFactory.Create();

            // A key saved before keys were scoped belongs to the endpoint the global config names.
            var (globalCfg, _) = UserConfigStore.Load();
            if (globalCfg is not null)
                await ProviderKeys.RetrieveAsync(store, ChatClientFactory.EndpointFor(globalCfg.ModelId, globalCfg.Provider, globalCfg.Endpoint), adoptLegacy: true);

            var keys = new Dictionary<string, string>();
            foreach (var origin in all.Select(OriginOf).Where(o => o.Length > 0).Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = await ProviderKeys.RetrieveAsync(store, origin);
                if (!string.IsNullOrWhiteSpace(key)) keys[origin] = key;
            }
            if (keys.Count == 0) return config;

            fill = m => NeedsKey(m) && keys.TryGetValue(OriginOf(m), out var key)
                ? m with { ApiKey = key, ApiKeyOrigin = OriginOf(m) }
                : m;
        }

        var agents = config.Agents.Select(a => a with { Model = fill(a.Model) }).ToList();
        var models  = config.Models.ToDictionary(kv => kv.Key, kv => fill(kv.Value));
        var sel     = config.Selection with
        {
            Magentic = config.Selection.Magentic is not null
                ? config.Selection.Magentic with { Model = config.Selection.Magentic.Model is not null ? fill(config.Selection.Magentic.Model) : null }
                : null,
        };

        return config with { Agents = agents, Models = models, Selection = sel };
    }

    private static string OriginOf(ModelConfig m) =>
        ProviderKeys.Origin(ChatClientFactory.EndpointFor(m.ModelId, m.Provider, m.Endpoint));

    // Separates binding from loading so both BuildAsync and LoadConfig get the same
    // helpful error message when a field type doesn't match the schema.
    /// <summary>Binds a pre-v2 <c>Orchestration:</c> file; used only by <c>fuseraft migrate</c>.</summary>
    internal static (OrchestrationConfig Config, Team.LegacyParts Legacy) LoadLegacyConfig(string configPath)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Config file not found: {configPath}");

        var configuration = YamlConfigLoader.IsYamlPath(configPath)
            ? YamlConfigLoader.LoadAsConfiguration(configPath)
            : new ConfigurationBuilder()
                .AddJsonFile(Path.GetFullPath(configPath), optional: false)
                .Build();

        var root = configuration.GetSection("Orchestration");
        var selection = root.GetSection("Selection").Get<Team.LegacySelection>() ?? new();
        if (selection.Graph is { } graph)
            selection = selection with { Graph = graph with { HasSubGraphs = root.GetSection("Selection:Graph:SubGraphs").Exists() } };

        var dependencies = new Dictionary<string, (List<string>, List<string>)>(StringComparer.Ordinal);
        foreach (var agent in root.GetSection("Agents").GetChildren())
        {
            var produces = agent.GetSection("Produces").Get<List<string>>() ?? [];
            var requires = agent.GetSection("Requires").Get<List<string>>() ?? [];
            if ((produces.Count > 0 || requires.Count > 0) && agent["Name"] is { } name)
                dependencies[name] = (produces, requires);
        }

        var legacy = new Team.LegacyParts
        {
            Selection             = selection,
            Termination           = root.GetSection("Termination").Get<Team.LegacyTermination>(),
            TestAssertionPatterns = root.GetSection("Validation:TestAssertionPatterns").Get<List<string>>(),
            AgentDependencies     = dependencies,
        };
        return (BindConfig(configPath, configuration), legacy);
    }

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
            SubagentModel          = inline.SubagentModel                               ?? baseConfig.SubagentModel,
            SubagentPlugins        = inline.SubagentPlugins                             ?? baseConfig.SubagentPlugins,
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

}
