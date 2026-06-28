using System.ComponentModel;
using System.Text.Json;
using YamlDotNet.Serialization;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>Syntax family a given <see cref="ArtifactPlugin"/> instance's content must satisfy.</summary>
public enum ArtifactFormat
{
    Md,
    Json,
    Yaml,
}

/// <summary>
/// Generic, fixed-target-path artifact writer shared by every recon/triage-style agent across
/// the init templates (brownfield's Archaeologist, greenfield/swe's Preflight, audit's
/// Auditor, and future planning-stage agents). Each instance is bound at construction to
/// exactly one path, one required <see cref="ArtifactFormat"/>, and one tool name — there is
/// no path parameter, so a call can never be redirected at the project's own source files the
/// way <c>write_file</c>/<c>patch_file</c> can. Pair with
/// <c>Capabilities: { FileSystem: [read] }</c> so the agent can examine the sandbox but can
/// only persist its findings through this one call.
///
/// <para>
/// Replaces the former ReconPlugin/PreflightPlugin/AuditPlugin, which were separate classes
/// purely so that each agent's tool list only ever contained its own write function. That
/// guarantee is preserved here too — <see cref="PluginRegistry.GetFunctionsFromObject"/>
/// builds this plugin's single <see cref="WriteFileAsync"/> method under <see cref="ToolName"/>
/// (not the class-name-derived prefix every other plugin uses), so registering this same
/// class many times under different names (see <c>PluginRegistry.Configure</c> and
/// <c>OrchestratorBuilder</c>) still gives each agent exactly one, uniquely-named write tool.
/// </para>
///
/// <para>
/// Trades the typed per-field parameters the old plugins had (e.g.
/// <c>WriteFileConventionsAsync(string? language, ...)</c>) for a single free-text
/// <paramref name="content"/> the agent composes itself, validated only for being
/// syntactically well-formed in its required format — not for matching any particular field
/// shape. Agent instructions carry the expected shape in prose, the same way they already do
/// for artifacts with no typed consumer (e.g. <c>preflight.json</c>).
/// </para>
/// </summary>
public sealed class ArtifactPlugin
{
    private readonly string _path;
    private readonly ArtifactFormat _format;

    public ArtifactPlugin(string path, ArtifactFormat format, string toolName, string description)
    {
        _path = path;
        _format = format;
        ToolName = toolName;
        Description = description;
    }

    /// <summary>The exact tool name this instance's <see cref="WriteFileAsync"/> is exposed as.</summary>
    internal string ToolName { get; }

    /// <summary>The tool description shown to the model — bespoke per artifact.</summary>
    internal string Description { get; }

    [Description("placeholder — overridden per instance via ArtifactPlugin.Description")]
    public async Task<string> WriteFileAsync(
        [Description("Full file content.")] string content,
        [Description("Must be exactly: md, json, or yaml.")] string format)
    {
        if (!Enum.TryParse<ArtifactFormat>(format, ignoreCase: true, out var parsed))
            return PluginResult.Error($"format must be one of: md, json, yaml (got '{format}').");

        if (parsed != _format)
            return PluginResult.Error(
                $"This artifact must be written as '{FormatName(_format)}', not '{FormatName(parsed)}'.");

        var error = Validate(content, parsed);
        if (error is not null)
            return PluginResult.Error(error);

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(_path, content);
        return PluginResult.Ok($"Wrote {content.Length} chars → {_path}");
    }

    private static string? Validate(string content, ArtifactFormat format)
    {
        switch (format)
        {
            case ArtifactFormat.Json:
                try { JsonDocument.Parse(content); }
                catch (JsonException ex) { return $"content is not valid JSON: {ex.Message}"; }
                break;

            case ArtifactFormat.Yaml:
                try { new DeserializerBuilder().Build().Deserialize<object>(content); }
                catch (YamlDotNet.Core.YamlException ex) { return $"content is not valid YAML: {ex.Message}"; }
                break;

            case ArtifactFormat.Md:
                break; // no required structure
        }
        return null;
    }

    private static string FormatName(ArtifactFormat format) => format.ToString().ToLowerInvariant();
}

/// <summary>
/// Tool descriptions for every <see cref="ArtifactPlugin"/> instance registered across the init
/// templates (brownfield's two recon artifacts, greenfield/swe's preflight report, audit's
/// findings and remediation plan, devops's ops plan, research's findings and review, and swe's
/// brief/brief-review pair) — shared between the stub registrations in
/// <see cref="PluginRegistry.RegisterDefaults"/> and the real session/sandbox-scoped
/// registrations in <c>OrchestratorBuilder</c> and <see cref="PluginRegistry.Configure"/> so the
/// description text lives in exactly one place.
/// </summary>
internal static class ReconDescriptions
{
    public const string Conventions =
        "Write the detected project convention profile. Use this instead of write_file — your role here is read-only with respect to the project's own source files.";

    public const string DiscoveryBrief =
        "Write the discovery brief describing the codebase shape and the files in scope for the task. Use this instead of write_file.";

    public const string Preflight =
        "Write the preflight environment report. Use this instead of write_file — your role here is read-only with respect to the project's own source files.";

    public const string AuditFindings =
        "Write the audit findings report. Use this instead of write_file — your role here is read-only with respect to the project's own source files.";

    public const string Brief =
        "Write the task brief for the Developer. Use this instead of write_file — your role here is to plan, not to implement.";

    public const string BriefReview =
        "Write your review of the brief. Use this instead of write_file — your role here is to critique the brief, not to rewrite or implement it.";

    public const string RemediationPlan =
        "Write the remediation plan. Use this instead of write_file — your role here is to triage and order findings, not to fix them yourself.";

    public const string OpsPlan =
        "Write the operations plan. Use this instead of write_file — your role here is to plan the operation, not to execute it.";

    public const string ResearchFindings =
        "Write your research findings. Use this instead of write_file.";

    public const string ResearchReview =
        "Write your review of the research findings. Use this instead of write_file — your role here is to critique the findings, not to rewrite them yourself.";
}
