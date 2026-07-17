using System.ComponentModel;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Gives an agent read-only introspection into its own actually-resolved tool list, so it
/// can check a capability claim against ground truth instead of reasoning about it from
/// memory or trusting another agent's notes.
///
/// <para>
/// Constructed per-agent in <c>AgentFactory.Create</c>, after the agent's full tool list is
/// resolved — not through the plugin registry, since it needs the final tool-name set as
/// input rather than producing tools that feed into it. Declaring <c>Self</c> in an agent's
/// <c>Plugins:</c> list is a signal <see cref="Agents.AgentToolResolver.ConvertPluginTools"/>
/// skips (matching the <c>Skills</c> pattern), not a normal plugin lookup.
/// </para>
/// </summary>
public sealed class SelfPlugin(IReadOnlySet<string> toolNames)
{
    [Description("Returns 'true' if this agent has the named tool available this turn, " +
                 "'false' otherwise. Call this before claiming you lack a tool or capability " +
                 "— do not guess from memory or trust another agent's session_context notes " +
                 "about what tools exist, since that claim could itself be wrong.")]
    public Task<string> HasCapabilityAsync(
        [Description("Exact tool name to check, e.g. 'patch_file', 'shell_run', 'git_commit'.")]
        string name)
        => Task.FromResult(toolNames.Contains(name) ? "true" : "false");

    [Description("Returns the full list of tool names actually available to this agent this turn.")]
    public Task<string> ListCapabilitiesAsync()
        => Task.FromResult(string.Join(", ", toolNames.OrderBy(n => n, StringComparer.Ordinal)));
}
