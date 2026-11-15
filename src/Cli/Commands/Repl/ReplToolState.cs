using Microsoft.Extensions.AI;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Storage;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Which of the chat's tools are on: the saved defaults (<c>repl.plugins</c>, <c>repl.disabledTools</c>) a chat starts
/// with, the changes <c>/tools</c> and the panel's tools menu make, and saving those back as the defaults.
/// </summary>
internal static class ReplToolState
{
    /// <summary>What a chat starts with off: optional plugins nobody turned on, and every <c>repl.disabledTools</c> entry it has.</summary>
    internal static (HashSet<string> Categories, HashSet<string> Tools) Defaults(
        ReplDefaultsConfig? repl, IEnumerable<string> flagPlugins, IReadOnlyDictionary<string, List<AIFunction>> toolsByCategory)
    {
        var on = new HashSet<string>(flagPlugins, StringComparer.OrdinalIgnoreCase);
        if (repl?.Plugins is { } plugins) on.UnionWith(plugins);

        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tools      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in toolsByCategory.Keys)
            if (ReplToolCatalog.OptionalPlugins.Contains(category) && !on.Contains(category))
                categories.Add(category);

        var toolNames = new HashSet<string>(toolsByCategory.Values.SelectMany(v => v).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in repl?.DisabledTools ?? [])
        {
            if (toolsByCategory.Keys.FirstOrDefault(k => k.Equals(entry, StringComparison.OrdinalIgnoreCase)) is { } category)
                categories.Add(category);
            else if (toolNames.Contains(entry))
                tools.Add(entry);
        }
        return (categories, tools);
    }

    /// <summary>Puts the chat's tools back to its saved defaults, keeping safe mode's blocks while it's on.</summary>
    internal static void ApplyDefaults(ReplSessionContext ctx, ReplDefaultsConfig? repl)
    {
        var (categories, tools) = Defaults(repl, ctx.FlagPlugins, ctx.ToolsByCategory);
        SetUserDisabled(ctx, categories);
        ctx.DisabledTools.Clear();
        ctx.DisabledTools.UnionWith(tools);
        ctx.ChatOptions = ctx.BuildChatOptions();
    }

    /// <summary>The categories the user has off. Safe mode's own blocks aren't among them: they lift when it ends.</summary>
    internal static HashSet<string> UserDisabled(ReplSessionContext ctx) =>
        ctx.SafeMode && ctx.PreSafeDisabled is { } beforeSafeMode
            ? new HashSet<string>(beforeSafeMode.Concat(ctx.DisabledCategories.Where(c => !IsSafeModeCategory(c))), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(ctx.DisabledCategories, StringComparer.OrdinalIgnoreCase);

    /// <summary>Turns a category on or off for this chat. Under safe mode the change also holds once it ends.</summary>
    internal static void SetCategory(ReplSessionContext ctx, string category, bool on)
    {
        var disabled = UserDisabled(ctx);
        if (on) disabled.Remove(category); else disabled.Add(category);
        SetUserDisabled(ctx, disabled);
    }

    private static void SetUserDisabled(ReplSessionContext ctx, IEnumerable<string> categories)
    {
        var wanted = categories.ToList();
        ctx.DisabledCategories.Clear();
        ctx.DisabledCategories.UnionWith(wanted);
        if (ctx.SafeMode && ctx.PreSafeDisabled is not null)
        {
            ctx.PreSafeDisabled = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
            foreach (var c in ReplSessionContext.SafeModePlugins.Where(ctx.ToolsByCategory.ContainsKey))
                ctx.DisabledCategories.Add(c);
        }
    }

    internal static bool IsSafeModeCategory(string category) =>
        ReplSessionContext.SafeModePlugins.Contains(category, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The saved defaults that would make a new chat start as this one is now. Entries for tools this chat doesn't have
    /// (an MCP server that isn't connected, say) are kept as they were.
    /// </summary>
    internal static (List<string> Plugins, List<string> DisabledTools) AsDefaults(ReplSessionContext ctx, ReplDefaultsConfig? repl)
    {
        var userDisabled = UserDisabled(ctx);
        var toolNames = new HashSet<string>(ctx.ToolsByCategory.Values.SelectMany(v => v).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        bool InThisChat(string entry) => ctx.ToolsByCategory.ContainsKey(entry) || toolNames.Contains(entry);

        var plugins = (repl?.Plugins ?? []).Where(p => !ctx.ToolsByCategory.ContainsKey(p)).ToList();
        plugins.AddRange(ReplToolCatalog.Categories
            .Where(c => c.Optional && ctx.ToolsByCategory.ContainsKey(c.Name) && !userDisabled.Contains(c.Name))
            .Select(c => c.Name));

        var disabled = (repl?.DisabledTools ?? []).Where(e => !InThisChat(e)).ToList();
        disabled.AddRange(ctx.ToolsByCategory.Keys.Where(c => userDisabled.Contains(c) && !ReplToolCatalog.OptionalPlugins.Contains(c)));
        disabled.AddRange(ctx.ToolsByCategory.Values.SelectMany(v => v).Select(f => f.Name)
            .Where(ctx.DisabledTools.Contains).Distinct(StringComparer.OrdinalIgnoreCase));
        return (plugins, disabled);
    }

    /// <summary>Whether a new chat would start with the tools this one has on now.</summary>
    internal static bool MatchesDefaults(ReplSessionContext ctx, ReplDefaultsConfig? repl)
    {
        var (categories, tools) = Defaults(repl, ctx.FlagPlugins, ctx.ToolsByCategory);
        return categories.SetEquals(UserDisabled(ctx)) && tools.SetEquals(ctx.DisabledTools);
    }

    /// <summary>The chat's tools for the panel's tools menu: every category it has and each tool in it, on or off.</summary>
    internal static object Event(ReplSessionContext ctx, ReplDefaultsConfig? savedDefaults)
    {
        var userDisabled = UserDisabled(ctx);
        var ordered = ReplToolCatalog.Categories.Select(c => c.Name).Where(ctx.ToolsByCategory.ContainsKey)
            .Concat(ctx.ToolsByCategory.Keys.Where(k => !ReplToolCatalog.Categories.Any(c => c.Name.Equals(k, StringComparison.OrdinalIgnoreCase))));
        var active = new HashSet<string>(ctx.GetActiveTools().Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        return new
        {
            type     = "tools",
            safeMode = ctx.SafeMode,
            active   = active.Count,
            matchesDefaults = MatchesDefaults(ctx, savedDefaults),
            categories = ordered.Select(name => new
            {
                name,
                description = ReplToolCatalog.Describe(name),
                optional    = ReplToolCatalog.OptionalPlugins.Contains(name),
                enabled     = !userDisabled.Contains(name),
                blocked     = ctx.SafeMode && IsSafeModeCategory(name) ? "safe mode" : null,
                tools = ctx.ToolsByCategory[name].Select(f => new
                {
                    name        = f.Name,
                    description = ReplToolCatalog.FirstSentence(f.Description),
                    enabled     = !ctx.DisabledTools.Contains(f.Name),
                    blocked     = !ctx.PassesSafeMode(f.Name) || !ctx.PassesSafeMode(name, f) ? "safe mode"
                                : !ctx.PassesCapabilityRestriction(f.Name) ? "restricted" : null,
                }).ToList(),
            }).ToList(),
        };
    }

    /// <summary>Sends the panel the chat's tools, when there's a panel to send them to.</summary>
    internal static void EmitIfBridged(ReplSessionContext ctx)
    {
        if (!ctx.JsonMode) return;
        ReplJsonBridge.Emit(Event(ctx, UserConfigStore.Load().Config?.Repl));
    }
}
