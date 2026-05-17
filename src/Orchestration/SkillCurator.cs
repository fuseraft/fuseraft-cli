using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration;

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
/// </summary>
public sealed class SkillCurator(
    IChatClient chatClient,
    SkillCurationConfig config,
    EvidenceStore? evidenceStore,
    ILogger<SkillCurator> logger)
{
    private static readonly Regex SkillBlock =
        new(@"<SKILL>(.*?)</SKILL>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex NameFrontmatter =
        new(@"^name:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>
    /// Evaluates the session and writes a SKILL.md to the library when one is warranted.
    /// Returns <c>(true, slug, path)</c> when a skill was written; <c>(false, null, null)</c> otherwise.
    /// Never throws — curation is best-effort and must not fail the run.
    /// </summary>
    public async Task<(bool Created, string? Slug, string? Path)> RunAsync(
        SessionCheckpoint checkpoint,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken ct)
    {
        var assistantTurns = messages.Count(m => m.Role == "assistant");
        if (assistantTurns < config.MinTurns)
        {
            logger.LogDebug(
                "Skill curation skipped — {Turns} assistant turns (min {Min}).",
                assistantTurns, config.MinTurns);
            return (false, null, null);
        }

        var digest   = await BuildDigestAsync(checkpoint, messages, ct);
        var response = await EvaluateAsync(digest, ct);

        if (string.IsNullOrWhiteSpace(response))
            return (false, null, null);

        var match = SkillBlock.Match(response);
        if (!match.Success)
            return (false, null, null);

        var skillContent = match.Groups[1].Value.Trim();
        var nameMatch    = NameFrontmatter.Match(skillContent);
        if (!nameMatch.Success)
        {
            logger.LogWarning("Skill curation: response missing 'name' in frontmatter — skipping.");
            return (false, null, null);
        }

        var name = nameMatch.Groups[1].Value.Trim().Trim('"').Trim('\'');
        var slug = ToSlug(name);
        var path = await WriteSkillAsync(slug, skillContent, ct);
        return (true, slug, path);
    }

    // Internals

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
            .Where(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content))
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

    private async Task<string> WriteSkillAsync(string slug, string content, CancellationToken ct)
    {
        var libraryPath = string.IsNullOrWhiteSpace(config.LibraryPath)
            ? FuseraftPaths.GlobalSkills
            : config.LibraryPath;

        var skillDir  = Path.Combine(libraryPath, slug);
        var skillPath = Path.Combine(skillDir, "SKILL.md");

        Directory.CreateDirectory(skillDir);

        var isUpdate = File.Exists(skillPath);
        await File.WriteAllTextAsync(skillPath, content, ct);

        logger.LogInformation(
            "Skill {Verb}: {Slug} → {Path}",
            isUpdate ? "updated" : "created", slug, skillPath);

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
            logger.LogWarning(ex, "Skill index update failed for '{Slug}' — skill was still written.", slug);
        }

        return skillPath;
    }

    private static string ToSlug(string name) =>
        Regex.Replace(name.ToLowerInvariant().Trim(), @"[^a-z0-9]+", "-").Trim('-');
}
