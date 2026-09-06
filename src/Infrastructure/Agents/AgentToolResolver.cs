using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Infrastructure.Agents;

/// <summary>
/// Resolves the plugin/tool list for an agent (and, separately, for a spawned sub-agent) into
/// <see cref="AIFunction"/>s, including the offload-caching and tool-call-notification wrapping
/// layers. Extracted from <see cref="AgentFactory"/> — single-caller-only from <c>Create</c>,
/// low coupling to the rest of agent construction.
/// </summary>
internal sealed class AgentToolResolver(
    ChatClientFactory chatClientFactory,
    PluginRegistry pluginRegistry,
    SecurityConfig? securityConfig,
    ScratchpadConfig? scratchpadConfig,
    ChatroomConfig? chatroomConfig,
    EventEmitter? eventEmitter)
{
    /// <summary>
    /// Resolves every plugin declared in <paramref name="config"/> into a flat list of
    /// <see cref="AIFunction"/> objects, applying per-plugin capability filters and
    /// registering any <see cref="ITurnResettable"/> instances for turn-start reset into
    /// <paramref name="turnResettables"/> (owned by the caller — shared with
    /// <c>AgentFactory.OnAgentTurnStarting</c>, which resets them before every turn).
    /// </summary>
    public List<AIFunction> ConvertPluginTools(
        AgentConfig config,
        ModelConfig resolvedModel,
        string? sessionId,
        HashSet<ITurnResettable> turnResettables,
        object resettablesLock)
    {
        var tools = new List<AIFunction>();

        foreach (var pluginName in config.Plugins)
        {
            IEnumerable<AIFunction> functions;

            // "Skills" is handled by AgentSkillsProvider (UseAIContextProviders), which
            // injects load_skill / run_skill_script as tools on the chat client pipeline.
            // The Plugins entry is a declaration of intent; no registry lookup is needed.
            if (pluginName.Equals("Skills", StringComparison.OrdinalIgnoreCase))
                continue;
            // "Self" (SelfPlugin) needs the agent's *complete* resolved tool-name set as
            // input, which isn't known until every other plugin in this loop has run — so
            // it's built by AgentFactory.Create right after ConvertPluginTools returns,
            // not resolved here. The Plugins entry is a declaration of intent, like Skills.
            else if (pluginName.Equals("Self", StringComparison.OrdinalIgnoreCase))
                continue;
            // "Scratchpad" is per-agent — each agent gets its own file under the session directory.
            else if (pluginName.Equals("Scratchpad", StringComparison.OrdinalIgnoreCase))
            {
                var basePath = sessionId is { Length: > 0 }
                    ? FuseraftPaths.ExpandSessionId(FuseraftPaths.LocalSessionScratchpad, sessionId)
                    : (scratchpadConfig?.BasePath ?? FuseraftPaths.GlobalScratchpad);
                functions = PluginRegistry.GetFunctionsFromObject(new ScratchpadPlugin(config.Name, basePath));
            }
            // "SubAgent" is per-agent — each agent gets its own lightweight IChatClient
            // (optionally on a different, cheaper model) and a configurable tool set so
            // the sub-agent respects the same sandbox constraints.
            else if (pluginName.Equals("SubAgent", StringComparison.OrdinalIgnoreCase))
            {
                // Allow the sub-agent to run on a different model (e.g. Haiku for cost control).
                var subModel  = string.IsNullOrWhiteSpace(config.SubAgentModel)
                    ? resolvedModel
                    : chatClientFactory.Resolve(new ModelConfig { ModelId = config.SubAgentModel });
                var subClient = chatClientFactory.Create(subModel);

                var explorerTools = BuildSubAgentTools(config, pluginRegistry, securityConfig);

                functions = PluginRegistry.GetFunctionsFromObject(
                    new SubAgentPlugin(subClient, explorerTools,
                        eventEmitter:    eventEmitter,
                        parentAgentName: config.Name,
                        maxToolCalls:    config.SubAgentMaxToolCalls));
            }
            // "Chatroom" is per-agent (own sender name) but all agents share the same file.
            else if (pluginName.Equals("Chatroom", StringComparison.OrdinalIgnoreCase))
            {
                var chatPath = FuseraftPaths.ExpandSessionId(
                    chatroomConfig?.Path ?? FuseraftPaths.LocalChatroom,
                    sessionId ?? "startup");
                functions = PluginRegistry.GetFunctionsFromObject(new ChatroomPlugin(config.Name, chatPath));
            }
            else if (pluginRegistry.TryGetAIFunctions(pluginName, out var aiFunctions))
            {
                functions = aiFunctions;
            }
            else if (pluginRegistry.TryGetAll(pluginName, out var plugins))
            {
                functions = plugins.SelectMany(PluginRegistry.GetFunctionsFromObject);
            }
            else if (pluginName.Equals("Investigation", StringComparison.OrdinalIgnoreCase))
            {
                // Investigation is registered only when ChangeTracking is configured.
                // Skip gracefully rather than crashing at startup.
                continue;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Agent '{config.Name}' references unknown plugin '{pluginName}'. " +
                    $"Registered plugins: {string.Join(", ", pluginRegistry.RegisteredPlugins)}");
            }

            // Apply per-plugin capability filter when the agent declares constraints.
            // Tools absent from the capability map (e.g. MCP tools) pass through unfiltered.
            if (config.Capabilities.TryGetValue(pluginName, out var caps) && caps.Count > 0)
                functions = functions.Where(f => PluginCapabilityMap.IsAllowed(f.Name, caps));

            tools.AddRange(functions);
        }

        // Collect any newly-seen ITurnResettable plugin instances so OnAgentTurnStarting
        // can reset their per-turn state before each agent's turn begins.
        foreach (var pluginName in config.Plugins)
        {
            if (pluginRegistry.TryGet(pluginName, out var obj) && obj is ITurnResettable tr)
                lock (resettablesLock) turnResettables.Add(tr);
        }

        return tools;
    }

    /// <summary>
    /// Wraps every tool with a <see cref="ToolResultOffloadFilter"/> so oversized results
    /// are stored to disk before they enter the conversation history. Applied before the
    /// notification proxy so the stub is what the provider receives, not the raw large content.
    /// Returns <paramref name="tools"/> unchanged when <paramref name="store"/> is null.
    /// </summary>
    public static List<AIFunction> BuildCachingMiddleware(
        List<AIFunction> tools,
        ToolResultArtifactStore? store)
    {
        if (store is not null)
            tools = tools.Select(f => (AIFunction)new ToolResultOffloadFilter(f, store)).ToList();

        return tools;
    }

    /// <summary>
    /// Wraps every tool with a <see cref="NotifyingAIFunction"/> proxy so
    /// <paramref name="onToolCalling"/> fires the moment a tool begins execution, not after
    /// the whole batch finishes. Also records the final tool count for telemetry in
    /// <paramref name="toolCounts"/> (owned by the caller — read by
    /// <c>AgentFactory.GetToolCount</c>). Returns <paramref name="tools"/> unchanged when
    /// <paramref name="onToolCalling"/> is null.
    /// </summary>
    public static List<AIFunction> WrapWithNotifications(
        List<AIFunction> tools,
        string agentName,
        Action<string, string, string?>? onToolCalling,
        ConcurrentDictionary<string, int> toolCounts)
    {
        toolCounts[agentName] = tools.Count;

        // Wrap every tool with a notifying proxy so onToolCalling fires the moment the
        // tool begins execution, not after the whole batch finishes.
        if (onToolCalling is not null)
            return tools.Select(f => (AIFunction)new NotifyingAIFunction(
                f, agentName,
                (agent, name, args) => { onToolCalling(agent, name, args); return Task.CompletedTask; })).ToList();

        return tools;
    }

    // Assembles the tool list for a sub-agent spawned by SubAgentPlugin.
    // When config.SubAgentPlugins is set, uses those plugins (capability-filtered like normal agents).
    // Otherwise falls back to the expanded default: FileSystem read, Search, Shell run, Git read.
    private static List<AIFunction> BuildSubAgentTools(
        AgentConfig config,
        PluginRegistry pluginRegistry,
        SecurityConfig? securityConfig)
    {
        var tools = new List<AIFunction>();

        if (config.SubAgentPlugins is { Count: > 0 })
        {
            // Custom plugin list — resolve and capability-filter the same way BuildTools does.
            foreach (var name in config.SubAgentPlugins)
            {
                IEnumerable<AIFunction> fns;
                if (pluginRegistry.TryGetAIFunctions(name, out var aiFns))
                    fns = aiFns;
                else if (pluginRegistry.TryGetAll(name, out var ps))
                    fns = ps.SelectMany(PluginRegistry.GetFunctionsFromObject);
                else
                    throw new InvalidOperationException(
                        $"Agent '{config.Name}' references unknown sub-agent plugin '{name}'. " +
                        $"Registered plugins: {string.Join(", ", pluginRegistry.RegisteredPlugins)}");

                if (config.Capabilities.TryGetValue(name, out var caps) && caps.Count > 0)
                    fns = fns.Where(f => PluginCapabilityMap.IsAllowed(f.Name, caps));

                tools.AddRange(fns);
            }
            return tools;
        }

        // Default: expanded read-oriented set. FileSystem (sandboxed, read ops only).
        var fsPlugin = new FileSystemPlugin(securityConfig?.FileSystemSandboxPath, exemptedPaths: ["~/.fuseraft/"]);
        var fsOps     = new FileSystemManagementOps(fsPlugin, securityConfig?.FileSystemSandboxPath, exemptedPaths: ["~/.fuseraft/"]);
        tools.AddRange(
            PluginRegistry.GetFunctionsFromObject(fsPlugin)
                          .Concat(PluginRegistry.GetFunctionsFromObject(fsOps))
                          .Where(f => ExplorerToolSets.FileSystemRead.Contains(f.Name)));

        // Search: all tools.
        if (pluginRegistry.TryGet("Search", out var searchPlugin))
            tools.AddRange(PluginRegistry.GetFunctionsFromObject(searchPlugin));

        // Shell: run commands (builds, tests) + env/path helpers.
        if (pluginRegistry.TryGet("Shell", out var shellPlugin))
        {
            tools.AddRange(
                PluginRegistry.GetFunctionsFromObject(shellPlugin)
                              .Where(f => ExplorerToolSets.ShellRead.Contains(f.Name)));
        }

        // Git: read-only operations.
        if (pluginRegistry.TryGet("Git", out var gitPlugin))
        {
            tools.AddRange(
                PluginRegistry.GetFunctionsFromObject(gitPlugin)
                              .Where(f => ExplorerToolSets.GitRead.Contains(f.Name)));
        }

        return tools;
    }
}
