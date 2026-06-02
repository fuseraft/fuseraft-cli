using System.ComponentModel;
using System.Text;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Agent-facing tools for the Architecture Decision Registry.
///
/// Tool names (via <c>decision_</c> prefix):
///   decision_search    — keyword + status/tag filter across all ADRs
///   decision_read      — fetch a single ADR by ID
///   decision_create    — record a new architecture decision
///   decision_supersede — mark an existing ADR as superseded
/// </summary>
public sealed class DecisionPlugin
{
    private readonly AdrRegistry _registry;
    private readonly RepositoryGraphBuilder? _graphBuilder;

    public DecisionPlugin(AdrRegistry registry, RepositoryGraphBuilder? graphBuilder = null)
    {
        _registry     = registry;
        _graphBuilder = graphBuilder;
    }

    [Description("Search architecture decision records by keyword, status, or tag.")]
    public async Task<string> SearchAsync(
        [Description("Keyword to match against title, context, decision text, and tags. Leave empty to list all.")]
        string query = "",
        [Description("Filter by status: Proposed, Accepted, Deprecated, or Superseded.")]
        string? status = null,
        [Description("Filter by tag.")]
        string? tag = null)
    {
        var results = await _registry.SearchAsync(query, status, tag);
        if (results.Count == 0) return PluginResult.NotFound("No matching decisions found.");

        var sb = new StringBuilder();
        sb.AppendLine($"=== Decisions ({results.Count} result(s)) ===");
        foreach (var e in results)
        {
            sb.AppendLine();
            sb.Append(FormatSummary(e));
        }
        return sb.ToString().TrimEnd();
    }

    [Description("Read an architecture decision record by ID.")]
    public async Task<string> ReadAsync(
        [Description("Decision ID, e.g. ADR-0042.")]
        string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return PluginResult.Error("id must not be empty.");

        var entry = await _registry.GetByIdAsync(id.Trim());
        return entry is null
            ? PluginResult.NotFound($"No decision with ID '{id}'.")
            : FormatFull(entry);
    }

    [Description("Record a new architecture decision.")]
    public async Task<string> CreateAsync(
        [Description("Short descriptive title.")]
        string title,
        [Description("Why this decision was needed — background and forces at play.")]
        string context,
        [Description("The decision that was made.")]
        string decision,
        [Description("Comma-separated alternatives that were considered and rejected.")]
        string? alternatives = null,
        [Description("Comma-separated consequences of this decision (positive and negative).")]
        string? consequences = null,
        [Description("Comma-separated tags for categorization (e.g. persistence,security).")]
        string? tags = null,
        [Description("Comma-separated IDs of earlier decisions this supersedes (e.g. ADR-0017,ADR-0021).")]
        string? supersedes = null,
        [Description("Comma-separated file paths or symbol IDs this decision governs (e.g. src/Auth.cs,type:fuseraft.Auth.TokenManager).")]
        string? governs = null)
    {
        if (string.IsNullOrWhiteSpace(title))    return PluginResult.Error("title must not be empty.");
        if (string.IsNullOrWhiteSpace(context))  return PluginResult.Error("context must not be empty.");
        if (string.IsNullOrWhiteSpace(decision)) return PluginResult.Error("decision must not be empty.");

        var id    = _registry.NextId();
        var entry = new AdrEntry
        {
            Id           = id,
            Title        = title.Trim(),
            Status       = "Accepted",
            Date         = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
            Context      = context.Trim(),
            Decision     = decision.Trim(),
            Alternatives = SplitCsv(alternatives),
            Consequences = SplitCsv(consequences),
            Tags         = SplitCsv(tags),
            Supersedes   = SplitCsv(supersedes),
            Governs      = SplitCsv(governs),
        };

        await _registry.SaveAsync(entry);

        foreach (var supersededId in entry.Supersedes)
        {
            var old = await _registry.GetByIdAsync(supersededId.Trim());
            if (old is not null && !old.Status.Equals("Superseded", StringComparison.OrdinalIgnoreCase))
                await _registry.SaveAsync(old with { Status = "Superseded" });
        }

        if (_graphBuilder is not null && entry.Governs.Count > 0)
            _ = _graphBuilder.UpsertAdrNodeAsync(entry); // fire-and-forget; graph is best-effort

        return PluginResult.Ok($"Created {id}: {entry.Title}");
    }

    [Description("Mark an architecture decision record as superseded.")]
    public async Task<string> SupersedeAsync(
        [Description("ID of the decision to supersede, e.g. ADR-0017.")]
        string id,
        [Description("ID of the newer decision that replaces it, e.g. ADR-0042.")]
        string newId)
    {
        if (string.IsNullOrWhiteSpace(id))    return PluginResult.Error("id must not be empty.");
        if (string.IsNullOrWhiteSpace(newId)) return PluginResult.Error("newId must not be empty.");

        var entry = await _registry.GetByIdAsync(id.Trim());
        if (entry is null) return PluginResult.NotFound($"No decision with ID '{id}'.");

        if (entry.Status.Equals("Superseded", StringComparison.OrdinalIgnoreCase))
            return PluginResult.Info($"{id} is already marked as Superseded.");

        await _registry.SaveAsync(entry with { Status = "Superseded" });
        return PluginResult.Ok($"{id} marked as Superseded (replaced by {newId.Trim()}).");
    }

    // Formatting

    private static string FormatSummary(AdrEntry e)
    {
        var sb = new StringBuilder();
        sb.Append($"[{e.Id}] {e.Title}");
        sb.Append($"  status: {e.Status}");
        sb.Append($"  date: {e.Date}");
        if (e.Tags.Count > 0) sb.Append($"  tags: {string.Join(", ", e.Tags)}");
        if (e.Supersedes.Count > 0) sb.Append($"  supersedes: {string.Join(", ", e.Supersedes)}");
        return sb.ToString();
    }

    private static string FormatFull(AdrEntry e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Id: {e.Id}");
        sb.AppendLine($"Title: {e.Title}");
        sb.AppendLine($"Status: {e.Status}");
        sb.AppendLine($"Date: {e.Date}");
        if (e.Tags.Count > 0)       sb.AppendLine($"Tags: {string.Join(", ", e.Tags)}");
        if (e.Supersedes.Count > 0) sb.AppendLine($"Supersedes: {string.Join(", ", e.Supersedes)}");
        sb.AppendLine();
        sb.AppendLine("Context:");
        sb.AppendLine(Indent(e.Context));
        sb.AppendLine();
        sb.AppendLine("Decision:");
        sb.AppendLine(Indent(e.Decision));
        if (e.Alternatives.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Alternatives:");
            foreach (var a in e.Alternatives) sb.AppendLine($"  - {a}");
        }
        if (e.Consequences.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Consequences:");
            foreach (var c in e.Consequences) sb.AppendLine($"  - {c}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Indent(string text) =>
        string.Join("\n", text.Split('\n').Select(l => $"  {l}"));

    private static List<string> SplitCsv(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0)];
}
