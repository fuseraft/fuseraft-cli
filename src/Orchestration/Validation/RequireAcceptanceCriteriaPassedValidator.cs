using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Validation;

/// <summary>
/// Blocks a handoff route unless every machine-testable acceptance criterion in
/// <c>brief.json</c> has evidence of passing in the current session's shell history.
///
/// <para>
/// A criterion is machine-testable when it carries an <c>expected_output_contains</c> field.
/// Plain-string criteria and object criteria without <c>expected_output_contains</c> are
/// skipped — they remain subject to the Reviewer's judgement, not this validator.
/// </para>
///
/// <para>
/// <b>How to write testable criteria in brief.json:</b>
/// </para>
/// <code>
/// "acceptance_criteria": [
///   {
///     "criterion": "Runtime: x = include 'lib/math'; x.sqrt(16) prints 4.0",
///     "test_command": "printf 'x = include \"lib/math\"\\nprint(x.sqrt(16))' > /tmp/ac.kiwi && ./bin/kiwi /tmp/ac.kiwi && echo CRITERION_SQRT_PASS",
///     "expected_output_contains": "CRITERION_SQRT_PASS"
///   }
/// ]
/// </code>
///
/// <para>
/// The validator scans <c>changes.json</c> for all successful <c>CommandsRun</c> entries
/// across every turn of the current session. A criterion is satisfied when any such command
/// produced output containing its <c>expected_output_contains</c> value.
/// </para>
///
/// <para>
/// Register this validator on the <c>developer → reviewer</c> edge (so the Developer cannot
/// hand off without first verifying the criteria) and optionally on the
/// <c>reviewer → approved</c> edge for defence in depth.
/// </para>
/// </summary>
public sealed class RequireAcceptanceCriteriaPassedValidator(
    string briefPath,
    string? changeLogPath = null) : IRoutingValidator
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    public async Task<RoutingValidationResult> ValidateAsync(
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        // 1. Brief must exist.
        if (!File.Exists(briefPath))
            return RoutingValidationResult.Fail(
                $"Handoff blocked: '{briefPath}' does not exist. Write the brief first.");

        // 2. Parse brief.json.
        JsonNode? root;
        try
        {
            var json = await File.ReadAllTextAsync(briefPath, cancellationToken);
            root = JsonNode.Parse(json);
        }
        catch (Exception ex)
        {
            return RoutingValidationResult.Fail(
                $"Handoff blocked: could not parse '{briefPath}': {ex.Message}");
        }

        // 3. Extract testable criteria — those with expected_output_contains set.
        var testable = ParseTestableCriteria(root?["acceptance_criteria"]);

        // No machine-testable criteria → nothing to check here.
        if (testable.Count == 0)
            return RoutingValidationResult.Pass();

        // 4. Collect all successful command outputs for this session from changes.json.
        var sessionOutputs = changeLogPath is not null
            ? await CollectSessionOutputsAsync(changeLogPath, cancellationToken)
            : [];

        // 5. For each testable criterion, check whether any successful command produced
        //    output containing the expected sentinel/substring.
        var unsatisfied = new List<TestableCriterion>();
        foreach (var criterion in testable)
        {
            bool satisfied = sessionOutputs.Any(output =>
                output.Contains(criterion.ExpectedOutputContains,
                    StringComparison.OrdinalIgnoreCase));

            if (!satisfied)
                unsatisfied.Add(criterion);
        }

        if (unsatisfied.Count == 0)
            return RoutingValidationResult.Pass();

        // Build a helpful error message that shows what command to run for each failure.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Handoff blocked: the following acceptance criteria have not been verified by a passing shell command this session:");
        sb.AppendLine();

        foreach (var c in unsatisfied)
        {
            sb.AppendLine($"  ✗ {c.CriterionText}");
            if (!string.IsNullOrWhiteSpace(c.TestCommand))
            {
                sb.AppendLine($"    Run: {c.TestCommand}");
                sb.AppendLine($"    Expected in output: \"{c.ExpectedOutputContains}\"");
            }
            else
            {
                sb.AppendLine($"    Run a command whose output contains: \"{c.ExpectedOutputContains}\"");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Run the indicated command(s), confirm the expected output appears, then retry the handoff.");
        return RoutingValidationResult.Fail(sb.ToString().TrimEnd());
    }

    // Reads changes.json and returns a flat list of output strings from every
    // successful shell command in the current session (all turns, all agents).
    private static async Task<List<string>> CollectSessionOutputsAsync(
        string logPath,
        CancellationToken ct)
    {
        if (!File.Exists(logPath)) return [];

        ChangeLog log;
        try
        {
            var json = await File.ReadAllTextAsync(logPath, ct);
            log = JsonSerializer.Deserialize<ChangeLog>(json, JsonOpts) ?? new ChangeLog();
        }
        catch { return []; }

        var sessionId = log.ActiveSessionId;
        var entries = sessionId is not null
            ? log.Entries.Where(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal))
            : (IEnumerable<ChangeEntry>)log.Entries;

        var outputs = new List<string>();
        foreach (var entry in entries)
            foreach (var cmd in entry.CommandsRun)
                if (cmd.Succeeded && cmd.Output is not null)
                    outputs.Add(cmd.Output);

        return outputs;
    }

    // Parses the acceptance_criteria node — supports plain string arrays and object arrays.
    // Returns only the criteria that have expected_output_contains set.
    private static List<TestableCriterion> ParseTestableCriteria(JsonNode? node)
    {
        var result = new List<TestableCriterion>();
        if (node is not JsonArray arr) return result;

        foreach (var item in arr)
        {
            if (item is null) continue;

            if (item is JsonObject obj)
            {
                var expectedOutput = obj["expected_output_contains"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(expectedOutput)) continue;

                result.Add(new TestableCriterion(
                    CriterionText:        obj["criterion"]?.GetValue<string>()    ?? "(unnamed)",
                    TestCommand:          obj["test_command"]?.GetValue<string>(),
                    ExpectedOutputContains: expectedOutput));
            }
            // Plain strings have no expected_output_contains — not machine-testable.
        }

        return result;
    }

    private sealed record TestableCriterion(
        string  CriterionText,
        string? TestCommand,
        string  ExpectedOutputContains);
}
