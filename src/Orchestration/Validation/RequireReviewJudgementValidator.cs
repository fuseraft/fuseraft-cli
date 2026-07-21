using System.Text.Json;
using System.Text.Json.Nodes;
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
/// <c>verdict</c>, and <c>evidence</c> fields. When a <paramref name="briefPath"/> is
/// supplied it also enforces <b>criterion coverage</b>: the review block must contain at
/// least as many entries as the brief's <c>acceptance_criteria</c> array, so every
/// criterion is explicitly addressed rather than collapsed into a single generic verdict.
/// </para>
/// </summary>
public sealed class RequireReviewJudgementValidator(string? briefPath = null) : IRoutingValidator
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

                if (!hasValidEntry) break; // fall through to the structured-block error below

                // Require at least one successful shell_run in this Reviewer turn.
                // Code inspection alone cannot confirm behavioral or runtime criteria —
                // the Reviewer must execute something (build, tests, or the use-case itself)
                // and reference the output in their evidence.
                bool hasAnyPass = judgement.Review.Any(e =>
                    string.Equals(e.Verdict, "PASS", StringComparison.OrdinalIgnoreCase));

                // If any criterion is FAIL, the change is not complete — the agent must fix it
                // or explicitly route REVISION REQUIRED instead of writing APPROVED.
                var failingCriteria = judgement.Review
                    .Where(e => string.Equals(e.Verdict, "FAIL", StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Criterion)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .ToList();

                if (failingCriteria.Count > 0)
                {
                    return Task.FromResult(RoutingValidationResult.Fail(
                        "APPROVED blocked: review contains FAIL verdict(s).\n\n" +
                        "Fix the failing criteria before writing APPROVED, or route " +
                        "REVISION REQUIRED so the developer can address them.\n\n" +
                        "Failing:\n" +
                        string.Join("\n", failingCriteria.Select(c => $"  ✗ {c}"))));
                }

                // Coverage check: when a brief is configured, the review must have at least
                // one entry per acceptance criterion so every criterion is explicitly addressed.
                var coverageError = CheckCriterionCoverage(judgement.Review);
                if (coverageError is not null)
                    return Task.FromResult(RoutingValidationResult.Fail(coverageError));

                if (hasAnyPass && !CurrentTurnHasSuccessfulShellRun(history))
                {
                    return Task.FromResult(RoutingValidationResult.Fail(
                        "APPROVED blocked: review contains PASS verdicts but no shell command " +
                        "was successfully run this turn.\n\n" +
                        "Code inspection alone is not sufficient — the Reviewer must execute " +
                        "something to confirm behavioral correctness:\n\n" +
                        "  1. Run a verification command with shell_run:\n" +
                        "       • Build:  shell_run(\"dotnet build\") / shell_run(\"cargo build\") / etc.\n" +
                        "       • Tests:  shell_run(\"dotnet test\") / shell_run(\"cargo test\") / etc.\n" +
                        "       • Run-it: shell_run(\"kiwi test_include.ki\") or equivalent.\n" +
                        "  2. Update 'evidence' fields to describe what you ran and what you observed.\n" +
                        "  3. Re-emit the ```json review block followed by APPROVED on its own line."));
                }

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
            "APPROVED blocked: your last reply had no text content at all — you called the " +
            "routing tool without writing anything first. This validator reads your reply's " +
            "visible text; a tool call alone, with no accompanying text, has nothing for it to " +
            "check.\n\n" +
            "Write the ```json review block AND the routing keyword as text in this same reply " +
            "(before or alongside calling the handoff tool) — do not call handoff with an empty " +
            "or missing text response, even if you believe you already reviewed everything in an " +
            "earlier turn."));
    }

    // When briefPath is set, loads acceptance_criteria count from brief.json and returns
    // an error string if the review has fewer entries than the brief has criteria.
    // Returns null (no error) when no brief is configured, the brief cannot be loaded,
    // or the brief has no acceptance_criteria.
    private string? CheckCriterionCoverage(List<ReviewCriterion> reviewEntries)
    {
        if (briefPath is null) return null;
        if (!File.Exists(briefPath)) return null;

        int criteriaCount;
        try
        {
            var json = File.ReadAllText(briefPath);
            var root = JsonNode.Parse(json);
            var arr  = root?["acceptance_criteria"] as JsonArray;
            criteriaCount = arr?.Count ?? 0;
        }
        catch { return null; }

        if (criteriaCount == 0) return null;

        if (reviewEntries.Count >= criteriaCount) return null;

        return
            $"APPROVED blocked: the review covers {reviewEntries.Count} criterion/criteria " +
            $"but brief.json lists {criteriaCount}.\n\n" +
            $"Add one review entry per acceptance criterion — every criterion must be explicitly " +
            $"addressed with its own verdict and evidence:\n\n" +
            $"```json\n" +
            $"{{\"review\":[\n" +
            $"  {{\"criterion\":\"<criterion text>\",\"verdict\":\"PASS\",\"evidence\":\"<what you ran and observed>\"}},\n" +
            $"  ... ({criteriaCount} entries total)\n" +
            $"]}}\n```\n\n" +
            $"Then write APPROVED on its own line.";
    }

    // Scans the current turn (back to the last user boundary) for a shell_run result
    // that did not produce an error or non-zero exit code.
    private static bool CurrentTurnHasSuccessfulShellRun(IList<ChatMessage> history)
    {
        // Phase 1: build a callId → functionName map from assistant messages in this turn.
        var callIdToName = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.User) break;
            if (history[i].Role != ChatRole.Assistant) continue;
            foreach (var item in history[i].Contents)
            {
                if (item is FunctionCallContent fcc && fcc.CallId is not null && fcc.Name is not null)
                    callIdToName.TryAdd(fcc.CallId, fcc.Name);
            }
        }

        // Phase 2: find a shell_run result with no error/exit prefix.
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.User) break;
            if (history[i].Role != ChatRole.Tool) continue;

            foreach (var item in history[i].Contents)
            {
                if (item is not FunctionResultContent frc) continue;
                var name = (frc.CallId is not null && callIdToName.TryGetValue(frc.CallId, out var n))
                    ? n : string.Empty;
                if (!name.Contains("shell_run", StringComparison.OrdinalIgnoreCase)) continue;

                var result = frc.Result?.ToString() ?? string.Empty;
                if (!result.StartsWith("[EXIT",      StringComparison.Ordinal) &&
                    !result.StartsWith("[ERROR]",    StringComparison.Ordinal) &&
                    !result.StartsWith("[TIMEOUT]",  StringComparison.Ordinal) &&
                    !result.StartsWith("[DENIED]",   StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
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
