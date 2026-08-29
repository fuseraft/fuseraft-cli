using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core.Skills;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>Result of wiring up skills for a REPL session.</summary>
/// <param name="Skills">Every skill discovered, for the startup banner count and <c>$slug</c> direct invocation.</param>
/// <param name="CatalogInstructions">Catalog text to append to the system prompt, or <c>null</c> when no skills were found.</param>
/// <param name="Tools">The <c>load_skill</c>/<c>read_skill_resource</c>/<c>run_skill_script</c> tools, or empty when no skills were found.</param>
internal sealed record ReplSkillsResult(
    IReadOnlyList<AgentSkill> Skills,
    string?                   CatalogInstructions,
    IReadOnlyList<AIFunction> Tools);

/// <summary>
/// Thin REPL-side wiring over Microsoft.Agents.AI's Agent Skills feature. Discovery, frontmatter
/// parsing/validation, and the skill tools themselves all come from
/// <see cref="AgentFileSkillsSource"/>/<see cref="AgentSkillsProvider"/> — the same classes
/// orchestration (<see cref="fuseraft.Cli.OrchestratorBuilder"/>) uses, so a skill is treated
/// identically by both surfaces. This file does not parse or validate anything itself.
/// </summary>
internal static class ReplSkillsLoader
{
    /// <summary>Convenience overload used by <see cref="ReplCommand"/> — searches the default dirs.</summary>
    internal static Task<ReplSkillsResult> BuildAsync(
        IChatClient client, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        BuildAsync(client, loggerFactory, FuseraftSkillsSources.GetDefaultSearchDirs(), cancellationToken);

    /// <summary>
    /// Discovers skills under <paramref name="searchDirs"/> using <paramref name="client"/>
    /// (wrapped in a throwaway <see cref="ChatClientAgent"/> — the only role it plays is
    /// satisfying the framework's generic "which agent is asking" context, since file-based
    /// discovery never invokes it) and returns the discovered skills plus the catalog
    /// instructions and tools an <see cref="AgentSkillsProvider"/> would attach to that agent.
    /// </summary>
    internal static async Task<ReplSkillsResult> BuildAsync(
        IChatClient client, ILoggerFactory loggerFactory, IEnumerable<string> searchDirs, CancellationToken cancellationToken)
    {
        var fileSource = new AgentFileSkillsSource(
            searchDirs,
            FuseraftSkillsSources.RunScriptAsync,
            loggerFactory: loggerFactory);

        // Same caching+dedup pipeline AgentSkillsProvider's own convenience constructor builds
        // internally — applied explicitly here so the skill list used for the startup banner
        // count and $slug direct invocation agrees with what the catalog/tools below show,
        // rather than the raw file source's un-deduplicated, per-search-dir concatenation.
        var source = new DeduplicatingAgentSkillsSource(new CachingAgentSkillsSource(fileSource), loggerFactory);

        var agent = new ChatClientAgent(client);
        IReadOnlyList<AgentSkill> skills = [.. await source.GetSkillsAsync(new AgentSkillsSourceContext(agent, session: null), cancellationToken)];

        if (skills.Count == 0)
            return new ReplSkillsResult(skills, null, []);

        var provider = new AgentSkillsProviderBuilder()
            .UseSource(source)
            .UseOptions(FuseraftSkillsSources.DisableApproval)
            .UseLoggerFactory(loggerFactory)
            .Build();

        // AIContextProvider.InvokingContext is [Experimental] (MAAI001) as of the
        // Microsoft.Agents.AI version fuseraft depends on — see the same suppression pattern
        // in AgentContextCompactionFilters.cs. This is the only place that touches it.
#pragma warning disable MAAI001
        var aiContext = await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(agent, session: null, aiContext: new AIContext()),
            cancellationToken);
#pragma warning restore MAAI001

        var tools = aiContext.Tools?.OfType<AIFunction>().ToList() ?? [];
        return new ReplSkillsResult(skills, aiContext.Instructions, tools);
    }
}
