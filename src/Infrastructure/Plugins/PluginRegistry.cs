using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Central registry that maps plugin names to factory functions.
///
/// Built-in plugins (registered by <see cref="RegisterDefaults"/>):
/// <list type="table">
///   <item><term>FileSystem</term><description>Read, write, list, and delete local files.</description></item>
///   <item><term>Shell</term><description>Execute shell commands and scripts.</description></item>
///   <item><term>Git</term><description>Common Git operations (status, diff, commit, branch).</description></item>
///   <item><term>Http</term><description>HTTP GET/POST/PUT/DELETE to external URLs.</description></item>
///   <item><term>Json</term><description>Format, query, merge, and validate JSON data.</description></item>
///   <item><term>Search</term><description>Find files by name, grep file contents, and locate symbol definitions.</description></item>
///   <item><term>Probe</term><description>Run code snippets, assert outputs with PASS/FAIL verdicts, and test hypotheses using Given/When/Then structure.</description></item>
///   <item><term>CodeExecution</term><description>Docker-backed sandboxed execution and persistent REPL sessions for Python and Node.js.</description></item>
///   <item><term>Handoff</term><description>Type-safe routing signal. Agents call <c>handoff(route_keyword: "...")</c> to hand off to the next step; the tool loop is terminated immediately so no further tools can be called after the signal.</description></item>
///   <item><term>Scratchpad</term><description>Per-agent persistent key-value store that survives across sessions. Registered here with a stub; per-agent instances with real paths are created in <see cref="fuseraft.Infrastructure.Agents.AgentFactory"/>.</description></item>
///   <item><term>Chatroom</term><description>Shared append-only JSONL message log for agent-to-agent coordination. Registered here with a stub; per-agent instances with real paths are created in <see cref="fuseraft.Infrastructure.Agents.AgentFactory"/>.</description></item>
///   <item><term>Changes</term><description>Read-only view of the session change log. Registered here with a stub; the real instance is registered by OrchestratorBuilder when ChangeTracking is configured.</description></item>
///   <item><term>Session</term><description>REPL session metadata, saved-session list, and log file access. Registered here with a stub; ReplCommand replaces it with a real instance bound to the live session.</description></item>
/// </list>
///
/// Add custom plugins via <see cref="Register"/> before the DI host is built.
/// </summary>
public sealed class PluginRegistry : IDisposable
{
    private readonly ILoggerFactory? _loggerFactory;

    public PluginRegistry(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
    }

    private readonly Dictionary<string, Func<object>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    // Cached instances — plugins are created once and reused across agents in the same
    // session. The cache is invalidated when a factory is re-registered (e.g. after Configure()).
    private readonly Dictionary<string, object> _instances =
        new(StringComparer.OrdinalIgnoreCase);

    // Pre-built AIFunction lists from MCP servers (or other pre-built sources).
    private readonly Dictionary<string, IReadOnlyList<AIFunction>> _aiFunctionSets =
        new(StringComparer.OrdinalIgnoreCase);

    // Shared HttpClient (one instance per registry lifetime)
    private readonly HttpClient _sharedHttpClient = BuildSharedHttpClient();

    // Registration

    /// <summary>
    /// Registers all built-in plugins without security constraints (unrestricted defaults).
    /// Call <see cref="Configure"/> after loading the orchestration config to enforce sandbox
    /// and allowlist rules derived from <see cref="SecurityConfig"/>.
    /// </summary>
    public PluginRegistry RegisterDefaults()
    {
        Register("FileSystem", () => new FileSystemPlugin());
        Register("Shell",      () => new ShellPlugin());
        Register("Git",        () => new GitPlugin());
        Register("Http",       () => new HttpPlugin(_sharedHttpClient, logger: _loggerFactory?.CreateLogger<HttpPlugin>()));
        Register("Json",       () => new JsonPlugin());
        Register("Search",     () => new SearchPlugin());
        Register("Document",   () => new DocumentPlugin());
        Register("Probe",      () => new ProbePlugin());
        Register("CodeExecution", () => new CodeExecutionPlugin());
        Register("Handoff",       () => new HandoffPlugin());

        // Stub registrations so `fuseraft plugins` can reflect function names and descriptions.
        // At runtime, AgentFactory replaces Scratchpad, Chatroom, and SubAgent with per-agent
        // instances, and OrchestratorBuilder replaces Changes with a real path-bound instance.
        var scratchpadBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".fuseraft", "scratchpad");
        Register("Scratchpad", () => new ScratchpadPlugin("agent", scratchpadBase));
        var slug = FuseraftPaths.ProjectSlug(Directory.GetCurrentDirectory());
        Register("Chatroom",   () => new ChatroomPlugin("agent", FuseraftPaths.ExpandSessionId(FuseraftPaths.LocalChatroom, "default")));
        Register("Changes",    () => new ChangesPlugin(FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalChanges, slug)));

        // SubAgent stub — AgentFactory replaces this with a real instance that has a
        // live IChatClient and sandboxed FileSystem + Search tools for the sub-agent loop.
        Register("SubAgent", () => new SubAgentPlugin(chatClient: null, explorerTools: []));

        Register("Compaction", () => new CompactionPlugin());

        // Stub registrations for introspection (fuseraft plugins). OrchestratorBuilder
        // calls ConfigureKnowledge() to replace these with a shared-instance version.
        var graphStoreForDecision = new RepositoryGraphStore(FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalRepositoryGraph, slug));
        Register("Decision", () => new DecisionPlugin(
            new AdrRegistry(new AdrStore(FuseraftPaths.LocalDecisions)),
            knowledgeLayer: null));

        Register("Graph", () => new GraphPlugin(graphStoreForDecision));

        Register("Objective", () => new ObjectivePlugin(
            new ObjectiveManager(new ObjectiveStore(FuseraftPaths.LocalObjectives))));

        // Stub — OrchestratorBuilder replaces this with a session-scoped instance.
        Register("SessionContext", () => new SessionContextPlugin(
            Path.Combine(Directory.GetCurrentDirectory(), ".fuseraft", "state", "sessions", "default", "context_summary.md")));

        // Stub — ReplCommand replaces this with a real instance bound to the live session.
        Register("Session", () => new ReplSessionPlugin("stub", DateTime.UtcNow, "unknown", Directory.GetCurrentDirectory()));
        return this;
    }

    /// <summary>
    /// Re-registers the knowledge plugins (Decision, Graph) using the shared
    /// <see cref="IKnowledgeLayer"/> instance created by <c>OrchestratorBuilder</c>.
    /// Call this after the knowledge layer is created so all agents in the session share
    /// the same underlying stores rather than the stub instances from <see cref="RegisterDefaults"/>.
    /// </summary>
    public PluginRegistry ConfigureKnowledge(IKnowledgeLayer knowledgeLayer)
    {
        var layer = (KnowledgeLayer)knowledgeLayer;
        Register("Decision",  () => new DecisionPlugin(layer.AdrRegistry, knowledgeLayer));
        Register("Graph",     () => new GraphPlugin(layer.GraphStore));
        Register("Objective", () => new ObjectivePlugin(new ObjectiveManager(layer.ObjectiveStore)));
        return this;
    }

    /// <summary>
    /// Re-registers the security-sensitive plugins (FileSystem, Shell, Http) using the
    /// constraints from <paramref name="security"/> and optional named API profiles.
    /// Call this after loading the orchestration config so that sandbox, allowlist, and
    /// profile rules are applied at runtime.
    /// </summary>
    public PluginRegistry Configure(
        SecurityConfig security,
        IReadOnlyDictionary<string, ApiProfileConfig>? apiProfiles = null,
        Func<string, Task<bool>>? shellCommandApprover = null,
        FileVersionStore? fileVersionStore = null,
        SessionReadCache? sessionReadCache = null,
        Action? onCacheHit = null,
        IEventSink? eventSink = null)
    {
        var sandboxRoot       = security.FileSystemSandboxPath;
        var allowedHosts      = security.HttpAllowedHosts is { Count: > 0 } h ? (IReadOnlyList<string>)h : null;
        var allowPrivateHosts = security.AllowPrivateHosts;

        // Create ShellPlugin once so FileSystemPlugin can reference its cache invalidator.
        // Both are registered as singletons — the factory lambda returns the same instance.
        var shellInstance = new ShellPlugin(sandboxRoot, shellCommandApprover, security.ShellPolicy, eventSink);
        Register("Shell",      () => shellInstance);
        Register("FileSystem", () => new FileSystemPlugin(sandboxRoot, security.ReadFileSizeLimit, versionStore: fileVersionStore, sessionCache: sessionReadCache, onWrite: shellInstance.InvalidateRunCache, onCacheHit: onCacheHit, exemptedPaths: ["~/.fuseraft/"]));
        Register("Http",       () => new HttpPlugin(_sharedHttpClient, allowedHosts, apiProfiles, allowPrivateHosts, _loggerFactory?.CreateLogger<HttpPlugin>()));
        Register("Document",   () => new DocumentPlugin(sandboxRoot));
        return this;
    }

    /// <summary>
    /// Registers a named plugin factory.
    /// </summary>
    public PluginRegistry Register(string name, Func<object> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        _factories[name] = factory;
        if (_instances.Remove(name, out var old) && old is IDisposable d)
            try { d.Dispose(); } catch { /* best effort */ }
        return this;
    }

    /// <summary>
    /// Registers a pre-built list of <see cref="AIFunction"/> instances (e.g. from an MCP server).
    /// These take precedence over factory-registered plugins with the same name.
    /// </summary>
    public PluginRegistry RegisterAIFunctions(string name, IReadOnlyList<AIFunction> functions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(functions);
        _aiFunctionSets[name] = functions;
        return this;
    }

    // Resolution

    /// <summary>
    /// Tries to resolve a pre-built <see cref="AIFunction"/> list by name (MCP servers register here).
    /// </summary>
    public bool TryGetAIFunctions(string name, [NotNullWhen(true)] out IReadOnlyList<AIFunction>? functions) =>
        _aiFunctionSets.TryGetValue(name, out functions);

    /// <summary>
    /// Tries to resolve a plugin instance by name.
    /// </summary>
    public bool TryGet(string name, [NotNullWhen(true)] out object? plugin)
    {
        if (_factories.TryGetValue(name, out var factory))
        {
            if (!_instances.TryGetValue(name, out plugin))
            {
                plugin = factory();
                _instances[name] = plugin;
            }
            return true;
        }
        plugin = null;
        return false;
    }

    /// <summary>
    /// All registered plugin names — includes both factory-registered and AIFunction set entries.
    /// </summary>
    public IEnumerable<string> RegisteredPlugins =>
        _factories.Keys.Concat(_aiFunctionSets.Keys.Where(k => !_factories.ContainsKey(k)));

    // Plugins whose class-name prefix is omitted from the tool name because their method
    // names are already self-describing (e.g. ReadFile, WriteFile). Adding "file_system_"
    // would break all existing tool references in agent instructions.
    private static readonly HashSet<string> NoPrefixPlugins =
        new(StringComparer.OrdinalIgnoreCase) { "FileSystem", "Handoff", "Skills", "Compaction" };

    /// <summary>
    /// Builds <see cref="AIFunction"/> instances from a plugin object by reflecting over
    /// public instance methods decorated with <see cref="DescriptionAttribute"/>.
    /// Tool names follow the <c>{plugin}_{method}</c> snake_case convention so validators
    /// and agent instructions can reliably match them (e.g. <c>shell_run</c>, <c>git_commit</c>).
    /// Plugins in <see cref="NoPrefixPlugins"/> omit the prefix (e.g. <c>write_file</c>).
    /// </summary>
    public static IReadOnlyList<AIFunction> GetFunctionsFromObject(object plugin)
    {
        var className = plugin.GetType().Name;
        var rawPrefix = className.EndsWith("Plugin", StringComparison.Ordinal) ? className[..^6] : className;
        var prefix    = NoPrefixPlugins.Contains(rawPrefix) ? null : ToSnakeCase(rawPrefix);

        return plugin.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.GetCustomAttribute<DescriptionAttribute>() is not null)
            .Select(m =>
            {
                var methodPart = ToSnakeCase(m.Name.EndsWith("Async", StringComparison.Ordinal) ? m.Name[..^5] : m.Name);
                // Strip redundant prefix/suffix: search_search_files → search_files, plan_create_plan → plan_create, probe_probe_code → probe_code
                if (prefix is not null)
                {
                    if (methodPart.StartsWith(prefix + "_", StringComparison.Ordinal))
                        methodPart = methodPart[(prefix.Length + 1)..];
                    else if (methodPart.EndsWith("_" + prefix, StringComparison.Ordinal))
                        methodPart = methodPart[..^(prefix.Length + 1)];
                }
                var toolName = prefix is null ? methodPart : $"{prefix}_{methodPart}";
                return AIFunctionFactory.Create(m, plugin, new AIFunctionFactoryOptions { Name = toolName });
            })
            .ToList();
    }

    private static string ToSnakeCase(string s) =>
        Regex.Replace(s, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();

    // Lifecycle

    public void Dispose()
    {
        _sharedHttpClient.Dispose();
        foreach (var instance in _instances.Values)
            if (instance is IDisposable d)
                try { d.Dispose(); } catch { /* best effort */ }
        _instances.Clear();
    }

    private static HttpClient BuildSharedHttpClient()
    {
        // Timeout.InfiniteTimeSpan — per-request timeouts are enforced via CancellationTokenSource
        // inside HttpPlugin so agents can specify different timeouts per call.
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("fuseraft/1.0");
        return client;
    }
}
