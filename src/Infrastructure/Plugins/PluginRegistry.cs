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
///   <item><term>Document</term><description>Read-only text extraction from PDF, DOCX, PPTX, and XLSX files.</description></item>
///   <item><term>Probe</term><description>Run code snippets, assert outputs with PASS/FAIL verdicts, and test hypotheses using Given/When/Then structure.</description></item>
///   <item><term>CodeExecution</term><description>Docker-backed sandboxed execution and persistent REPL sessions for Python and Node.js.</description></item>
///   <item><term>Handoff</term><description>Type-safe routing signal. Agents call <c>handoff(route_keyword: "...")</c> to hand off to the next step; the tool loop is terminated immediately so no further tools can be called after the signal.</description></item>
///   <item><term>Scratchpad</term><description>Per-agent persistent key-value store that survives across sessions. Registered here with a stub; per-agent instances with real paths are created in <see cref="fuseraft.Infrastructure.Agents.AgentFactory"/>.</description></item>
///   <item><term>Chatroom</term><description>Shared append-only JSONL message log for agent-to-agent coordination. Registered here with a stub; per-agent instances with real paths are created in <see cref="fuseraft.Infrastructure.Agents.AgentFactory"/>.</description></item>
///   <item><term>Changes</term><description>Read-only view of the session change log. Registered here with a stub; the real instance is registered by OrchestratorBuilder when ChangeTracking is configured.</description></item>
///   <item><term>Investigation</term><description>Durable hypothesis/root-cause log. Only registered by OrchestratorBuilder when ChangeTracking is configured — no stub here, so it is absent from <c>fuseraft plugins</c> until a session with ChangeTracking creates it.</description></item>
///   <item><term>Compaction</term><description>On-demand history compaction via <c>compact_conversation</c>; a no-op unless the orchestration config also sets <c>Compaction</c>.</description></item>
///   <item><term>Decision</term><description>Architecture Decision Registry (ADR) search/read/create/supersede. Registered here with a stub; <see cref="ConfigureKnowledge"/> replaces it with an instance sharing the session's <see cref="IKnowledgeLayer"/>.</description></item>
///   <item><term>Graph</term><description>Read-only queries over the repository semantic graph. Registered here with a stub; <see cref="ConfigureKnowledge"/> replaces it with the session's shared graph store.</description></item>
///   <item><term>Objective</term><description>Long-horizon objective tracking across orchestration runs. Registered here with a stub; <see cref="ConfigureKnowledge"/> replaces it with the session's shared objective store.</description></item>
///   <item><term>SessionContext</term><description>Shared handoff-note summary for the current orchestration session. Registered here with a stub; OrchestratorBuilder replaces it with a session-scoped instance.</description></item>
///   <item><term>Conventions, DiscoveryBrief, Preflight, Brief, BriefReview, AuditFindings, RemediationPlan, OpsPlan, ResearchFindings, ResearchReview</term><description>Fixed-target-path <see cref="ArtifactPlugin"/> writers for recon/planning-style agents — one class registered many times under different names/paths/tool identities. See <see cref="ArtifactPlugin"/>'s doc comment.</description></item>
///   <item><term>Session</term><description>REPL session metadata, saved-session list, and log file access. Registered here with a stub; ReplCommand replaces it with a real instance bound to the live session.</description></item>
/// </list>
///
/// Not listed here because they are never resolved through this registry's <c>Plugins:</c>-name
/// mechanism: <c>Todo</c> (REPL-only, wired directly by <c>ReplCommand</c>) and <c>Skills</c>
/// (REPL-only, registered automatically when at least one skill is installed).
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

    // Each plugin name maps to a list of factories — almost always one, except "FileSystem",
    // which registers a second object (FileSystemManagementOps) sharing its per-turn state.
    // See RegisterAdditional/TryGetAll.
    private readonly Dictionary<string, List<Func<object>>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    // Cached instances — plugins are created once and reused across agents in the same
    // session. The cache is invalidated when a factory is re-registered (e.g. after Configure()).
    private readonly Dictionary<string, List<object>> _instances =
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
        // Constructed eagerly (not inside the factory lambda) so both registrations under
        // "FileSystem" close over the same instance — FileSystemManagementOps borrows its
        // per-turn HashSets by reference. See PluginRegistry's multi-object-per-name support.
        var fsPlugin = new FileSystemPlugin();
        Register("FileSystem", () => fsPlugin);
        RegisterAdditional("FileSystem", () => new FileSystemManagementOps(fsPlugin));
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
        Register("Scratchpad", () => new ScratchpadPlugin("agent", FuseraftPaths.GlobalScratchpad));
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

        // Stubs — OrchestratorBuilder replaces these with session-scoped instances. All are the
        // same ArtifactPlugin class registered under different names/paths/tool identities —
        // see ArtifactPlugin's doc comment for why one class can serve every recon/planning-style
        // agent without any of them seeing a write function meant for a different agent.
        var defaultArtifactBase = Path.Combine(Directory.GetCurrentDirectory(), ".fuseraft", "state", "sessions", "default");
        Register("Conventions", () => new ArtifactPlugin(
            Path.Combine(defaultArtifactBase, "conventions.json"), ArtifactFormat.Json,
            "write_file_conventions", ReconDescriptions.Conventions));
        Register("DiscoveryBrief", () => new ArtifactPlugin(
            Path.Combine(defaultArtifactBase, "brief.brownfield.json"), ArtifactFormat.Json,
            "write_file_discovery_brief", ReconDescriptions.DiscoveryBrief));
        Register("Preflight", () => new ArtifactPlugin(
            Path.Combine(defaultArtifactBase, "preflight.json"), ArtifactFormat.Json,
            "write_file_preflight", ReconDescriptions.Preflight));
        Register("Brief", () => new ArtifactPlugin(
            Path.Combine(defaultArtifactBase, "brief.json"), ArtifactFormat.Json,
            "write_file_brief", ReconDescriptions.Brief));
        Register("BriefReview", () => new ArtifactPlugin(
            Path.Combine(defaultArtifactBase, "brief-review.json"), ArtifactFormat.Json,
            "write_file_brief_review", ReconDescriptions.BriefReview));

        // Stubs — Configure() replaces these with sandbox-rooted instances.
        Register("AuditFindings", () => new ArtifactPlugin(
            Path.Combine(Directory.GetCurrentDirectory(), FuseraftPaths.LocalAuditFindings), ArtifactFormat.Json,
            "write_file_audit_findings", ReconDescriptions.AuditFindings));
        Register("RemediationPlan", () => new ArtifactPlugin(
            Path.Combine(Directory.GetCurrentDirectory(), FuseraftPaths.LocalRemediationPlan), ArtifactFormat.Json,
            "write_file_remediation_plan", ReconDescriptions.RemediationPlan));
        Register("OpsPlan", () => new ArtifactPlugin(
            Path.Combine(Directory.GetCurrentDirectory(), FuseraftPaths.LocalOpsPlan), ArtifactFormat.Yaml,
            "write_file_ops_plan", ReconDescriptions.OpsPlan));
        Register("ResearchFindings", () => new ArtifactPlugin(
            Path.Combine(Directory.GetCurrentDirectory(), FuseraftPaths.LocalResearchFindings), ArtifactFormat.Md,
            "write_file_research_findings", ReconDescriptions.ResearchFindings));
        Register("ResearchReview", () => new ArtifactPlugin(
            Path.Combine(Directory.GetCurrentDirectory(), FuseraftPaths.LocalResearchReview), ArtifactFormat.Json,
            "write_file_research_review", ReconDescriptions.ResearchReview));

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

        // Same eager-construction-plus-shared-closure pattern as RegisterDefaults — both
        // "FileSystem" registrations must share one FileSystemPlugin instance's per-turn state.
        var fsPlugin = new FileSystemPlugin(sandboxRoot, security.ReadFileSizeLimit, versionStore: fileVersionStore, sessionCache: sessionReadCache, onWrite: shellInstance.InvalidateRunCache, onCacheHit: onCacheHit, exemptedPaths: ["~/.fuseraft/"]);
        Register("FileSystem", () => fsPlugin);
        RegisterAdditional("FileSystem", () => new FileSystemManagementOps(
            fsPlugin, sandboxRoot, sessionCache: sessionReadCache, versionStore: fileVersionStore, exemptedPaths: ["~/.fuseraft/"]));
        Register("Http",       () => new HttpPlugin(_sharedHttpClient, allowedHosts, apiProfiles, allowPrivateHosts, _loggerFactory?.CreateLogger<HttpPlugin>()));
        Register("Document",   () => new DocumentPlugin(sandboxRoot));

        // Resolve against the same root FileSystemPlugin uses, so each artifact lands exactly
        // where its downstream reader's read_file expects it regardless of sandbox configuration.
        // Same rationale as the session-scoped Conventions/DiscoveryBrief/Preflight/Brief/
        // BriefReview registrations in OrchestratorBuilder — these four just have no
        // {session_id}/{project_slug} in their path, so they're sandbox- not session-scoped.
        var artifactBase = sandboxRoot is not null ? FuseraftPaths.ExpandPath(sandboxRoot) : Directory.GetCurrentDirectory();
        Register("AuditFindings", () => new ArtifactPlugin(
            Path.Combine(artifactBase, FuseraftPaths.LocalAuditFindings), ArtifactFormat.Json,
            "write_file_audit_findings", ReconDescriptions.AuditFindings));
        Register("RemediationPlan", () => new ArtifactPlugin(
            Path.Combine(artifactBase, FuseraftPaths.LocalRemediationPlan), ArtifactFormat.Json,
            "write_file_remediation_plan", ReconDescriptions.RemediationPlan));
        Register("OpsPlan", () => new ArtifactPlugin(
            Path.Combine(artifactBase, FuseraftPaths.LocalOpsPlan), ArtifactFormat.Yaml,
            "write_file_ops_plan", ReconDescriptions.OpsPlan));
        Register("ResearchFindings", () => new ArtifactPlugin(
            Path.Combine(artifactBase, FuseraftPaths.LocalResearchFindings), ArtifactFormat.Md,
            "write_file_research_findings", ReconDescriptions.ResearchFindings));
        Register("ResearchReview", () => new ArtifactPlugin(
            Path.Combine(artifactBase, FuseraftPaths.LocalResearchReview), ArtifactFormat.Json,
            "write_file_research_review", ReconDescriptions.ResearchReview));
        return this;
    }

    /// <summary>
    /// Registers a named plugin factory, replacing any existing registration(s) under
    /// <paramref name="name"/> (disposing their cached instances). Use
    /// <see cref="RegisterAdditional"/> to add a second object under an existing name instead
    /// of replacing it.
    /// </summary>
    public PluginRegistry Register(string name, Func<object> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        _factories[name] = [factory];
        DisposeCached(name);
        return this;
    }

    /// <summary>
    /// Registers an additional plugin factory under an existing name, without replacing what's
    /// already registered. <see cref="GetFunctionsFromObject"/> is applied to every object
    /// registered under a name and the results concatenated — used to split "FileSystem"'s
    /// tool surface across <see cref="FileSystemPlugin"/> and
    /// <see cref="FileSystemManagementOps"/> while keeping one registered name.
    /// </summary>
    public PluginRegistry RegisterAdditional(string name, Func<object> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        if (!_factories.TryGetValue(name, out var list))
        {
            list = [];
            _factories[name] = list;
        }
        list.Add(factory);
        DisposeCached(name);
        return this;
    }

    private void DisposeCached(string name)
    {
        if (_instances.Remove(name, out var old))
            foreach (var o in old)
                if (o is IDisposable d)
                    try { d.Dispose(); } catch { /* best effort */ }
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
    /// Tries to resolve a plugin instance by name. When multiple objects are registered under
    /// <paramref name="name"/> (see <see cref="RegisterAdditional"/>), returns the first one —
    /// callers that need every object's tool surface should use <see cref="TryGetAll"/> instead.
    /// </summary>
    public bool TryGet(string name, [NotNullWhen(true)] out object? plugin)
    {
        if (TryGetAll(name, out var all) && all.Count > 0)
        {
            plugin = all[0];
            return true;
        }
        plugin = null;
        return false;
    }

    /// <summary>
    /// Tries to resolve every plugin instance registered under <paramref name="name"/> — a
    /// list of one for every plugin except "FileSystem", which registers a second object
    /// (see <see cref="RegisterAdditional"/>).
    /// </summary>
    public bool TryGetAll(string name, [NotNullWhen(true)] out IReadOnlyList<object>? plugins)
    {
        if (_factories.TryGetValue(name, out var factories))
        {
            if (!_instances.TryGetValue(name, out var built))
            {
                built = factories.Select(f => f()).ToList();
                _instances[name] = built;
            }
            plugins = built;
            return true;
        }
        plugins = null;
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
        new(StringComparer.OrdinalIgnoreCase)
            { "FileSystem", "FileSystemManagementOps", "Handoff", "Skills", "Compaction" };

    /// <summary>
    /// Builds <see cref="AIFunction"/> instances from a plugin object by reflecting over
    /// public instance methods decorated with <see cref="DescriptionAttribute"/>.
    /// Tool names follow the <c>{plugin}_{method}</c> snake_case convention so validators
    /// and agent instructions can reliably match them (e.g. <c>shell_run</c>, <c>git_commit</c>).
    /// Plugins in <see cref="NoPrefixPlugins"/> omit the prefix (e.g. <c>write_file</c>).
    /// </summary>
    public static IReadOnlyList<AIFunction> GetFunctionsFromObject(object plugin)
    {
        // ArtifactPlugin carries its own tool name/description per instance instead of
        // deriving them from the class name — the same class is registered many times under
        // different names (Conventions, DiscoveryBrief, Preflight, AuditFindings, ...) and
        // each registration still needs its own uniquely-named write function so an agent
        // that includes two of them never sees a name collision.
        if (plugin is ArtifactPlugin artifact)
        {
            var method = typeof(ArtifactPlugin).GetMethod(nameof(ArtifactPlugin.WriteFileAsync))!;
            return [AIFunctionFactory.Create(method, artifact, new AIFunctionFactoryOptions
            {
                Name        = artifact.ToolName,
                Description = artifact.Description,
            })];
        }

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
        foreach (var list in _instances.Values)
            foreach (var instance in list)
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
