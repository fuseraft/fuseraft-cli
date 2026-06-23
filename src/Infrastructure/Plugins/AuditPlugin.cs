using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Narrow, fixed-target-path artifact writer for the audit template's Auditor agent.
/// Writes exactly the findings report and takes no path parameter — unlike
/// <c>write_file</c>/<c>patch_file</c>, there is no way to direct this call at the project's
/// own source files. Pair with <c>Capabilities: { FileSystem: [read] }</c> on the agent so a
/// security/quality auditor can examine the codebase but never modify it, while still being
/// able to persist its findings. See <see cref="ReconPlugin"/>/<see cref="PreflightPlugin"/>
/// for the brownfield/greenfield equivalents — kept as a separate class for the same reason
/// they are: each agent only ever sees the function it actually needs.
/// </summary>
public sealed class AuditPlugin(string findingsPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Description("Write the audit findings report. Use this instead of write_file — your role here is read-only with respect to the project's own source files. " +
                 "Pass one parallel array per field, all the same length — one entry per finding, in the same order.")]
    public async Task<string> WriteFileAuditFindingsAsync(
        [Description("Sequential IDs by type, e.g. \"SEC-001\", \"QUA-001\", \"CMP-001\", \"COR-001\".")] List<string>? ids = null,
        [Description("One of: critical, high, medium, low.")] List<string>? severities = null,
        [Description("One of: security, quality, compliance, correctness.")] List<string>? types = null,
        [Description("Relative file path of the finding.")] List<string>? files = null,
        [Description("Line number of the finding.")] List<int>? lines = null,
        [Description("What the issue is.")] List<string>? descriptions = null,
        [Description("What to do about it.")] List<string>? recommendations = null)
    {
        var count = new[] { ids?.Count, severities?.Count, types?.Count, files?.Count, lines?.Count, descriptions?.Count, recommendations?.Count }
            .Where(c => c is > 0)
            .Select(c => c!.Value)
            .DefaultIfEmpty(0)
            .Min();

        var findings = new List<AuditFinding>(count);
        for (var i = 0; i < count; i++)
        {
            findings.Add(new AuditFinding
            {
                Id             = At(ids, i),
                Severity       = At(severities, i),
                Type           = At(types, i),
                File           = At(files, i),
                Line           = lines is { } l && i < l.Count ? l[i] : 0,
                Description    = At(descriptions, i),
                Recommendation = At(recommendations, i),
            });
        }

        return await PluginIo.WriteJsonAsync(findingsPath, new AuditFindingsReport { Findings = findings }, JsonOptions);
    }

    private static string? At(List<string>? list, int i) => list is { } l && i < l.Count ? l[i] : null;
}

/// <summary>
/// Audit findings report written by the audit template's Auditor agent to
/// <see cref="fuseraft.Core.FuseraftPaths.LocalAuditFindings"/>. Read back only via
/// <c>read_file</c> by later agents — no other C# code deserializes this, so its shape is
/// free to match whatever the Auditor's instructions ask for.
/// </summary>
internal sealed record AuditFindingsReport
{
    [JsonPropertyName("findings")]
    public List<AuditFinding> Findings { get; init; } = [];
}

internal sealed record AuditFinding
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("severity")]
    public string? Severity { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("file")]
    public string? File { get; init; }

    [JsonPropertyName("line")]
    public int Line { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("recommendation")]
    public string? Recommendation { get; init; }
}
