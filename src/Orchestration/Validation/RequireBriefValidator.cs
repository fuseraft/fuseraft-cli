using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Validation;

/// <summary>
/// Blocks <c>HANDOFF TO DEVELOPER</c> (or any configured route) unless
/// <c>brief.json</c> exists on disk and contains a structurally complete brief.
///
/// <para>
/// Rationale: the Planner's sole job is to write a focused brief for the Developer.
/// An empty or missing brief means the Developer has no authoritative spec to follow —
/// every downstream failure can be traced back to this gap.  This validator makes the
/// Planner's contract mechanical: no valid brief = no handoff.
/// </para>
///
/// <para>
/// Checks (in order):
/// <list type="number">
///   <item><c>brief.json</c> exists at the configured path.</item>
///   <item><c>brief.json</c> is valid JSON.</item>
///   <item><c>goal</c> field is non-empty.</item>
///   <item><c>files_to_change</c> array is non-empty.</item>
///   <item><c>acceptance_criteria</c> array is non-empty.</item>
///   <item><c>implementation</c> array is non-empty.</item>
/// </list>
/// </para>
///
/// <para>
/// On retry sessions where <c>brief.json</c> already exists from a prior turn, the
/// validator passes immediately (the Planner is expected to have updated it or the
/// existing brief is still valid).
/// </para>
/// </summary>
public sealed class RequireBriefValidator(string briefPath) : IRoutingValidator
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    public async Task<RoutingValidationResult> ValidateAsync(
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        // 1. File existence
        if (!File.Exists(briefPath))
            return RoutingValidationResult.Fail(
                $"HANDOFF TO DEVELOPER blocked: '{briefPath}' does not exist.\n\n" +
                $"  1. Identify goal, constraints, acceptance criteria.\n" +
                $"  2. Explore codebase with list_files/read_file.\n" +
                $"  3. write_file '{briefPath}':\n" +
                $"     {{ \"goal\": \"...\", \"files_to_change\": [{{\"path\": \"...\", \"reason\": \"...\"}}], \"acceptance_criteria\": [\"...\"], \"constraints\": [\"...\"], \"implementation\": [{{\"action\": \"write\", \"path\": \"...\", \"content\": \"...\"}}] }}\n" +
                $"  4. Call handoff(route_keyword: \"HANDOFF TO DEVELOPER\").");

        // 2. JSON validity
        string rawJson;
        BriefContent? brief;
        try
        {
            rawJson = await File.ReadAllTextAsync(briefPath, cancellationToken);
            brief   = JsonSerializer.Deserialize<BriefContent>(rawJson, JsonOptions);
        }
        catch (Exception ex)
        {
            return RoutingValidationResult.Fail(
                $"HANDOFF TO DEVELOPER blocked: '{briefPath}' is invalid JSON: {ex.Message}. Fix and retry.");
        }

        if (brief is null)
            return RoutingValidationResult.Fail(
                $"HANDOFF TO DEVELOPER blocked: '{briefPath}' parsed to null. Rewrite as a valid JSON object.");

        // For field-level failures, include the current brief content so the Planner can
        // patch only the missing field without re-reading the file (TextOnly mode strips
        // ChatRole.Tool results between turns, so without this the Planner loses the brief).
        const int MaxBriefChars = 2000;
        var briefPreview = rawJson.Length > MaxBriefChars
            ? rawJson[..MaxBriefChars] + "\n...(truncated)"
            : rawJson;
        var currentBriefNote =
            $"\n\nCurrent '{briefPath}':\n{briefPreview}\n\n" +
            $"Update only the missing field(s) with write_file, then retry the handoff.";

        // 3. goal field
        if (string.IsNullOrWhiteSpace(brief.Goal))
            return RoutingValidationResult.Fail(
                $"HANDOFF TO DEVELOPER blocked: '{briefPath}' missing 'goal'. Add one sentence describing what to build." +
                currentBriefNote);

        // 4. files_to_change
        if (brief.FilesToChange is null or { Count: 0 })
            return RoutingValidationResult.Fail(
                $"HANDOFF TO DEVELOPER blocked: '{briefPath}' has empty 'files_to_change'. List every file to create or modify." +
                currentBriefNote);

        // 5. acceptance_criteria
        if (brief.AcceptanceCriteria is null or { Count: 0 })
            return RoutingValidationResult.Fail(
                $"HANDOFF TO DEVELOPER blocked: '{briefPath}' has empty 'acceptance_criteria'. Add at least one testable criterion." +
                currentBriefNote);

        // 6. implementation — must be present and non-empty so the Developer has
        //    an executable spec rather than prose guidance to interpret.
        if (brief.Implementation is null or { Count: 0 })
            return RoutingValidationResult.Fail(
                $"HANDOFF TO DEVELOPER blocked: '{briefPath}' has empty 'implementation'. Provide ordered changes:\n" +
                $"  - action=write: full content for new/small files.\n" +
                $"  - action=patch: exact old→new for large files.\n" +
                $"Cover every file in files_to_change." +
                currentBriefNote);

        return RoutingValidationResult.Pass();
    }
}

// Minimal DTOs for brief.json deserialization

internal sealed record BriefContent
{
    [JsonPropertyName("goal")]
    public string? Goal { get; init; }

    [JsonPropertyName("files_to_change")]
    public List<BriefFileEntry>? FilesToChange { get; init; }

    [JsonPropertyName("acceptance_criteria")]
    public List<string>? AcceptanceCriteria { get; init; }

    [JsonPropertyName("implementation")]
    public List<object>? Implementation { get; init; }
}

internal sealed record BriefFileEntry
{
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
