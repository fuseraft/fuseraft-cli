using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Core.Skills;

namespace fuseraft.Orchestration.Skills;

/// <summary>Outcome of a single skill curation attempt.</summary>
public enum SkillCurationOutcome
{
    /// <summary>A new SKILL.md was written.</summary>
    Created,
    /// <summary>An existing SKILL.md was updated in place.</summary>
    Updated,
    /// <summary>Curation was skipped because the session had too few turns.</summary>
    Skipped,
    /// <summary>The LLM reviewed the session and determined no portable skill is warranted.</summary>
    NoSkill,
    /// <summary>Curation failed due to an LLM error, write error, or malformed response.</summary>
    Failed,
}

/// <summary>Full result of a skill curation attempt.</summary>
public sealed record SkillCurationResult(
    SkillCurationOutcome Outcome,
    string?              Slug          = null,
    string?              Path          = null,
    string?              FailureReason = null,
    int                  TurnsDigested = 0,
    string?              Model         = null)
{
    /// <summary>True when a skill file was written (Created or Updated).</summary>
    public bool WroteSkill => Outcome is SkillCurationOutcome.Created or SkillCurationOutcome.Updated;
}

/// <summary>
/// Post-session curator that reviews a completed session and writes reusable procedural
/// knowledge to the skills library as a SKILL.md file.
///
/// <para>
/// The curator makes a single LLM call with a text-only digest of the session — not the
/// full conversation history. Tool frames are excluded; only assistant text turns and a
/// summary of actions taken (files written, commands run) are included.
/// </para>
///
/// <para>
/// Skills are written to <c>{LibraryPath}/{slug}/SKILL.md</c>. Existing skills with the
/// same slug are updated in place; the curator never deletes.
/// </para>
///
/// <para>
/// Every attempt — success, skip, or failure — is appended to the curation log at
/// <c>LogPath</c> (default <c>~/.fuseraft/skill-curation.jsonl</c>). This provides a
/// persistent record for measuring curation quality over time.
/// </para>
/// </summary>
public sealed class SkillCurator(
    IChatClient chatClient,
    SkillCurationConfig config,
    EvidenceStore? evidenceStore,
    ILogger<SkillCurator> logger)
{
    private static readonly Regex SkillBlock =
        new(@"<SKILL>(.*?)</SKILL>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions LogJsonOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Evaluates the session and writes a SKILL.md to the library when one is warranted.
    /// Never throws — curation is best-effort and must not fail the run.
    /// </summary>
    /// <param name="checkpoint">Session checkpoint (provides task description and session ID).</param>
    /// <param name="messages">All messages from the session.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="source">
    /// Label for the curation log (e.g. <c>"run"</c> or <c>"repl"</c>)
    /// to distinguish which command surface triggered curation.
    /// </param>
    public async Task<SkillCurationResult> RunAsync(
        SessionCheckpoint checkpoint,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken ct,
        string source = "run")
    {
        var modelId = chatClient.GetService<ChatClientMetadata>()?.DefaultModelId;

        var assistantTurns = messages.Count(m => m.Role == MessageRole.Assistant);
        if (assistantTurns < config.MinTurns)
        {
            var reason = $"Only {assistantTurns} assistant turn{(assistantTurns == 1 ? "" : "s")} (min {config.MinTurns}).";
            logger.LogDebug("Skill curation skipped — {Reason}", reason);
            var skipped = new SkillCurationResult(
                SkillCurationOutcome.Skipped, FailureReason: reason, Model: modelId);
            await AppendCurationLogAsync(checkpoint.SessionId, skipped, source, ct);
            return skipped;
        }

        var digestTurns = Math.Min(assistantTurns, config.DigestTurns);
        logger.LogDebug(
            "Skill curation starting — session={Session} turns={Turns} digest={Digest} model={Model}",
            checkpoint.SessionId, assistantTurns, digestTurns, modelId);

        var digest   = await BuildDigestAsync(checkpoint, messages, ct);
        var response = await EvaluateAsync(digest, ct);

        if (string.IsNullOrWhiteSpace(response))
        {
            const string emptyReason = "LLM returned an empty response.";
            logger.LogWarning(
                "Skill curation failed — session={Session} reason={Reason}",
                checkpoint.SessionId, emptyReason);
            var failed = new SkillCurationResult(
                SkillCurationOutcome.Failed,
                FailureReason: emptyReason,
                TurnsDigested: digestTurns,
                Model: modelId);
            await AppendCurationLogAsync(checkpoint.SessionId, failed, source, ct);
            return failed;
        }

        logger.LogDebug(
            "Skill curation LLM response — session={Session} length={Length} preview={Preview}",
            checkpoint.SessionId,
            response.Length,
            response.Length > 120 ? response[..120] + "…" : response);

        // Intentional "no skill" signal from the model.
        if (response.Contains("NO_SKILL", StringComparison.OrdinalIgnoreCase) && !SkillBlock.IsMatch(response))
        {
            logger.LogInformation(
                "Skill curation: no portable skill identified — session={Session} turns={Turns}",
                checkpoint.SessionId, digestTurns);
            var noSkill = new SkillCurationResult(
                SkillCurationOutcome.NoSkill,
                TurnsDigested: digestTurns,
                Model: modelId);
            await AppendCurationLogAsync(checkpoint.SessionId, noSkill, source, ct);
            return noSkill;
        }

        var match = SkillBlock.Match(response);
        if (!match.Success)
        {
            var badFormatReason = "LLM response contained neither a <SKILL> block nor NO_SKILL.";
            logger.LogWarning(
                "Skill curation failed — session={Session} reason={Reason} response={Response}",
                checkpoint.SessionId, badFormatReason,
                response.Length > 300 ? response[..300] + "…" : response);
            var failed = new SkillCurationResult(
                SkillCurationOutcome.Failed,
                FailureReason: badFormatReason,
                TurnsDigested: digestTurns,
                Model: modelId);
            await AppendCurationLogAsync(checkpoint.SessionId, failed, source, ct);
            return failed;
        }

        var skillContent = match.Groups[1].Value.Trim();
        var frontmatter  = SkillFrontmatterSpec.TryParse(skillContent);

        if (string.IsNullOrWhiteSpace(frontmatter?.Name))
        {
            const string noNameReason = "SKILL block is missing the 'name:' frontmatter field.";
            logger.LogWarning(
                "Skill curation failed — session={Session} reason={Reason}",
                checkpoint.SessionId, noNameReason);
            var failed = new SkillCurationResult(
                SkillCurationOutcome.Failed,
                FailureReason: noNameReason,
                TurnsDigested: digestTurns,
                Model: modelId);
            await AppendCurationLogAsync(checkpoint.SessionId, failed, source, ct);
            return failed;
        }

        if (!SkillFrontmatterSpec.ValidateDescription(frontmatter.Description, out var descReason))
        {
            logger.LogWarning(
                "Skill curation failed — session={Session} reason={Reason}",
                checkpoint.SessionId, descReason);
            var failed = new SkillCurationResult(
                SkillCurationOutcome.Failed,
                FailureReason: descReason,
                TurnsDigested: digestTurns,
                Model: modelId);
            await AppendCurationLogAsync(checkpoint.SessionId, failed, source, ct);
            return failed;
        }

        var slug = SkillFrontmatterSpec.ToSlug(frontmatter.Name);
        if (!SkillFrontmatterSpec.ValidateName(slug, out var slugReason))
        {
            var badSlugReason = $"Derived slug '{slug}' from name '{frontmatter.Name}' is invalid: {slugReason}";
            logger.LogWarning(
                "Skill curation failed — session={Session} reason={Reason}",
                checkpoint.SessionId, badSlugReason);
            var failed = new SkillCurationResult(
                SkillCurationOutcome.Failed,
                FailureReason: badSlugReason,
                TurnsDigested: digestTurns,
                Model: modelId);
            await AppendCurationLogAsync(checkpoint.SessionId, failed, source, ct);
            return failed;
        }

        // Guarantee the written file's 'name:' matches the directory it's written under — the
        // LLM's raw name may need slugifying (spaces, uppercase, ...), and without this the file
        // and its own directory would disagree, which fuseraft's orchestration skills provider
        // treats as invalid and silently drops.
        skillContent = SkillFrontmatterSpec.WithCanonicalName(skillContent, slug);

        try
        {
            var (skillPath, isUpdate) = await WriteSkillAsync(slug, skillContent, ct);
            var outcome = isUpdate ? SkillCurationOutcome.Updated : SkillCurationOutcome.Created;

            logger.LogInformation(
                "Skill {Verb} — session={Session} slug={Slug} path={Path} turns={Turns}",
                isUpdate ? "updated" : "created",
                checkpoint.SessionId, slug, skillPath, digestTurns);

            var result = new SkillCurationResult(outcome, slug, skillPath,
                TurnsDigested: digestTurns, Model: modelId);
            await AppendCurationLogAsync(checkpoint.SessionId, result, source, ct);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var writeReason = $"Write failed: {ex.Message}";
            logger.LogError(ex,
                "Skill curation write failed — session={Session} slug={Slug}",
                checkpoint.SessionId, slug);
            var failed = new SkillCurationResult(
                SkillCurationOutcome.Failed,
                Slug: slug,
                FailureReason: writeReason,
                TurnsDigested: digestTurns,
                Model: modelId);
            await AppendCurationLogAsync(checkpoint.SessionId, failed, source, ct);
            return failed;
        }
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private async Task<string> BuildDigestAsync(
        SessionCheckpoint checkpoint,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"TASK: {checkpoint.Task}");
        sb.AppendLine();

        // Text-only assistant turns, capped to DigestTurns most recent
        var textTurns = messages
            .Where(m => m.Role == MessageRole.Assistant && !string.IsNullOrWhiteSpace(m.Content))
            .TakeLast(config.DigestTurns)
            .ToList();

        if (textTurns.Count > 0)
        {
            sb.AppendLine("SESSION DIGEST:");
            foreach (var msg in textTurns)
            {
                var agent = string.IsNullOrWhiteSpace(msg.AgentName) ? "Agent" : msg.AgentName;
                var text  = msg.Content.Length > 2000 ? msg.Content[..2000] + "…" : msg.Content;
                sb.AppendLine($"[{agent}]: {text}");
                sb.AppendLine();
            }
        }

        if (evidenceStore is not null)
        {
            var files    = await evidenceStore.GetWrittenFilePathsAsync(ct);
            var commands = await evidenceStore.GetSucceededCommandsAsync(ct);

            if (files.Count > 0 || commands.Count > 0)
            {
                sb.AppendLine("ACTIONS TAKEN:");
                if (files.Count > 0)
                    sb.AppendLine($"  Files written: {string.Join(", ", files.Take(20))}");
                if (commands.Count > 0)
                    sb.AppendLine($"  Commands run: {string.Join(", ", commands.Take(10))}");
            }
        }

        return sb.ToString();
    }

    private async Task<string?> EvaluateAsync(string digest, CancellationToken ct)
    {
        const string system = """
            You are a skill curator for an AI agent orchestration system. After reviewing a completed session, decide whether it produced reusable procedural knowledge worth saving.

            A skill IS warranted when:
            - The session solved a non-trivial, multi-step problem
            - The approach is generalizable — it applies to similar future tasks
            - The steps are concrete and actionable (not just vague advice)

            A skill is NOT warranted for:
            - Trivial one-step tasks (rename a variable, add a single line)
            - Tasks entirely specific to one codebase with no generalizable pattern
            - Sessions that failed to produce a clean, repeatable result

            OUTPUT RULES:
            - If a skill IS warranted, output it inside <SKILL>...</SKILL> tags using the format below.
            - If no skill is warranted, output only the word: NO_SKILL

            SKILL FORMAT:
            <SKILL>
            ---
            name: kebab-case-slug
            description: "One sentence trigger. Start with a clear condition: 'Use when X' or 'Apply when Y'."
            ---

            # Title

            ## Purpose
            2-3 sentences: what this skill achieves and why it matters.

            ## When to Use
            - Specific trigger condition 1
            - Specific trigger condition 2
            - Specific trigger condition 3

            ## Workflow

            ### Step 1: ...
            ...

            ## References
            (omit section if none)
            </SKILL>
            """;

        try
        {
            var result = await chatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, system),
                    new ChatMessage(ChatRole.User, $"Review this session and decide:\n\n{digest}"),
                ],
                cancellationToken: ct);

            return result.Text?.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Skill curation LLM call failed.");
            return null;
        }
    }

    /// <summary>
    /// Writes the SKILL.md and returns <c>(path, isUpdate)</c>.
    /// Throws on I/O failure so the caller can record it in the curation log.
    /// </summary>
    private async Task<(string Path, bool IsUpdate)> WriteSkillAsync(
        string slug, string content, CancellationToken ct)
    {
        var libraryPath = string.IsNullOrWhiteSpace(config.LibraryPath)
            ? FuseraftPaths.GlobalSkills
            : config.LibraryPath;

        var skillDir  = Path.Combine(libraryPath, slug);
        var skillPath = Path.Combine(skillDir, "SKILL.md");

        Directory.CreateDirectory(skillDir);

        var isUpdate = File.Exists(skillPath);
        await File.WriteAllTextAsync(skillPath, content, ct);

        // Update the FTS5 index so future sessions can discover this skill by task description.
        try
        {
            var indexPath = string.IsNullOrWhiteSpace(config.IndexPath)
                ? FuseraftPaths.GlobalSkillsIndex
                : config.IndexPath;
            await using var index = new SkillIndex(indexPath);
            await index.IndexAsync(slug, skillPath, content, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Skill index update failed for '{Slug}' — skill was still written.", slug);
        }

        return (skillPath, isUpdate);
    }

    /// <summary>
    /// Appends one JSON line to the curation log. Best-effort — never throws.
    /// </summary>
    private async Task AppendCurationLogAsync(
        string sessionId,
        SkillCurationResult result,
        string source,
        CancellationToken ct)
    {
        try
        {
            var logPath = string.IsNullOrWhiteSpace(config.LogPath)
                ? FuseraftPaths.GlobalSkillCurationLog
                : config.LogPath;

            var entry = new CurationLogEntry(
                Ts:            DateTimeOffset.UtcNow.ToString("O"),
                Session:       sessionId,
                Source:        source,
                Outcome:       result.Outcome.ToString().ToLowerInvariant(),
                Slug:          result.Slug,
                Path:          result.Path,
                TurnsDigested: result.TurnsDigested > 0 ? result.TurnsDigested : null,
                Model:         result.Model,
                FailureReason: result.FailureReason);

            var line = JsonSerializer.Serialize(entry, LogJsonOpts) + "\n";

            var dir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.AppendAllTextAsync(logPath, line, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not append to curation log — non-fatal.");
        }
    }

    private sealed record CurationLogEntry(
        string  Ts,
        string  Session,
        string  Source,
        string  Outcome,
        string? Slug,
        string? Path,
        int?    TurnsDigested,
        string? Model,
        string? FailureReason);
}
