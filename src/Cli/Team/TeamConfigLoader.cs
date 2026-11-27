using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Team;

namespace fuseraft.Cli.Team;

/// <summary>Reads a v2 team file (YAML or JSON) strictly: unknown fields are errors, never ignored.</summary>
public static class TeamConfigLoader
{
    internal static readonly JsonSerializerOptions StrictOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling      = JsonUnmappedMemberHandling.Disallow,
        NumberHandling              = JsonNumberHandling.AllowReadingFromString,
        AllowDuplicateProperties    = false,
        Converters                  = { new JsonStringEnumConverter(), new LenientStringConverter(), new ModelAliasConverter() },
    };

    /// <summary>Loads and compiles <paramref name="path"/> into the runtime orchestration config.</summary>
    public static OrchestrationConfig LoadAndCompile(string path) =>
        TeamCompiler.Compile(Load(path));

    /// <summary>Loads <paramref name="path"/> into a <see cref="TeamConfig"/>, reading the markdown agent files it names.</summary>
    public static TeamConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config file not found: {path}");
        return Load(ReadObject(path), path);
    }

    /// <summary>Loads an already-parsed team file; <paramref name="pending"/> supplies agent files (by full path) not yet on disk.</summary>
    internal static TeamConfig Load(JsonObject root, string path, IReadOnlyDictionary<string, string>? pending = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";

        RejectCaseDuplicates(root, "");

        if (Find(root, "Orchestration") is not null)
            throw new TeamConfigException(
                $"'{path}' uses the old 'Orchestration:' format, which is no longer supported.\n" +
                $"Convert it with: fuseraft migrate {path}");

        if (Find(root, "Team") is null)
            throw new TeamConfigException(
                $"'{path}' is missing the top-level 'team:' key. A team file starts with 'team: <name>'.");

        if (Find(root, "Agents") is { Value: JsonObject agents })
            foreach (var (name, node) in agents.ToList())
            {
                if (node is JsonObject agent && Find(agent, "AgentFile") is not null)
                    throw new TeamConfigException(
                        $"agents.{name}.agentFile: agent files are now markdown, referenced as '{name}: agents/{name.ToLowerInvariant()}.md'.\n" +
                        $"Convert this team with: fuseraft migrate {path}");
                if (node is JsonValue value && value.TryGetValue<string>(out var file))
                    agents[name] = ReadAgentMarkdown(Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(dir, file)), name, pending);
            }

        return Deserialize<TeamConfig>(root, path);
    }

    /// <summary>Deserializes <paramref name="node"/> strictly, turning binding failures into readable errors.</summary>
    internal static T Deserialize<T>(JsonNode node, string where)
    {
        try
        {
            return node.Deserialize<T>(StrictOptions)
                ?? throw new TeamConfigException($"{where}: expected a map.");
        }
        catch (JsonException ex)
        {
            throw new TeamConfigException($"{where}: {Explain(ex)}", ex);
        }
    }

    /// <summary>Deserializes <paramref name="element"/> strictly, turning binding failures into readable errors.</summary>
    internal static T Deserialize<T>(JsonElement element, string where)
    {
        try
        {
            return element.Deserialize<T>(StrictOptions)
                ?? throw new TeamConfigException($"{where}: expected a map.");
        }
        catch (JsonException ex)
        {
            throw new TeamConfigException($"{where}: {Explain(ex)}", ex);
        }
    }

    private static string Explain(JsonException ex)
    {
        var at = string.IsNullOrEmpty(ex.Path) || ex.Path == "$" ? "" : $" at {ex.Path.TrimStart('$', '.')}";
        if (ex.Message.Contains("could not be mapped", StringComparison.Ordinal))
        {
            var start = ex.Message.IndexOf('\'');
            var end   = start >= 0 ? ex.Message.IndexOf('\'', start + 1) : -1;
            var field = start >= 0 && end > start ? ex.Message[(start + 1)..end] : "?";
            return $"unknown field '{field}'{at}. Check the spelling, or see docs/configuration.md for the fields this section accepts.";
        }
        var firstLine = ex.Message.Split('.', 2)[0];
        return $"{firstLine}{at}.";
    }

    internal static JsonObject ReadObject(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { throw new TeamConfigException($"Cannot read '{path}': {ex.Message}", ex); }
        return ParseObject(text, path);
    }

    /// <summary>Parses <paramref name="text"/> as YAML or JSON, by <paramref name="path"/>'s extension.</summary>
    internal static JsonObject ParseObject(string text, string path)
    {
        string json;
        try { json = YamlConfigLoader.IsYamlPath(path) ? YamlConfigLoader.ConvertYamlToJson(text) : text; }
        catch (Exception ex) { throw new TeamConfigException($"'{path}' has invalid YAML: {ex.Message}", ex); }

        try
        {
            return JsonNode.Parse(json) as JsonObject
                ?? throw new TeamConfigException($"'{path}' must be a map at the top level.");
        }
        catch (JsonException ex) { throw new TeamConfigException($"'{path}' is not valid JSON: {ex.Message}", ex); }
    }

    /// <summary>Reads a markdown agent file: the frontmatter holds the agent's fields, the body is its instructions.</summary>
    private static JsonObject ReadAgentMarkdown(string file, string agentName, IReadOnlyDictionary<string, string>? pending)
    {
        var at = $"agents.{agentName} ({file})";
        if (!file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            throw new TeamConfigException($"agents.{agentName}: '{file}' isn't a markdown file. Write the agent inline, or point to a .md file.");

        string? text = null;
        if (pending?.TryGetValue(file, out var staged) != true)
        {
            if (!File.Exists(file))
                throw new TeamConfigException($"agents.{agentName}: '{file}' not found.");
            try { text = File.ReadAllText(file); }
            catch (Exception ex) { throw new TeamConfigException($"Cannot read '{file}': {ex.Message}", ex); }
        }
        else text = staged!;

        if (!Core.Subagents.SubagentDefinitionLoader.TrySplitFrontmatter(text, out var yaml, out var body))
            throw new TeamConfigException($"{at}: the file must start with a '---' frontmatter block holding the agent's fields.");

        JsonObject fields;
        try
        {
            fields = yaml.Trim().Length == 0
                ? []
                : JsonNode.Parse(YamlConfigLoader.ConvertYamlToJson(yaml)) as JsonObject
                  ?? throw new TeamConfigException($"{at}: the frontmatter must be a map of fields.");
        }
        catch (Exception ex) when (ex is not TeamConfigException)
        {
            throw new TeamConfigException($"{at}: invalid frontmatter YAML: {ex.Message}", ex);
        }

        RejectCaseDuplicates(fields, at);
        if (Find(fields, "Instructions") is not null)
            throw new TeamConfigException($"{at}: the markdown body is the instructions; remove 'instructions' from the frontmatter.");
        if (Find(fields, "AgentFile") is not null)
            throw new TeamConfigException($"{at}: an agent file can't name another agent file; remove 'agentFile'.");

        fields["instructions"] = body.Trim();
        Deserialize<TeamAgentConfig>(fields, at);
        return fields;
    }

    // Keys match case-insensitively, so maxTurns and MaxTurns are the same field.
    internal static KeyValuePair<string, JsonNode?>? Find(JsonObject obj, string key)
    {
        foreach (var pair in obj)
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                return pair;
        return null;
    }

    // Maps whose direct children are names the user chose, and maps whose whole subtree is user data.
    private static readonly HashSet<string> NamedChildren = new(StringComparer.OrdinalIgnoreCase)
        { "agents", "models", "steps", "artifacts", "apiProfiles" };
    internal static readonly HashSet<string> Opaque = new(StringComparer.OrdinalIgnoreCase)
        { "shape", "env", "headers", "defaultHeaders", "capabilities", "toolResultCharOverrides" };

    // Fields match case-insensitively, so the same field written twice in different cases would
    // otherwise let one silently win (or crash a case-insensitive lookup). User-named maps are skipped.
    private static void RejectCaseDuplicates(JsonNode? node, string where, bool namedChildren = false)
    {
        switch (node)
        {
            case JsonObject obj:
                var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in obj)
                {
                    if (!namedChildren && !seen.TryAdd(key, key))
                        throw new TeamConfigException(
                            $"{(where.Length > 0 ? where + ": " : "")}'{seen[key]}' and '{key}' are the same field written twice; keep one.");
                    if (!Opaque.Contains(key))
                        RejectCaseDuplicates(value, where.Length > 0 ? $"{where}.{key}" : key, !namedChildren && NamedChildren.Contains(key));
                }
                break;
            case JsonArray arr:
                var i = 0;
                foreach (var item in arr)
                    RejectCaseDuplicates(item, $"{where}[{i++}]");
                break;
        }
    }

    /// <summary>Lets any model field be written as an alias string as well as a full model map.</summary>
    private sealed class ModelAliasConverter : JsonConverter<ModelConfig>
    {
        public override ModelConfig? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
                return new ModelConfig { ModelId = reader.GetString()! };

            var inner = new JsonSerializerOptions(options);
            inner.Converters.Remove(inner.Converters.First(c => c is ModelAliasConverter));
            return JsonSerializer.Deserialize<ModelConfig>(ref reader, inner);
        }

        public override void Write(Utf8JsonWriter writer, ModelConfig value, JsonSerializerOptions options) =>
            throw new NotSupportedException();
    }

    /// <summary>Accepts YAML scalars that parsed as numbers or bools where a string is expected.</summary>
    private sealed class LenientStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.ValueSpan),
                JsonTokenType.True   => "true",
                JsonTokenType.False  => "false",
                JsonTokenType.Null   => null,
                _ => throw new JsonException($"Expected a string, found {reader.TokenType}."),
            };

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }
}
