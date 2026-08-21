using System.Text;

namespace fuseraft.Core.Models.Agents;

/// <summary>
/// Synthesized handoff payload passed to the next agent at a routing transition, replacing
/// the historical pattern of injecting a bare <c>[fuseraft: A → B]</c> marker (or a heuristic
/// excerpt of A's raw response) into the shared transcript. Populated from the optional
/// <c>goal</c>/<c>background</c>/<c>constraints</c> arguments on <c>handoff()</c>
/// (<see cref="fuseraft.Infrastructure.Plugins.HandoffPlugin"/>).
/// </summary>
public sealed record AgentDirective
{
    /// <summary>What the receiving agent must accomplish this turn.</summary>
    public required string Goal { get; init; }

    /// <summary>What the handing-off agent already learned, tried, or ruled out.</summary>
    public string? Background { get; init; }

    /// <summary>Explicit constraints the receiving agent must respect.</summary>
    public IReadOnlyList<string> Constraints { get; init; } = [];

    /// <summary>Renders this directive as a single user-facing message body.</summary>
    public string Format()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Goal);

        if (!string.IsNullOrWhiteSpace(Background))
        {
            sb.AppendLine();
            sb.AppendLine("Background:");
            sb.AppendLine(Background);
        }

        if (Constraints.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Constraints:");
            foreach (var c in Constraints)
                sb.AppendLine($"- {c}");
        }

        return sb.ToString().TrimEnd();
    }
}
