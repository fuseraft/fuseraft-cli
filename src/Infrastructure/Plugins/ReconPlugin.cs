using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using fuseraft.Core.Models.Config;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Narrow, fixed-target-path artifact writer for the brownfield template's Archaeologist agent.
/// Each function writes exactly one well-known JSON artifact and takes no path parameter —
/// unlike <c>write_file</c>/<c>patch_file</c>, there is no way to direct these calls at the
/// project's own source files. Pair with <c>Capabilities: { FileSystem: [read] }</c> on the
/// agent so it can examine the codebase but cannot write or patch it, while still being able to
/// persist its own findings.
///
/// <para>
/// Function names are prefixed <c>write_file_</c> deliberately: <see cref="fuseraft.Orchestration.Validation.HandoffToTesterValidator"/>
/// (the <c>RequireWriteFile</c> validator) detects evidence of work via a substring match on
/// the tool name, so a node gated by that validator (brownfield's <c>RECON COMPLETE</c> edge)
/// still unblocks correctly with no validator changes.
/// </para>
///
/// <para>
/// See <see cref="PreflightPlugin"/> for the equivalent, narrower plugin used by the greenfield
/// template's Preflight agent — kept as a separate class (rather than folded into this one) so
/// each agent only ever sees the function it actually needs, not an unused sibling.
/// </para>
/// </summary>
public sealed class ReconPlugin(string conventionsPath, string discoveryBriefPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented           = true,
        DefaultIgnoreCondition  = JsonIgnoreCondition.WhenWritingNull,
    };

    [Description("Write the detected project convention profile. Use this instead of write_file — your role here is read-only with respect to the project's own source files.")]
    public async Task<string> WriteFileConventionsAsync(
        [Description("Primary language/ecosystem, e.g. \"go\", \"typescript\", \"python\".")] string? language = null,
        [Description("Naming conventions observed, e.g. \"test files match *_test.go\".")] List<string>? namingPatterns = null,
        [Description("Error-handling idioms to follow, e.g. \"wrap errors with fmt.Errorf(\\\"%w\\\", err)\".")] List<string>? errorHandling = null,
        [Description("Patterns that must not appear in written code, e.g. \"no panic() outside main\".")] List<string>? forbiddenPatterns = null,
        [Description("Testing conventions, e.g. \"table-driven tests use testify/require\".")] List<string>? testPatterns = null,
        [Description("Structural observations about the codebase layout.")] List<string>? structuralNotes = null,
        [Description("Command that builds the project, e.g. \"go build ./...\".")] string? buildCommand = null,
        [Description("Command that runs the full test suite, e.g. \"go test ./...\".")] string? testCommand = null)
    {
        var profile = new ConventionProfile
        {
            Language          = language,
            NamingPatterns    = namingPatterns    ?? [],
            ErrorHandling     = errorHandling     ?? [],
            ForbiddenPatterns = forbiddenPatterns ?? [],
            TestPatterns      = testPatterns      ?? [],
            StructuralNotes   = structuralNotes   ?? [],
            BuildCommand      = buildCommand,
            TestCommand       = testCommand,
        };

        return await PluginIo.WriteJsonAsync(conventionsPath, profile, JsonOptions);
    }

    [Description("Write the discovery brief describing the codebase shape and the files in scope for the task. Use this instead of write_file.")]
    public async Task<string> WriteFileDiscoveryBriefAsync(
        [Description("One-paragraph summary of the codebase structure.")] string? summary = null,
        [Description("File paths likely relevant to the task.")] List<string>? inScopeFiles = null,
        [Description("Fragility observations, one per entry, formatted as \"path — reason\" (e.g. \"src/legacy.go — no tests, high churn\"). Entries without \" — \" are kept as the reason with an empty path.")] List<string>? fragilitySignals = null,
        [Description("Files that lack a corresponding test file.")] List<string>? testCoverageGaps = null)
    {
        var brief = new BrownfieldDiscoveryBrief
        {
            Summary          = summary,
            InScopeFiles     = inScopeFiles     ?? [],
            TestCoverageGaps = testCoverageGaps ?? [],
            FragilitySignals = (fragilitySignals ?? [])
                .Select(ParseFragilitySignal)
                .ToList(),
        };

        return await PluginIo.WriteJsonAsync(discoveryBriefPath, brief, JsonOptions);
    }

    // "path — reason" → FragilitySignal { File = "path", Reason = "reason" }. No separator:
    // the whole entry is kept as the reason with an empty path, rather than throwing — a
    // malformed entry should degrade gracefully, not fail the tool call.
    private static FragilitySignal ParseFragilitySignal(string entry)
    {
        var idx = entry.IndexOf(" — ", StringComparison.Ordinal);
        return idx < 0
            ? new FragilitySignal { File = string.Empty, Reason = entry.Trim() }
            : new FragilitySignal { File = entry[..idx].Trim(), Reason = entry[(idx + 3)..].Trim() };
    }
}
