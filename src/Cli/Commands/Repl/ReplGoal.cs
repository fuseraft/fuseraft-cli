using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using fuseraft.Core;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>The judge's reading of whether a <c>/goal</c> objective has been provably met.</summary>
/// <param name="Score">Probability (0–1) the full objective is done and verified.</param>
/// <param name="Complete">The judge considers every requirement satisfied with evidence.</param>
/// <param name="Blocked">The agent is waiting on something only the user can supply (a decision, a credential, an approval).</param>
/// <param name="Missing">Concise description of what is still unverified; empty when complete.</param>
internal sealed record GoalVerdict(double Score, bool Complete, bool Blocked, string Missing);

/// <summary>What the <c>/goal</c> loop should do after a verdict.</summary>
internal enum GoalAction { Continue, Complete, Capped, Stalled, Blocked }

/// <summary>
/// One <c>/goal</c> run: the objective, its iteration budget, and the bookkeeping that decides when
/// to stop. Pure state — no I/O — so the decision rules can be tested without a model.
/// </summary>
internal sealed class GoalState(string objective, int maxIterations)
{
    /// <summary>Consecutive audits reporting the same missing work before the loop gives up.</summary>
    internal const int StallLimit = 3;

    public string Objective     { get; } = objective;
    public int    MaxIterations { get; } = maxIterations;

    /// <summary>Audits performed so far (each follows one full agent turn).</summary>
    public int Iteration { get; private set; }

    public GoalVerdict? LastVerdict { get; private set; }

    private string _lastMissingKey = string.Empty;
    private int    _sameMissingRun;

    /// <summary>
    /// Records an audit and returns what to do next. <b>Complete</b> wins over everything; then a
    /// block on the user; then a stall (the same missing work reported <see cref="StallLimit"/>
    /// times running — typically a denied tool call or an unwinnable check the agent keeps
    /// retrying); then the iteration cap.
    /// </summary>
    public GoalAction Record(GoalVerdict verdict)
    {
        Iteration++;
        LastVerdict = verdict;

        var key = ReplGoal.NormalizeMissing(verdict.Missing);
        _sameMissingRun = key.Length > 0 && key == _lastMissingKey ? _sameMissingRun + 1 : 1;
        _lastMissingKey = key;

        if (verdict.Complete)                    return GoalAction.Complete;
        if (verdict.Blocked)                     return GoalAction.Blocked;
        if (_sameMissingRun >= StallLimit)       return GoalAction.Stalled;
        if (Iteration >= MaxIterations)          return GoalAction.Capped;
        return GoalAction.Continue;
    }
}

/// <summary>
/// The pure parts of <c>/goal</c>: an independent LLM judge reads the transcript and decides
/// whether the objective is provably done; if not, the agent is re-prompted with what is missing.
/// The judge only ever sees the transcript (never the agent's system prompt), and anything the
/// agent merely claims without tool evidence counts as unverified. Ported in spirit from the
/// OpenHands SDK's <c>/goal</c>, with two additions: a <c>blocked</c> verdict (stop and hand
/// control back when the agent needs the user) and a stall guard.
/// </summary>
internal static class ReplGoal
{
    internal const int DefaultMaxIterations = 5;

    /// <summary>Leading text of the follow-up user message; recognised by <see cref="ReplTurn.IsInternalCorrectionMessage"/>.</summary>
    internal const string FollowUpPrefix = "The goal is not complete yet";

    /// <summary>Leading text of the message that restarts an interrupted goal.</summary>
    internal const string ResumePrefix = "Resuming a goal that was interrupted";

    private const int MaxToolResultChars = 1500;
    private const int MaxToolArgChars    = 300;
    internal const int DefaultTranscriptChars = 60_000;

    private const string JudgeInstructions = """
        You are auditing whether a long-running GOAL has been COMPLETED by an AI coding agent.

        <objective>
        {0}
        </objective>

        Derive the concrete requirements implied by the objective. For EACH requirement, look for
        authoritative evidence in the transcript below: file contents, command output, or test
        results the agent's tools actually returned. Treat missing, uncertain, or merely-claimed
        evidence as NOT satisfied — an agent saying "done" or "tests pass" without a tool result
        showing it does not count.

        <transcript>
        {1}
        </transcript>

        Respond with STRICT JSON and nothing else, in exactly this shape:
        {{"score": <float 0.0-1.0, probability the FULL objective is provably done>, "complete": <true|false>, "blocked": <true|false>, "missing": "<concise description of what remains unverified, or an empty string if complete>"}}

        Set "blocked" to true ONLY when the agent has stopped because it needs something the user
        must provide (a decision between options, a credential, an approval it was denied, a
        clarification) and cannot make further progress alone. That includes the agent asking
        again because the user's earlier answer did not settle the question. Otherwise false.
        """;

    internal static string BuildJudgePrompt(string objective, string transcript) =>
        string.Format(JudgeInstructions, objective, transcript);

    internal static string BuildFollowUp(int iteration, string missing)
    {
        var outstanding = string.IsNullOrWhiteSpace(missing) ? "Some requirements are not yet verified." : missing.Trim();
        return $"{FollowUpPrefix} (audit {iteration}). Outstanding: {outstanding}\n\n" +
               "Inspect the real current state of the workspace instead of relying on memory. For each " +
               "remaining requirement, make concrete progress and gather evidence by running the relevant " +
               "tests or commands. Keep the full objective intact and finish only once every requirement " +
               "is provably satisfied. If you are blocked on something only the user can decide or provide, " +
               "say so plainly and stop rather than guessing.";
    }

    internal static string BuildResumeMessage(string objective) =>
        $"{ResumePrefix}. The objective is: {objective}\n\n" +
        "Re-check the real current state of the workspace (do not rely on memory) and continue making " +
        "concrete, verified progress. Finish only once every requirement is provably satisfied.";

    /// <summary>Lower-cased, punctuation-free, whitespace-collapsed form used to spot a repeated "missing" report.</summary>
    internal static string NormalizeMissing(string? missing)
    {
        if (string.IsNullOrWhiteSpace(missing)) return string.Empty;
        var sb = new StringBuilder(missing.Length);
        foreach (var ch in missing.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    // -------------------------------------------------------------------------
    // Transcript
    // -------------------------------------------------------------------------

    /// <summary>
    /// Renders the conversation as plain text for the judge: user/assistant text, tool calls with
    /// bounded arguments, tool results with bounded bodies. The system prompt is left out (large,
    /// no goal evidence). When the whole thing exceeds <paramref name="maxChars"/> the oldest entries
    /// are dropped — the most recent evidence is what decides whether the goal is met.
    /// </summary>
    internal static string RenderTranscript(IReadOnlyList<ChatMessage> history, int maxChars = DefaultTranscriptChars)
    {
        var entries = new List<string>();
        foreach (var m in history)
        {
            if (m.Role == ChatRole.System) continue;

            var parts = new List<string>();
            foreach (var c in m.Contents)
            {
                switch (c)
                {
                    case TextContent t when !string.IsNullOrWhiteSpace(t.Text):
                        parts.Add(t.Text.Trim());
                        break;
                    case FunctionCallContent call:
                        parts.Add($"[tool call] {call.Name}({Truncate(FormatArgs(call.Arguments), MaxToolArgChars)})");
                        break;
                    case FunctionResultContent result:
                        parts.Add($"[tool result] {Truncate(ToolResultText.ToStringOrDefault(result.Result), MaxToolResultChars)}");
                        break;
                    case DataContent or UriContent:
                        parts.Add("[attachment]");
                        break;
                }
            }
            if (parts.Count == 0) continue;
            entries.Add($"{m.Role.Value}: {string.Join("\n", parts)}");
        }

        var total   = entries.Sum(e => e.Length + 2);
        var dropped = 0;
        while (total > maxChars && entries.Count > 1)
        {
            total -= entries[0].Length + 2;
            entries.RemoveAt(0);
            dropped++;
        }

        var body = string.Join("\n\n", entries);
        return dropped > 0 ? $"[{dropped} earlier message(s) omitted]\n\n{body}" : body;
    }

    private static string FormatArgs(IDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0) return string.Empty;
        try   { return JsonSerializer.Serialize(args); }
        catch { return string.Join(", ", args.Keys); }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…(truncated)";

    // -------------------------------------------------------------------------
    // Verdict parsing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Normalises the judge's reply into a verdict. Tolerates code fences and prose around the JSON.
    /// An unparseable reply becomes a conservative <em>not complete</em> verdict — the loop keeps
    /// working (and the stall guard eventually stops it) rather than declaring victory on garbage.
    /// </summary>
    internal static GoalVerdict ParseVerdict(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        var unparseable = new GoalVerdict(0.0, false, false, "Judge verdict could not be parsed.");
        if (text.Length == 0) return unparseable;

        foreach (var candidate in JsonCandidates(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                var root = doc.RootElement;

                var score = root.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number
                    ? Math.Clamp(s.GetDouble(), 0.0, 1.0)
                    : 0.0;
                var complete = root.TryGetProperty("complete", out var c) && c.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? c.GetBoolean()
                    : score >= 1.0;
                var blocked = root.TryGetProperty("blocked", out var b) && b.ValueKind == JsonValueKind.True;
                var missing = root.TryGetProperty("missing", out var mi) && mi.ValueKind == JsonValueKind.String
                    ? mi.GetString() ?? string.Empty
                    : string.Empty;

                // A "complete" verdict that still lists missing work contradicts itself — trust the
                // specific over the summary flag.
                if (complete && !string.IsNullOrWhiteSpace(missing)) complete = false;

                return new GoalVerdict(score, complete, blocked && !complete, missing.Trim());
            }
            catch (JsonException) { /* try the next candidate */ }
        }
        return unparseable;
    }

    private static IEnumerable<string> JsonCandidates(string text)
    {
        yield return text;
        var start = text.IndexOf('{');
        var end   = text.LastIndexOf('}');
        if (start >= 0 && end > start) yield return text[start..(end + 1)];
    }

    // -------------------------------------------------------------------------
    // Judge call
    // -------------------------------------------------------------------------

    /// <summary>
    /// Asks <paramref name="client"/> (no tools, fresh context) whether the objective is met.
    /// Provider failures propagate: unlike a critic, an unavailable judge must stop the loop, not
    /// silently approve or spin forever.
    /// </summary>
    internal static async Task<GoalVerdict> JudgeAsync(
        IChatClient client, string objective, IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken)
    {
        var prompt   = BuildJudgePrompt(objective, RenderTranscript(history));
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: cancellationToken);
        return ParseVerdict(response.Text);
    }
}
