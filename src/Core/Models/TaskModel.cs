using System.Text;

namespace fuseraft.Core.Models;

/// <summary>
/// Structured representation of the user's goal for the current session.
///
/// <para>
/// Replaces the single-string <see cref="SessionCheckpoint.Task"/> with a typed schema
/// that selection strategies and routing validators can interrogate without parsing
/// free-form prose. The <see cref="ActiveTargets"/> list is the primary integration point:
/// it drives <see cref="fuseraft.Orchestration.ContextWindowFilter"/> semantic projection
/// and tells <c>LlmSelectionStrategy</c> which files are currently in scope.
/// </para>
/// </summary>
public record TaskModel
{
    /// <summary>The primary goal of the session in plain text.</summary>
    public string Goal { get; init; } = string.Empty;

    /// <summary>
    /// Quality or process constraints (e.g. "all tests must pass", "no new public API").
    /// Injected into selection-strategy prompts so the LLM knows the guardrails.
    /// </summary>
    public List<string> Constraints { get; init; } = [];

    /// <summary>
    /// File paths or logical targets currently active in the session
    /// (e.g. "src/Foo.cs", "tests/FooTests.cs"). Updated by the orchestrator as
    /// agents write files, so the list reflects the current working surface.
    /// Used by context projection to prioritise messages referencing these paths.
    /// </summary>
    public List<string> ActiveTargets { get; init; } = [];

    /// <summary>
    /// High-level phase label (e.g. "planning", "implementing", "testing", "reviewing").
    /// Null until the orchestrator or an agent explicitly sets it.
    /// </summary>
    public string? Phase { get; init; }

    /// <summary>
    /// Builds a minimal <see cref="TaskModel"/> from a raw task string.
    /// Goal is set to <paramref name="task"/>; all other fields are empty.
    /// </summary>
    public static TaskModel FromGoal(string task) => new() { Goal = task };

    /// <summary>
    /// Returns a concise block suitable for injection into agent system context.
    /// </summary>
    public string FormatForContext()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[TASK MODEL]");
        sb.AppendLine($"Goal: {Goal}");

        if (Constraints.Count > 0)
        {
            sb.AppendLine("Constraints:");
            foreach (var c in Constraints)
                sb.AppendLine($"  - {c}");
        }

        if (ActiveTargets.Count > 0)
        {
            sb.AppendLine("Active targets:");
            foreach (var t in ActiveTargets)
                sb.AppendLine($"  - {t}");
        }

        if (Phase is not null)
            sb.AppendLine($"Phase: {Phase}");

        return sb.ToString().TrimEnd();
    }
}
