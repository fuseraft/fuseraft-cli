using Microsoft.Extensions.AI;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Orchestration;

namespace fuseraft.Orchestration.Workflow;

/// <summary>
/// Correction-injection helpers used by <see cref="fuseraft.Orchestration.GraphOrchestrator"/>.
/// Each public method appends one or more <see cref="ChatRole.User"/> messages to
/// <paramref name="history"/> when an agent produces an invalid turn, then returns
/// so the executor loop can retry.
/// </summary>
internal static class CorrectionEngine
{
    // Well-known phase-break keywords used to detect foreign-keyword errors.
    internal static readonly HashSet<string> PhaseBreakKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "BUGS FOUND", "REVISION REQUIRED", "REPLAN REQUIRED", "APPROVED"
    };

    internal static readonly string[] ToolRefusalPhrases =
    [
        "tool use is disabled", "tools are disabled", "tool use disabled",
        "tool calling is disabled", "tool access is disabled", "cannot call",
        "can't call", "no tool access", "tools are not available",
        "tools are unavailable", "tool use is not available",
        "when tools available", "when tools are available",
        "once tools are available", "implement without tools",
        "implement this without tools", "without tools using",
        "re-enable tool", "re-enable tools", "enable tool use",
        "would run", "would write", "need the tools", "blocked by tool",
    ];

    // -------------------------------------------------------------------------
    // Public entry points
    // -------------------------------------------------------------------------

    internal static async Task InjectNoKeywordCorrection(
        List<ChatMessage> history,
        string responseText,
        string agentName,
        int consecutiveCount,
        AgentRouteTable routeTable,
        EventEmitter? eventEmitter = null,
        IReadOnlyList<ToolCallRecord>? turnToolCalls = null)
    {
        var validKeywordList = BuildValidKeywordList(routeTable);
        bool isReviewerType  = routeTable.IsReviewerType;

        if (TryInjectForeignKeywordCorrection(history, responseText, routeTable, agentName, validKeywordList)) return;
        if (TryInjectCodeBlockCorrection(history, responseText, isReviewerType, validKeywordList)) return;

        // Also treat as "has tool calls" when the AgentMessage records sub-agent tool calls
        // that ran inside a SubAgentPlugin — those don't produce ChatRole.Tool entries in the
        // outer history so CurrentTurnHasToolCalls would return false without this check.
        if (!CurrentTurnHasToolCalls(history) && (turnToolCalls is null || turnToolCalls.Count == 0))
        {
            InjectNoToolCallsCorrection(history, isReviewerType, validKeywordList);
            return;
        }

        if (TryInjectBuildRevertCorrection(history, validKeywordList)) return;
        if (consecutiveCount >= 2 && await TryInjectStagnationCorrection(history, agentName, consecutiveCount, validKeywordList, eventEmitter)) return;
        if (await TryInjectHallucinationCorrection(history, responseText, agentName, consecutiveCount, validKeywordList, eventEmitter)) return;

        var failedShellOutput = ScanForFailedShellOutput(history);
        if (consecutiveCount >= 2 && failedShellOutput is not null)
        {
            await InjectPersistentBuildFailureCorrection(history, agentName, consecutiveCount, validKeywordList, failedShellOutput, eventEmitter);
            return;
        }

        await InjectFinalCorrection(history, agentName, consecutiveCount, validKeywordList, eventEmitter, failedShellOutput);
    }

    internal static async Task InjectValidationError(
        List<ChatMessage> history,
        string errorMessage,
        int consecutiveCount,
        string responseText,
        string foundKeyword,
        EventEmitter? eventEmitter = null,
        int maxRetries = GraphOrchestrator.DefaultMaxRetries)
    {
        // On second+ retry, check whether the agent actually called any tools.
        if (consecutiveCount > 1 && !CurrentTurnHasToolCalls(history))
        {
            history.Add(new ChatMessage(ChatRole.User,
                "NO TOOL CALLS: You re-emitted the keyword without corrective action. " +
                "Next response MUST start with a tool call — no keyword until error below is resolved."));
        }

        // Tool-refusal correction.
        if (responseText.Contains("```") ||
            ToolRefusalPhrases.Any(p => responseText.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            history.Add(new ChatMessage(ChatRole.User,
                "CRITICAL: Code blocks are NOT written to disk. All tools available: " +
                "write_file, shell_run, read_file, git_add, git_commit. " +
                "Next response must start with a tool call."));
        }

        // Special case for APPROVED blocked by RequireShellPass.
        if (string.Equals(foundKeyword, "APPROVED", StringComparison.OrdinalIgnoreCase) &&
            errorMessage.Contains("shell_run", StringComparison.OrdinalIgnoreCase))
        {
            history.Add(new ChatMessage(ChatRole.User,
                "APPROVED rejected: no successful shell_run tool call found in this response turn.\n\n" +
                "1. Text is not a tool call — writing 'shell_run(...)' as text has zero effect.\n" +
                "2. Each turn is checked independently — prior-turn shell_run does not carry forward.\n\n" +
                "Next response, in order:\n" +
                "  1. Invoke the shell_run tool and wait for the result.\n" +
                "  2. Write APPROVED on its own line immediately after.\n" +
                "(Both in the same response.)"));
            return;
        }

        // Append the most recent failed shell output so the agent knows why the build
        // failed. This is critical in TextOnly mode where ChatRole.Tool messages are
        // stripped between turns — without this the agent has no compiler error context.
        var failedOutput = ScanForFailedShellOutput(history);
        var buildDetail  = failedOutput is not null
            ? $"\n\nThe most recent failed shell command produced this output:\n{failedOutput}"
            : string.Empty;

        // Every branch must start with a prefix ContextWindowFilter.IsCorrectionMessage
        // recognizes (see CorrectionPrefixes) — agents on the declared-context/artifact_spec
        // path only see ChatRole.User history that matches one of those prefixes, so an
        // unprefixed first-occurrence message is silently invisible to them and they repeat
        // the same mistake next turn with no idea why it was rejected.
        var errorToInject = consecutiveCount > 1
            ? $"RETRY {consecutiveCount}/{maxRetries} — Previous attempt did not resolve this. Do not repeat it.\n\n" +
              errorMessage + buildDetail
            : $"VALIDATION FAILED — {errorMessage}" + buildDetail;

        history.Add(new ChatMessage(ChatRole.User, errorToInject));
        await (eventEmitter?.EmitAsync(EventTypes.CorrectionInjected,
            payload: new { type = "validation_error", keyword = foundKeyword, consecutive = consecutiveCount }) ?? Task.CompletedTask);
    }

    // -------------------------------------------------------------------------
    // Shared helpers (internal so GraphOrchestrator can reuse BuildValidKeywordList)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a sorted, quoted, comma-separated list of all keywords valid for
    /// <paramref name="routeTable"/>, or a placeholder when none are configured.
    /// </summary>
    internal static string BuildValidKeywordList(AgentRouteTable routeTable)
    {
        var keywords = routeTable.Routes.Keys
            .Concat(routeTable.PhaseBreakKeywords)
            .Concat(routeTable.ParallelKeywords)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(k => $"'{k}'")
            .ToList();

        return keywords.Count > 0
            ? string.Join(", ", keywords)
            : "(none configured — contact your workflow author)";
    }

    /// <summary>
    /// Scans backward from the most recent message to the nearest user boundary and
    /// returns the output of the first failed shell_run found (prefixed with [EXIT).
    /// Returns null when no failed shell output is present in the current turn.
    /// </summary>
    internal static string? ScanForFailedShellOutput(IList<ChatMessage> history)
    {
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.User) break;
            if (history[i].Role != ChatRole.Tool) continue;

            foreach (var item in history[i].Contents)
            {
                if (item is not FunctionResultContent frc) continue;
                var result = frc.Result?.ToString() ?? string.Empty;
                if (!result.StartsWith("[EXIT", StringComparison.Ordinal)) continue;

                const int MaxErrorChars = 1200;
                return result.Length > MaxErrorChars
                    ? result[..MaxErrorChars] + "\n...(truncated — use shell_run to re-run and see the full output)"
                    : result;
            }
        }
        return null;
    }

    /// <summary>
    /// Extracts the target filename from a <c>sed -i</c> shell command.
    /// Returns null when the command is not a sed -i invocation or no filename can be parsed.
    /// </summary>
    internal static string? ExtractSedTargetFile(string cmd)
    {
        if (!cmd.Contains("sed -i", StringComparison.Ordinal)) return null;
        var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // The filename is typically the last token that looks like a path (contains '.' or '/')
        // and is not a flag or a quoted sed expression.
        var file = parts.LastOrDefault(p =>
            !p.StartsWith('-') &&
            !p.StartsWith('\'') &&
            !p.StartsWith('"') &&
            (p.Contains('.') || p.Contains('/')));
        return string.IsNullOrEmpty(file) ? null : file.Trim('\'', '"');
    }

    // -------------------------------------------------------------------------
    // InjectNoKeywordCorrection — named per-branch helpers
    // -------------------------------------------------------------------------

    // Returns true and injects a WRONG KEYWORD message when the response contains a
    // keyword that belongs to another agent.
    private static bool TryInjectForeignKeywordCorrection(
        List<ChatMessage> history,
        string responseText,
        AgentRouteTable routeTable,
        string agentName,
        string validKeywordList)
    {
        // Check BOTH phase-break keywords (APPROVED, BUGS FOUND, etc.) AND send-forward
        // keywords from other agents' route tables (e.g. "HANDOFF TO DEVELOPER" emitted by
        // Developer). Without the latter, the model receives a generic "no keyword" correction
        // that it typically ignores, causing an infinite retry loop.
        string? foreignKeyword = null;

        foreach (var keyword in PhaseBreakKeywords)
        {
            if (!routeTable.PhaseBreakKeywords.Contains(keyword) &&
                KeywordDetector.IsKeywordOnOwnLineStrict(responseText, keyword))
            {
                foreignKeyword = keyword;
                break;
            }
        }

        if (foreignKeyword is null)
        {
            foreach (var keyword in routeTable.ForeignSendForwardKeywords)
            {
                if (KeywordDetector.IsKeywordOnOwnLineStrict(responseText, keyword))
                {
                    foreignKeyword = keyword;
                    break;
                }
            }
        }

        if (foreignKeyword is null) return false;

        history.Add(new ChatMessage(ChatRole.User,
            $"WRONG KEYWORD: '{foreignKeyword}' belongs to a different agent — not valid for {agentName}.\n\n" +
            $"Valid keywords: {validKeywordList}\n\n" +
            $"Emit the correct keyword when done, or complete remaining work first."));
        return true;
    }

    // Returns true and injects a code-block correction when the response contains ``` or
    // tool-refusal phrases. Reviewer-type agents (GraphNodeConfig.ReviewerType) get a
    // specialized message because their ```json judgement block is intentional.
    private static bool TryInjectCodeBlockCorrection(
        List<ChatMessage> history,
        string responseText,
        bool isReviewerType,
        string validKeywordList)
    {
        bool hasCodeBlock = responseText.Contains("```");

        if (hasCodeBlock && isReviewerType)
        {
            history.Add(new ChatMessage(ChatRole.User,
                $"JSON block correct — follow the closing ``` immediately with your decision keyword on its own line.\n\n" +
                $"Valid keywords: {validKeywordList}\n\n" +
                $"Format (in one response): ```json\n{{ ... }}\n```\n<keyword>"));
            return true;
        }

        if (hasCodeBlock ||
            ToolRefusalPhrases.Any(p => responseText.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            history.Add(new ChatMessage(ChatRole.User,
                "CRITICAL: Code blocks are NOT written to disk — no filesystem effect. " +
                "All tools available: write_file, shell_run, read_file, git_add, git_commit.\n\n" +
                $"Valid keywords: {validKeywordList}\n\n" +
                "Next response: tool call first, correct keyword last."));
            return true;
        }

        return false;
    }

    // Injects a NO TOOL CALLS message. The message is specialized for reviewer-type agents.
    private static void InjectNoToolCallsCorrection(
        List<ChatMessage> history,
        bool isReviewerType,
        string validKeywordList)
    {
        if (isReviewerType)
        {
            history.Add(new ChatMessage(ChatRole.User,
                $"NO TOOL CALLS: You described your review without calling any tools. Required:\n" +
                $"  1. shell_run — run the tests (required before APPROVED).\n" +
                $"  2. read_file — verify changed files.\n" +
                $"  3. Emit decision keyword after tool calls.\n\n" +
                $"Valid keywords: {validKeywordList}"));
        }
        else
        {
            history.Add(new ChatMessage(ChatRole.User,
                $"NO TOOL CALLS AND NO KEYWORD.\n" +
                $"  A. Work complete → respond with the handoff keyword only, no prose.\n" +
                $"  B. Work remains → begin next response with a tool call (write_file, shell_run, etc.).\n\n" +
                $"Valid keywords: {validKeywordList}"));
        }
    }

    // Returns true and injects a BUILD FAILURE message when the turn contains both a
    // non-zero shell exit AND a git restore/reset — the agent reverted instead of fixing.
    private static bool TryInjectBuildRevertCorrection(
        List<ChatMessage> history,
        string validKeywordList)
    {
        bool sawExitError = false;
        bool sawGitRevert = false;

        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.User) break;

            if (history[i].Role == ChatRole.Tool)
            {
                foreach (var item in history[i].Contents)
                {
                    if (item is FunctionResultContent frc)
                    {
                        var result = frc.Result?.ToString() ?? string.Empty;
                        if (result.StartsWith("[EXIT", StringComparison.Ordinal) ||
                            result.StartsWith("[ERROR]", StringComparison.Ordinal))
                            sawExitError = true;
                    }
                }
            }

            if (history[i].Role == ChatRole.Assistant)
            {
                foreach (var item in history[i].Contents)
                {
                    if (item is FunctionCallContent fc && fc.Name is "shell_run" or "shell_run_script")
                    {
                        var cmd = fc.Arguments?.TryGetValue("command", out var v) == true
                            ? v?.ToString() ?? string.Empty : string.Empty;
                        if (cmd.Contains("git restore", StringComparison.OrdinalIgnoreCase) ||
                            cmd.Contains("git reset",   StringComparison.OrdinalIgnoreCase))
                            sawGitRevert = true;
                    }
                }
            }
        }

        if (!sawExitError || !sawGitRevert) return false;

        history.Add(new ChatMessage(ChatRole.User,
            "BUILD FAILURE: You ran git restore/reset after a non-zero exit without diagnosing the error.\n\n" +
            "Fix, don't revert:\n" +
            "  1. Read the error from tool output above.\n" +
            "  2. Fix with write_file.\n" +
            "  3. Re-run the build.\n\n" +
            $"Valid keywords: {validKeywordList}"));
        return true;
    }

    // Returns true and injects a STAGNATION or STUCK message when the agent has made no
    // successful write-side calls for consecutiveCount turns. Only called when consecutiveCount >= 2.
    private static async Task<bool> TryInjectStagnationCorrection(
        List<ChatMessage> history,
        string agentName,
        int consecutiveCount,
        string validKeywordList,
        EventEmitter? eventEmitter)
    {
        var callResults = BuildCallSuccessMap(history);

        bool hasSuccessfulWriteSideCalls = false;
        bool hasFailedWriteAttempts      = false;
        string[] pureWriteTools = ["write_file", "patch_file", "delete_file", "git_add", "git_commit"];

        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.User) break;
            if (history[i].Role != ChatRole.Assistant) continue;

            foreach (var item in history[i].Contents)
            {
                if (item is not FunctionCallContent fc) continue;
                var callId = fc.CallId ?? fc.Name;
                bool callOk = callResults.TryGetValue(callId, out var s) ? s : true;

                if (Array.IndexOf(pureWriteTools, fc.Name) >= 0)
                {
                    if (callOk) hasSuccessfulWriteSideCalls = true;
                    else        hasFailedWriteAttempts      = true;
                }
                else if (fc.Name is "shell_run" or "shell_run_script")
                {
                    var args = fc.Arguments?.ToString() ?? string.Empty;
                    bool isReadOnlyShell = System.Text.RegularExpressions.Regex.IsMatch(args,
                        @"sed\s+-n\b|git\s+(diff|status|log|show|blame|stash\s+list)\b|^\s*(cat|ls|find|grep|rg|wc|head|tail|echo)\b",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!isReadOnlyShell && callOk)
                        hasSuccessfulWriteSideCalls = true;
                }
            }

            if (hasSuccessfulWriteSideCalls) break;
        }

        if (hasSuccessfulWriteSideCalls) return false;

        var stagnationMsg = hasFailedWriteAttempts
            ? $"STUCK — ALL WRITES REJECTED ({consecutiveCount} turns): oldText does not match exactly.\n\n" +
              $"  1. grep_in_file(path, \"distinctive line\") → get line number.\n" +
              $"  2. read_file(path, startLine=<line-2>, maxLines=10) → copy verbatim text.\n" +
              $"  3. Paste verbatim as oldText — do not retype from memory.\n" +
              $"  4. patch_file with that oldText.\n\n" +
              $"Execute steps 1–4 for ONE file now. No exploration.\n\nValid keywords: {validKeywordList}"
            : $"STAGNATION ({consecutiveCount} read-only turns): This turn MUST write something:\n" +
              $"  • write_file / patch_file / shell_run(\"sed -i ...\") / shell_run(\"bash build.sh\")\n\n" +
              $"Pick the first file in files_to_change and write it now. No more reads.\n\nValid keywords: {validKeywordList}";

        history.Add(new ChatMessage(ChatRole.User, stagnationMsg));
        await (eventEmitter?.EmitAsync(EventTypes.CorrectionInjected,
            agent:   agentName,
            payload: new { type = hasFailedWriteAttempts ? "stagnation_failed_writes" : "stagnation", consecutive = consecutiveCount }) ?? Task.CompletedTask);
        return true;
    }

    // Returns true and injects a HALLUCINATION message when the agent claims it wrote code
    // but no write-side tool call succeeded in the current turn.
    private static async Task<bool> TryInjectHallucinationCorrection(
        List<ChatMessage> history,
        string responseText,
        string agentName,
        int consecutiveCount,
        string validKeywordList,
        EventEmitter? eventEmitter)
    {
        bool claimsImplementation = System.Text.RegularExpressions.Regex.IsMatch(
            responseText,
            @"\b(implemented|added|modified|updated|created|wrote|inserted|patched|fixed)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!claimsImplementation) return false;

        bool wroteAnything = false;
        string[] writingTools = ["write_file", "patch_file", "delete_file", "git_add", "git_commit"];

        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.User) break;
            if (history[i].Role != ChatRole.Assistant) continue;

            foreach (var part in history[i].Contents.OfType<FunctionCallContent>())
            {
                if (Array.IndexOf(writingTools, part.Name) >= 0)
                {
                    wroteAnything = true;
                    break;
                }
                if (part.Name is "shell_run" or "shell_run_script")
                {
                    var args = part.Arguments?.ToString() ?? string.Empty;
                    if (args.Contains("sed -i", StringComparison.Ordinal) ||
                        args.Contains("build.sh", StringComparison.Ordinal))
                    {
                        wroteAnything = true;
                        break;
                    }
                }
            }
            if (wroteAnything) break;
        }

        if (wroteAnything) return false;

        history.Add(new ChatMessage(ChatRole.User,
            $"HALLUCINATION: You claimed implementation but no write_file/patch_file/sed -i/git_add ran — nothing was written. " +
            $"Call write_file or patch_file now; describing code has no effect.\n\nValid keywords: {validKeywordList}"));
        await (eventEmitter?.EmitAsync(EventTypes.CorrectionInjected,
            agent:   agentName,
            payload: new { type = "hallucination", consecutive = consecutiveCount }) ?? Task.CompletedTask);
        return true;
    }

    // Injects a PERSISTENT BUILD FAILURE message with a diff audit hint.
    // Only called when consecutiveCount >= 2 and failedShellOutput is not null.
    private static async Task InjectPersistentBuildFailureCorrection(
        List<ChatMessage> history,
        string agentName,
        int consecutiveCount,
        string validKeywordList,
        string failedShellOutput,
        EventEmitter? eventEmitter)
    {
        var patchedFiles = new HashSet<string>(StringComparer.Ordinal);

        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.User) break;
            if (history[i].Role != ChatRole.Assistant) continue;

            foreach (var item in history[i].Contents)
            {
                if (item is not FunctionCallContent fc) continue;
                if (fc.Name is not ("shell_run" or "shell_run_script")) continue;
                var cmd = fc.Arguments?.TryGetValue("command", out var v) == true
                    ? v?.ToString() ?? string.Empty : string.Empty;
                var file = ExtractSedTargetFile(cmd);
                if (!string.IsNullOrEmpty(file)) patchedFiles.Add(file!);
            }
        }

        var filesHint = patchedFiles.Count > 0
            ? $"\n\nFiles you have been patching:\n" +
              string.Join("\n", patchedFiles.Select(f => $"  - {f}")) +
              $"\n\nFor each file, run:\n" +
              $"  shell_run(\"git diff <file>\") to see all accumulated changes.\n" +
              $"  shell_run(\"git checkout -- <file>\") to reset it if it is too tangled, " +
              $"then re-apply your edits in a single targeted pass."
            : string.Empty;

        history.Add(new ChatMessage(ChatRole.User,
            $"PERSISTENT BUILD FAILURE ({consecutiveCount} turns): Patching without seeing the full diff compounds damage.\n\n" +
            $"BUILD ERROR:\n{failedShellOutput}{filesHint}\n\n" +
            $"  1. shell_run(\"git diff <file>\") on each edited file — repeated patches can corrupt structure.\n" +
            $"  2. Fix only the specific compiler error.\n" +
            $"  3. If tangled: shell_run(\"git checkout -- <file>\"), re-apply edits in one pass.\n" +
            $"  4. Re-run the build.\n\nValid keywords: {validKeywordList}"));
        await (eventEmitter?.EmitAsync(EventTypes.CorrectionInjected,
            agent:   agentName,
            payload: new { type = "persistent_build_failure", consecutive = consecutiveCount }) ?? Task.CompletedTask);
    }

    // Injects the generic no-keyword correction message. Embeds file write results,
    // build failure output, failed-write errors, and directory-query reminder so the
    // message survives ContextWindowFilter (which strips Tool messages between turns).
    private static async Task InjectFinalCorrection(
        List<ChatMessage> history,
        string agentName,
        int consecutiveCount,
        string validKeywordList,
        EventEmitter? eventEmitter,
        string? failedShellOutput)
    {
        var callResults = BuildCallSuccessMap(history);
        var (filesWritten, failedWriteErrors) = CollectWriteResults(history, callResults);
        var directoryQueryReminder = ScanForDirectoryQueryMisuse(history);
        var failedWriteSection     = BuildFailedWriteSection(failedWriteErrors);

        if (filesWritten.Count > 0)
        {
            var fileList = string.Join("\n", filesWritten.Select(f => $"  - {f}"));
            var buildSection = failedShellOutput is not null
                ? $"\n\nBUILD FAILURE: A shell command in your last turn exited with a non-zero " +
                  $"code. The error output was:\n{failedShellOutput}\n\n" +
                  $"Read the error, fix the specific line(s) with patch_file or write_file, then re-run the build."
                : string.Empty;

            history.Add(new ChatMessage(ChatRole.User,
                $"Files written this turn (already on disk):\n" +
                $"{fileList}{buildSection}{failedWriteSection}{directoryQueryReminder}\n\n" +
                $"  A. Build passes → emit handoff keyword now.\n" +
                $"  B. Build failed → fix with patch_file/write_file, re-run, then emit keyword.\n\n" +
                $"Valid keywords: {validKeywordList}"));
            await (eventEmitter?.EmitAsync(EventTypes.CorrectionInjected,
                agent:   agentName,
                payload: new { type = "files_written_no_keyword", consecutive = consecutiveCount }) ?? Task.CompletedTask);
        }
        else
        {
            var buildSection = failedShellOutput is not null
                ? $"\n\nBUILD FAILURE: A shell command in your last turn exited with a non-zero " +
                  $"code. The error output was:\n{failedShellOutput}\n\n" +
                  $"Do NOT research — read the error above and fix the specific line(s) " +
                  $"with patch_file or write_file. Then re-run the build and emit the handoff keyword.\n"
                : string.Empty;

            history.Add(new ChatMessage(ChatRole.User,
                $"No handoff keyword emitted.{buildSection}{failedWriteSection}{directoryQueryReminder}\n" +
                $"Valid keywords: {validKeywordList}\n\n" +
                $"Work complete → emit keyword as your entire response. Work remains → one tool call, then keyword."));
            await (eventEmitter?.EmitAsync(EventTypes.CorrectionInjected,
                agent:   agentName,
                payload: new { type = failedWriteErrors.Count > 0 ? "failed_write_no_keyword" : "no_keyword_generic", consecutive = consecutiveCount }) ?? Task.CompletedTask);
        }
    }

    // -------------------------------------------------------------------------
    // Private history-scan helpers
    // -------------------------------------------------------------------------

    // Returns true when the current turn (since the last ChatRole.User boundary) contains
    // at least one ChatRole.Tool message, meaning the agent made at least one tool call.
    private static bool CurrentTurnHasToolCalls(List<ChatMessage> history)
    {
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.User) break;
            if (history[i].Role == ChatRole.Tool) return true;
        }
        return false;
    }

    // Scans the current turn and builds a callId → succeeded map from FunctionResultContent.
    // A result is considered failed when it starts with a known error prefix.
    private static Dictionary<string, bool> BuildCallSuccessMap(List<ChatMessage> history)
    {
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);

        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.User) break;

            foreach (var item in history[i].Contents)
            {
                if (item is not FunctionResultContent fr) continue;
                var key  = fr.CallId ?? string.Empty;
                var text = fr.Result?.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(key))
                    map[key] = !text.StartsWith("[ERROR]",     StringComparison.Ordinal)
                             && !text.StartsWith("[DENIED]",    StringComparison.Ordinal)
                             && !text.StartsWith("[TIMEOUT]",   StringComparison.Ordinal)
                             && !text.StartsWith("[NOT FOUND]", StringComparison.Ordinal)
                             && !text.StartsWith("[EXIT ",      StringComparison.Ordinal);
            }
        }

        return map;
    }

    // Correlates write/patch/sed tool calls with their results to produce a set of
    // files successfully written and a list of failed-write errors for the correction message.
    private static (HashSet<string> FilesWritten, List<(string Path, string Error)> FailedErrors)
        CollectWriteResults(List<ChatMessage> history, Dictionary<string, bool> callResults)
    {
        var filesWritten     = new HashSet<string>(StringComparer.Ordinal);
        var failedWriteErrors = new List<(string Path, string Error)>();
        var writeCalls       = new List<(string CallId, string? Path)>();

        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.User) break;
            if (history[i].Role != ChatRole.Assistant) continue;

            foreach (var item in history[i].Contents)
            {
                if (item is not FunctionCallContent fc) continue;

                string? path   = null;
                string  callId = fc.CallId ?? fc.Name;

                if (fc.Name is "write_file" or "patch_file")
                {
                    path = fc.Arguments?.TryGetValue("path", out var p) == true ? p?.ToString() : null;
                }
                else if (fc.Name is "shell_run" or "shell_run_script")
                {
                    var cmd = fc.Arguments?.TryGetValue("command", out var cv) == true
                        ? cv?.ToString() ?? string.Empty : string.Empty;
                    path = ExtractSedTargetFile(cmd);
                }

                if (path is not null) writeCalls.Add((callId, path));
            }
        }

        foreach (var (callId, path) in writeCalls)
        {
            bool ok = callResults.TryGetValue(callId, out var s) ? s : true;
            if (ok)
            {
                filesWritten.Add(path!);
                continue;
            }

            // Locate the error text for this specific call.
            string errText = string.Empty;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].Role == ChatRole.User) break;
                foreach (var item in history[i].Contents)
                {
                    if (item is FunctionResultContent fr && (fr.CallId ?? string.Empty) == callId)
                    {
                        errText = StringHelpers.Truncate(fr.Result?.ToString() ?? string.Empty, 400);
                        break;
                    }
                }
                if (errText.Length > 0) break;
            }
            failedWriteErrors.Add((path!, errText));
        }

        return (filesWritten, failedWriteErrors);
    }

    // Returns a reminder string when search_content was called with a directory path as query.
    private static string ScanForDirectoryQueryMisuse(List<ChatMessage> history)
    {
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.User) break;
            if (history[i].Role != ChatRole.Tool) continue;

            foreach (var item in history[i].Contents)
            {
                if (item is FunctionResultContent frc &&
                    (frc.Result?.ToString() ?? string.Empty)
                        .Contains("looks like a directory path", StringComparison.Ordinal))
                {
                    return "\n\nNOTE: search_content 'query' is a text pattern, not a directory path. " +
                           "Use 'directory' param for scoping: search_content(query: \"pattern\", directory: \"src/Parsing\"). " +
                           "For listings: shell_run(\"grep -rn \\\"pattern\\\" src/VM\").";
                }
            }
        }
        return string.Empty;
    }

    // Builds the FAILED WRITES section for a correction message, or returns empty string.
    private static string BuildFailedWriteSection(List<(string Path, string Error)> failedWriteErrors)
    {
        if (failedWriteErrors.Count == 0) return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("\n\nFAILED WRITES — these files were NOT written to disk:");
        foreach (var (path, err) in failedWriteErrors)
        {
            sb.AppendLine($"  - {path}");
            if (!string.IsNullOrEmpty(err))
                sb.AppendLine($"    Error: {err}");
        }
        sb.AppendLine(
            "Use patch_file(path, oldText, newText) for targeted edits. " +
            "Never use write_file to overwrite large files — the truncation guard will block it.");
        return sb.ToString().TrimEnd();
    }
}
