using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using fuseraft.Core;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration;

/// <summary>
/// Resolves a <see cref="TransitionConfig.HandoffContext"/> source list into a formatted
/// context block that is injected into history when a state machine transition fires.
///
/// <para>
/// Each source reads from a durable disk artifact (session context summary, change log,
/// brief fields, or arbitrary files) rather than from the conversation transcript. This
/// keeps the injected context proportional to what the receiving agent actually needs
/// rather than proportional to total session length.
/// </para>
/// </summary>
public sealed class HandoffContextResolver
{
    private readonly string? _sandboxRoot;
    private readonly string? _changeLogPath;
    private readonly string? _briefPath;

    private string _sessionId = string.Empty;

    private const int DefaultMaxCharsPerSource = 4_000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive    = true,
        DefaultIgnoreCondition         = JsonIgnoreCondition.WhenWritingNull,
    };

    public HandoffContextResolver(
        string? sandboxRoot    = null,
        string? changeLogPath  = null,
        string? briefPath      = null)
    {
        _sandboxRoot   = sandboxRoot;
        _changeLogPath = changeLogPath;
        _briefPath     = briefPath;
    }

    public void SetSessionId(string sessionId) => _sessionId = sessionId;

    /// <summary>
    /// Resolves all sources in <paramref name="sources"/> and returns a formatted context
    /// block labelled for <paramref name="toAgent"/>, or <c>null</c> when no source yields
    /// content (missing files, empty summaries).
    /// </summary>
    public async Task<string?> ResolveAsync(
        string toAgent,
        IReadOnlyList<HandoffContextSource> sources,
        CancellationToken ct = default)
    {
        if (sources.Count == 0) return null;

        var sections = new List<(string Label, string Content)>(sources.Count);
        foreach (var src in sources)
        {
            var content = await ResolveOneAsync(src, ct);
            if (!string.IsNullOrWhiteSpace(content))
                sections.Add((src.Label ?? DefaultLabel(src.Source), content.Trim()));
        }

        if (sections.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine($"[HANDOFF CONTEXT — assembled for {toAgent}]");
        foreach (var (label, content) in sections)
        {
            sb.AppendLine();
            sb.AppendLine($"## {label}");
            sb.AppendLine(content);
        }
        return sb.ToString().TrimEnd();
    }

    // Source resolution

    private async Task<string?> ResolveOneAsync(HandoffContextSource src, CancellationToken ct)
    {
        var maxChars = src.MaxChars > 0 ? src.MaxChars : DefaultMaxCharsPerSource;
        var (type, param) = ParseSource(src.Source);
        return type switch
        {
            "session_context" => await ResolveSessionContextAsync(ct),
            "changes_recent"  => await ResolveChangesRecentAsync(
                                     int.TryParse(param, out var n) ? Math.Max(1, n) : 3,
                                     maxChars, ct),
            "brief_field"     => await ResolveBriefFieldAsync(param ?? string.Empty, maxChars, ct),
            "file"            => await ResolveFileAsync(param ?? string.Empty, maxChars, ct),
            _                 => null,
        };
    }

    private async Task<string?> ResolveSessionContextAsync(CancellationToken ct)
    {
        var path = FuseraftPaths.ExpandSessionId(FuseraftPaths.LocalSessionContext, _sessionId);
        if (!File.Exists(path)) return null;
        try
        {
            var text = await File.ReadAllTextAsync(path, ct);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch { return null; }
    }

    private async Task<string?> ResolveChangesRecentAsync(int count, int maxChars, CancellationToken ct)
    {
        var logPath = _changeLogPath ?? FuseraftPaths.LocalChanges;
        if (!File.Exists(logPath)) return null;
        try
        {
            var json    = await File.ReadAllTextAsync(logPath, ct);
            var log     = JsonSerializer.Deserialize<ChangeLog>(json, JsonOpts);
            if (log is null || log.Entries.Count == 0) return null;

            var entries = log.Entries
                .Where(e => string.IsNullOrEmpty(_sessionId) || e.SessionId == _sessionId || e.SessionId is null)
                .TakeLast(count)
                .ToList();
            if (entries.Count == 0) entries = log.Entries.TakeLast(count).ToList();

            return Truncate(FormatChangeEntries(entries), maxChars);
        }
        catch { return null; }
    }

    private async Task<string?> ResolveBriefFieldAsync(string field, int maxChars, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(field)) return null;
        var briefPath = _briefPath ?? FuseraftPaths.LocalBrief;
        var expanded  = FuseraftPaths.ExpandSessionId(briefPath, _sessionId);
        if (!File.Exists(expanded)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(expanded, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Try exact name then lowercase
            if (!root.TryGetProperty(field, out var prop))
                root.TryGetProperty(field.ToLowerInvariant(), out prop);

            if (prop.ValueKind == JsonValueKind.Undefined) return null;

            var text = prop.ValueKind == JsonValueKind.String
                ? prop.GetString()
                : prop.GetRawText();

            return text is null ? null : Truncate(text, maxChars);
        }
        catch { return null; }
    }

    private async Task<string?> ResolveFileAsync(string relativePath, int maxChars, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var expanded = FuseraftPaths.ExpandSessionId(relativePath, _sessionId);
        var resolved = _sandboxRoot is not null
            ? Path.Combine(_sandboxRoot, expanded)
            : expanded;
        if (!File.Exists(resolved)) return null;
        try
        {
            var text = await File.ReadAllTextAsync(resolved, ct);
            return Truncate(text, maxChars);
        }
        catch { return null; }
    }

    // Helpers

    private static (string Type, string? Param) ParseSource(string source)
    {
        var idx = source.IndexOf(':');
        if (idx < 0) return (source.Trim().ToLowerInvariant(), null);
        return (source[..idx].Trim().ToLowerInvariant(), source[(idx + 1)..].Trim());
    }

    private static string DefaultLabel(string source)
    {
        var (type, param) = ParseSource(source);
        return type switch
        {
            "session_context" => "Session Context",
            "changes_recent"  => "Recent Changes",
            "brief_field"     => $"Task: {param}",
            "file"            => param is not null ? Path.GetFileName(param) : "File",
            _                 => source,
        };
    }

    private static string FormatChangeEntries(IReadOnlyList<ChangeEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.AppendLine($"[Turn {e.TurnIndex}] {e.Agent} ({e.Timestamp:yyyy-MM-dd HH:mm} UTC)");

            if (e.FilesWritten.Count > 0)
            {
                sb.AppendLine("  Files written:");
                foreach (var f in e.FilesWritten) sb.AppendLine($"    - {f}");
            }
            if (e.FilesDeleted.Count > 0)
            {
                sb.AppendLine("  Files deleted:");
                foreach (var f in e.FilesDeleted) sb.AppendLine($"    - {f}");
            }
            if (e.CommandsRun.Count > 0)
            {
                sb.AppendLine("  Commands run:");
                foreach (var c in e.CommandsRun)
                    sb.AppendLine($"    - {c.Command} [{(c.Succeeded ? "OK" : "FAILED")}]");
            }
            if (e.GitCommits.Count > 0)
            {
                sb.AppendLine("  Git commits:");
                foreach (var g in e.GitCommits) sb.AppendLine($"    - {g}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string text, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars) return text;
        return text[..maxChars] +
               $"\n[...{text.Length - maxChars:N0} chars truncated — use file tool to read in full]";
    }
}
