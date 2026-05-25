namespace fuseraft.Core.Models;

/// <summary>
/// Configures the post-session skill curator that reviews completed sessions and
/// writes reusable procedural knowledge to the skills library as SKILL.md files.
/// </summary>
public record SkillCurationConfig
{
    /// <summary>
    /// Whether to run skill curation after a successful session.
    /// Default: false.
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Directory where skill subdirectories are written.
    /// Defaults to <c>~/.fuseraft/skills</c> when null or empty.
    /// </summary>
    public string? LibraryPath { get; init; }

    /// <summary>
    /// Model alias or ID used for the curation LLM call.
    /// Resolved against the session's <c>Models</c> registry first, then by provider prefix.
    /// Defaults to the first agent's model when null or empty.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Minimum number of completed assistant turns before curation runs.
    /// Prevents trivial one-shot sessions from generating skills.
    /// Default: 5.
    /// </summary>
    public int MinTurns { get; init; } = 5;

    /// <summary>
    /// Maximum number of assistant-turn text messages included in the session digest
    /// sent to the curation LLM. Only text content is included; tool frames are excluded.
    /// Default: 30.
    /// </summary>
    public int DigestTurns { get; init; } = 30;

    /// <summary>
    /// Path to the SQLite FTS5 skill index database.
    /// Defaults to <c>~/.fuseraft/skills/index.db</c> when null or empty.
    /// </summary>
    public string? IndexPath { get; init; }

    /// <summary>
    /// Maximum number of skills injected from the index into the session context.
    /// Only the top-N most relevant skills (ranked by FTS5 match score) are included.
    /// Default: 5.
    /// </summary>
    public int IndexTopN { get; init; } = 5;

    /// <summary>
    /// Path to the append-only JSONL curation log written after every curation attempt.
    /// Each line records the outcome, slug, model, turn count, and any error.
    /// Defaults to <c>~/.fuseraft/skill-curation.jsonl</c> when null or empty.
    /// </summary>
    public string? LogPath { get; init; }
}
