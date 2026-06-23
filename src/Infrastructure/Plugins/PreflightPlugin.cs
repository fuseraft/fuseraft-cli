using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Narrow, fixed-target-path artifact writer for the greenfield template's Preflight agent.
/// Writes exactly the environment report and takes no path parameter — unlike
/// <c>write_file</c>/<c>patch_file</c>, there is no way to direct this call at the project's
/// own source files. Pair with <c>Capabilities: { FileSystem: [read] }</c> on the agent so it
/// can examine the sandbox but cannot write or patch it, while still being able to persist its
/// findings. See <see cref="ReconPlugin"/> for the brownfield equivalent — kept as a separate
/// class so each agent only ever sees the function it actually needs.
/// </summary>
public sealed class PreflightPlugin(string preflightPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Description("Write the preflight environment report. Use this instead of write_file — your role here is read-only with respect to the project's own source files.")]
    public async Task<string> WriteFilePreflightAsync(
        [Description("Detected project types, e.g. [\"python\"].")] List<string>? projectTypes = null,
        [Description("Detected runtime versions, one per entry, formatted as \"runtime: version\" (e.g. \"python3: 3.12.1\").")] List<string>? runtimeVersions = null,
        [Description("Runtimes that were checked but not found.")] List<string>? missingRuntimes = null,
        [Description("True if the sandbox is inside a git working tree.")] bool gitRepo = false,
        [Description("True if `git status --short` produced no output; null if not checked.")] bool? gitClean = null,
        [Description("Non-fatal observations worth surfacing to later agents.")] List<string>? warnings = null)
    {
        var report = new PreflightReport
        {
            ProjectTypes    = projectTypes    ?? [],
            MissingRuntimes = missingRuntimes ?? [],
            Warnings        = warnings        ?? [],
            GitRepo         = gitRepo,
            GitClean        = gitClean,
            RuntimeVersions = (runtimeVersions ?? [])
                .Select(ParseRuntimeVersion)
                .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase),
        };

        return await PluginIo.WriteJsonAsync(preflightPath, report, JsonOptions);
    }

    // "runtime: version" → ("runtime", "version"). No separator: the whole entry becomes the
    // key with an empty version, rather than throwing — a malformed entry should degrade
    // gracefully, not fail the tool call.
    private static KeyValuePair<string, string> ParseRuntimeVersion(string entry)
    {
        var idx = entry.IndexOf(": ", StringComparison.Ordinal);
        return idx < 0
            ? new(entry.Trim(), string.Empty)
            : new(entry[..idx].Trim(), entry[(idx + 2)..].Trim());
    }
}

/// <summary>
/// Environment preflight report written by the greenfield template's Preflight agent to
/// <see cref="fuseraft.Core.FuseraftPaths.LocalPreflight"/>. Read back only via <c>read_file</c>
/// by later agents — no other C# code deserializes this, so its shape is free to match whatever
/// the Preflight agent's instructions ask for.
/// </summary>
internal sealed record PreflightReport
{
    [JsonPropertyName("project_types")]
    public List<string> ProjectTypes { get; init; } = [];

    [JsonPropertyName("runtime_versions")]
    public Dictionary<string, string> RuntimeVersions { get; init; } = [];

    [JsonPropertyName("missing_runtimes")]
    public List<string> MissingRuntimes { get; init; } = [];

    [JsonPropertyName("git_repo")]
    public bool GitRepo { get; init; }

    [JsonPropertyName("git_clean")]
    public bool? GitClean { get; init; }

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; init; } = [];
}
