using System.Text;
using System.Text.Json;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Team;

namespace fuseraft.Cli.Team;

/// <summary>Builds the instructions an agent runs with: its own text plus a section generated from the team file.</summary>
internal static class TeamPrompt
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Appends the artifacts this agent touches and its allowed handoffs to its instructions.</summary>
    public static string Build(
        TeamAgentConfig agent,
        TeamConfig team,
        IReadOnlyList<(HandoffRoute Route, List<string> Gates)>? routes)
    {
        var artifacts = agent.Writes.Select(a => (Name: a, Writes: true))
            .Concat(agent.Reads.Where(a => !agent.Writes.Contains(a)).Select(a => (Name: a, Writes: false)))
            .ToList();

        if (artifacts.Count == 0 && routes is not { Count: > 0 })
            return agent.Instructions;

        var sb = new StringBuilder(agent.Instructions.TrimEnd());
        sb.Append("\n\n## Team protocol\n");

        if (artifacts.Count > 0)
        {
            sb.Append("\n### Artifacts\n");
            foreach (var (name, writes) in artifacts)
            {
                var art = team.Artifacts[name];
                sb.Append($"- `{name}` at `{art.Path}`: ");
                sb.Append(writes ? "you write this with write_file" : "you read this; it is included below when it exists");
                if (writes && art.Shape is { } shape)
                    sb.Append(" as JSON with this shape (`A|B` means one of those strings, `[T]` a list, `?` optional):\n")
                      .Append("```json\n").Append(JsonSerializer.Serialize(shape, Indented)).Append("\n```\n");
                else
                    sb.Append(".\n");
            }
        }

        if (routes is { Count: > 0 })
        {
            sb.Append("\n### Handing off\n")
              .Append(agent.RemoteAgent is null
                  ? "When your work is done, make `handoff` your last tool call, with `route_keyword` set to one of:\n"
                  : "When your work is done, end your reply with one of these route names on its own line:\n");
            foreach (var (route, gates) in routes)
            {
                sb.Append($"- `{route.Name}`: {route.Description}");
                if (gates.Count > 0)
                    sb.Append(" fuseraft checks first that ").Append(string.Join("; ", gates)).Append('.');
                sb.Append('\n');
            }
            sb.Append("If a check fails, you'll get a message naming what's missing; fix it and hand off again.");
            sb.Append(agent.RemoteAgent is null
                ? " Fill in `goal`, `background` and `constraints` so the next agent can work without this conversation.\n"
                : "\n");
        }

        return sb.ToString();
    }
}
