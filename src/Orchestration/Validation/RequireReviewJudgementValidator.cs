using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Validation;

/// <summary>
/// Blocks the <c>APPROVED</c> termination keyword unless the Reviewer's current turn
/// contains a structured review judgement block.
///
/// <para>
/// Expected format — a JSON object containing a <c>"review"</c> array, either inside a
/// <c>```json</c> code fence or as a bare object anywhere in the message:
/// </para>
/// <code>
/// {
///   "review": [
///     { "criterion": "...", "verdict": "PASS", "evidence": "..." },
///     ...
///   ]
/// }
/// </code>
///
/// <para>
/// The validator requires at least one entry with non-empty <c>criterion</c>,
/// <c>verdict</c>, and <c>evidence</c> fields. It does not require full criterion
/// coverage here — that is enforced upstream by <see cref="HandoffToReviewerValidator"/>.
/// </para>
/// </summary>
public sealed class RequireReviewJudgementValidator : IRoutingValidator
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    // Matches content inside ```json ... ``` or ``` ... ``` code fences.
    private static readonly Regex CodeFenceRegex =
        new(@"```(?:json)?\s*(\{[\s\S]*?\})\s*```", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task<RoutingValidationResult> ValidateAsync(
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        // Walk backward through the current turn to find the Reviewer's last text message.
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i];
            if (msg.Role == ChatRole.User) break;
            if (msg.Role == ChatRole.Tool)  continue;

            var content = msg.Text;
            if (string.IsNullOrWhiteSpace(content)) continue;

            // Found the Reviewer's last substantive message. Check for a review block.
            if (TryParseReviewBlock(content, out var judgement) && judgement!.Review is { Count: > 0 })
            {
                // Require at least one fully-populated entry.
                bool hasValidEntry = judgement.Review.Any(e =>
                    !string.IsNullOrWhiteSpace(e.Criterion) &&
                    !string.IsNullOrWhiteSpace(e.Verdict)   &&
                    !string.IsNullOrWhiteSpace(e.Evidence));

                if (hasValidEntry)
                    return Task.FromResult(RoutingValidationResult.Pass());
            }

            // The Reviewer's message is present but lacks a valid judgement block — fail.
            return Task.FromResult(RoutingValidationResult.Fail(
                "APPROVED blocked: response has no structured review block.\n\n" +
                "Add a ```json block before APPROVED:\n\n" +
                "```json\n{ \"review\": [{ \"criterion\": \"...\"," +
                " \"verdict\": \"PASS\", \"evidence\": \"...\" }] }\n```\n\n" +
                "Cover every acceptance criterion. Use PASS or FAIL. Then write APPROVED on its own line."));
        }

        // No Reviewer message found in history at all.
        return Task.FromResult(RoutingValidationResult.Fail(
            "APPROVED blocked: no Reviewer message found. " +
            "Complete your review with a structured judgement block before writing APPROVED."));
    }

    /// <summary>
    /// Tries to extract and parse a <c>{ "review": [...] }</c> JSON object from
    /// <paramref name="content"/>. Prefers a code-fenced block; falls back to the first
    /// raw JSON object that contains a <c>"review"</c> key.
    /// </summary>
    private static bool TryParseReviewBlock(string content, out ReviewJudgement? judgement)
    {
        judgement = null;

        // Prefer content inside a ```json ... ``` code fence.
        var fenceMatch = CodeFenceRegex.Match(content);
        if (fenceMatch.Success && TryDeserialize(fenceMatch.Groups[1].Value, out judgement))
            return true;

        // Fall back: find the outermost {...} block that contains "review".
        if (!content.Contains("\"review\"", StringComparison.OrdinalIgnoreCase))
            return false;

        int start = content.IndexOf('{');
        int end   = content.LastIndexOf('}');
        if (start >= 0 && end > start)
            return TryDeserialize(content[start..(end + 1)], out judgement);

        return false;
    }

    private static bool TryDeserialize(string json, out ReviewJudgement? judgement)
    {
        judgement = null;
        try
        {
            judgement = JsonSerializer.Deserialize<ReviewJudgement>(json, JsonOpts);
            return judgement?.Review is not null;
        }
        catch { return false; }
    }
}

// Internal DTOs

internal sealed record ReviewJudgement
{
    [JsonPropertyName("review")]
    public List<ReviewCriterion>? Review { get; init; }
}

internal sealed record ReviewCriterion
{
    [JsonPropertyName("criterion")]
    public string? Criterion { get; init; }

    [JsonPropertyName("verdict")]
    public string? Verdict { get; init; }

    [JsonPropertyName("evidence")]
    public string? Evidence { get; init; }
}
