using System.Text.Json;
using System.Text.Json.Nodes;

namespace fuseraft.Cli.Team;

/// <summary>Rewrites a team file's <c>agentFile</c> agents as markdown agent files referenced by path.</summary>
internal static class AgentFileMigrator
{
    /// <summary>The rewritten team file, the markdown files to write next to it, and notes for the user.</summary>
    public sealed record Result(string TeamText, IReadOnlyList<(string Path, string Content)> Files, IReadOnlyList<string> Notes);

    /// <summary>True when <paramref name="root"/> is a team file with at least one <c>agentFile</c> agent.</summary>
    public static bool Applies(JsonObject root) =>
        TeamConfigLoader.Find(root, "Team") is not null
        && TeamConfigLoader.Find(root, "Agents") is { Value: JsonObject agents }
        && agents.Any(a => a.Value is JsonObject agent && TeamConfigLoader.Find(agent, "AgentFile") is not null);

    /// <summary>Converts the team file at <paramref name="teamPath"/>; nothing is written.</summary>
    public static Result Convert(string teamPath, string? header = null)
    {
        var root   = TeamConfigLoader.ReadObject(teamPath);
        var dir    = Path.GetDirectoryName(Path.GetFullPath(teamPath)) ?? ".";
        var agents = (JsonObject)TeamConfigLoader.Find(root, "Agents")!.Value.Value!;
        var notes  = new List<string>();

        var merged = new List<(string Name, string Source, JsonObject Fields)>();
        foreach (var (name, node) in agents.ToList())
        {
            if (node is not JsonObject inline || TeamConfigLoader.Find(inline, "AgentFile") is not { Value: var fileNode }) continue;
            var file   = fileNode?.GetValue<string>() ?? "";
            var source = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(dir, file));
            if (!File.Exists(source))
                throw new TeamConfigException($"agents.{name}.agentFile: '{file}' not found.");

            var fields = TeamConfigLoader.ReadObject(source);
            if (TeamConfigLoader.Find(fields, "Agent") is { Value: JsonObject wrapped }) fields = wrapped;
            fields = (JsonObject)fields.DeepClone();
            foreach (var (key, value) in inline)
            {
                if (key.Equals("AgentFile", StringComparison.OrdinalIgnoreCase)) continue;
                if (TeamConfigLoader.Find(fields, key) is { Key: var existing })
                    fields.SetAt(fields.IndexOf(existing), key, value?.DeepClone());
                else
                    fields[key] = value?.DeepClone();
            }
            merged.Add((name, source, CamelCase(fields)));
        }

        var files = new List<(string Path, string Content)>();
        foreach (var group in merged.GroupBy(m => m.Source))
        {
            var contents = group.Select(m => (m.Name, Content: Markdown(m.Fields))).ToList();
            var shared   = contents.Select(c => c.Content).Distinct().Count() == 1;
            foreach (var (name, content) in contents)
            {
                var target = shared
                    ? Path.ChangeExtension(group.Key, ".md")
                    : Path.Combine(Path.GetDirectoryName(group.Key)!, $"{Path.GetFileNameWithoutExtension(group.Key)}-{name.ToLowerInvariant()}.md");

                if (File.Exists(target) && File.ReadAllText(target) != content)
                    throw new TeamConfigException($"'{target}' already exists and holds something else; move it before migrating.");
                if (!files.Any(f => f.Path == target)) files.Add((target, content));
                agents[name] = Path.GetRelativePath(dir, target).Replace('\\', '/');
            }
            notes.Add($"{Path.GetRelativePath(dir, group.Key)} is no longer read; delete it once no other team uses it.");
        }

        var teamText = YamlConfigLoader.IsYamlPath(teamPath)
            ? (string.IsNullOrWhiteSpace(header) ? "" : header.TrimEnd() + "\n\n") + YamlWriter.Write(root)
            : root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        return new Result(teamText, files, notes);
    }

    private static string Markdown(JsonObject fields)
    {
        var copy = (JsonObject)fields.DeepClone();
        var instructions = "";
        if (TeamConfigLoader.Find(copy, "Instructions") is { Key: var key, Value: var value })
        {
            instructions = value?.GetValue<string>() ?? "";
            copy.Remove(key);
        }
        return "---\n" + (copy.Count > 0 ? YamlWriter.Write(copy, spaced: false) : "") + "---\n" + instructions.Trim() + "\n";
    }

    // Older agent files used PascalCase; everything we write is camelCase. User data (env, headers, ...) keeps its keys.
    private static JsonObject CamelCase(JsonObject obj)
    {
        var result = new JsonObject();
        foreach (var (key, value) in obj)
        {
            var name = key.Length > 0 ? char.ToLowerInvariant(key[0]) + key[1..] : key;
            result[name] = TeamConfigLoader.Opaque.Contains(key) ? value?.DeepClone() : CamelCaseNode(value);
        }
        return result;
    }

    private static JsonNode? CamelCaseNode(JsonNode? node) => node switch
    {
        JsonObject o => CamelCase(o),
        JsonArray a  => new JsonArray([.. a.Select(CamelCaseNode)]),
        _            => node?.DeepClone(),
    };
}
