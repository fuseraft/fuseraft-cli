using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using fuseraft.Core.Models.Team;

namespace fuseraft.Cli.Team;

/// <summary>Builds the JSON Schema for team files from the records the loader binds them to.</summary>
public static class TeamSchema
{
    public const string Id = "https://raw.githubusercontent.com/fuseraft/fuseraft-cli/main/schema/team.schema.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerOptions.Default)
    {
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters             = { new JsonStringEnumConverter() },
    };

    private const string ModelMarker = "x-fuseraft-model";

    // Marks model nodes instead of rewriting them here, so the exporter's own $ref paths stay valid until inlined.
    private static readonly JsonSchemaExporterOptions Exporter = new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = (ctx, node) =>
        {
            if (ctx.TypeInfo.Type == typeof(ModelConfig) && node is JsonObject obj) obj[ModelMarker] = true;
            return node;
        },
    };

    /// <summary>The schema, as indented JSON.</summary>
    public static string Render() => Build().ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

    /// <summary>The schema for a whole team file.</summary>
    public static JsonObject Build()
    {
        var root  = Export<TeamConfig>();
        var props = (JsonObject)root["properties"]!;
        var defs  = new JsonObject
        {
            ["model"] = Model(),
            ["agent"] = Agent(),
            ["edge"]  = Edge(),
        };

        props["models"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Named model aliases that agents and patterns refer to by name.",
            ["additionalProperties"] = Ref("model"),
        };
        props["agents"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Agents keyed by name: an inline map, or the path of a markdown agent file.",
            ["additionalProperties"] = new JsonObject
            {
                ["anyOf"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "string",
                        ["pattern"] = @"\.md$",
                        ["description"] = "A markdown file, relative to the team file: frontmatter holds the agent's fields, the body is its instructions.",
                    },
                    Ref("agent")),
            },
        };
        props["flow"]     = Flow();
        props["advanced"] = Subset(Export<OrchestrationConfig>(), TeamCompiler.AdvancedTopLevel,
            "Opt-in layers (compaction, verifier, telemetry, ...).");

        var schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = Id,
            ["title"] = "fuseraft team",
            ["description"] = "A fuseraft team file. See docs/configuration.md.",
        };
        foreach (var (key, value) in root) schema[key] = value?.DeepClone();
        schema["required"] = new JsonArray("team", "agents", "flow");
        schema["$defs"] = defs;
        return schema;
    }

    private static JsonObject Agent()
    {
        var agent = Export<TeamAgentConfig>();
        var props = (JsonObject)agent["properties"]!;
        props["model"]    = Ref("model");
        props["sees"]     = new JsonObject { ["enum"] = new JsonArray("handoff", "conversation", "fork") };
        props["advanced"] = Subset(Export<AgentConfig>(), TeamCompiler.AdvancedAgent, "Per-agent knobs.");
        return agent;
    }

    // Any model field takes an alias string as well as a full model map.
    private static JsonObject Model()
    {
        var model = (JsonObject)Inline(Options.GetJsonSchemaAsNode(typeof(ModelConfig), Exporter));
        model.Remove(ModelMarker);
        return new() { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "string" }, ShareModels(model)) };
    }

    private static JsonObject Flow()
    {
        var steps = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "A state machine: each step is named after its agent and lists that agent's outgoing edges.",
            ["properties"] = new JsonObject
            {
                ["start"] = new JsonObject { ["type"] = "string", ["description"] = "The agent that runs first." },
                ["steps"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = new JsonObject { ["type"] = "array", ["items"] = Ref("edge") },
                },
            },
            ["required"] = new JsonArray("start", "steps"),
            ["additionalProperties"] = false,
        };

        var flows = new JsonArray(steps);
        foreach (var (name, schema) in new (string, JsonObject)[]
        {
            ("magentic",      Export<MagenticManagerConfig>()),
            ("adversarial",   Export<AdversarialConfig>()),
            ("mapreduce",     Export<MapReduceConfig>()),
            ("scattergather", Export<ScatterGatherConfig>()),
        })
        {
            ((JsonObject)schema["properties"]!)["pattern"] = new JsonObject { ["const"] = name };
            schema["required"] = new JsonArray("pattern");
            flows.Add(schema);
        }
        return new JsonObject { ["description"] = "Either start + steps, or a pattern with its fields.", ["anyOf"] = flows };
    }

    private static JsonObject Edge()
    {
        var name = new JsonObject { ["type"] = "string" };
        var gate = new JsonObject
        {
            ["anyOf"] = new JsonArray(
                new JsonObject { ["type"] = "string", ["description"] = "A gate check name or an artifact name." },
                new JsonObject { ["type"] = "object", ["description"] = "A gate check with its argument and options." }),
        };
        var props = new JsonObject
        {
            ["to"] = new JsonObject
            {
                ["description"] = "An agent, a list of agents, or end.",
                ["anyOf"] = new JsonArray(name.DeepClone(), new JsonObject { ["type"] = "array", ["items"] = name.DeepClone(), ["minItems"] = 1 }),
            },
            ["name"]        = new JsonObject { ["type"] = "string", ["description"] = "The route name the agent hands off with." },
            ["when"]        = new JsonObject { ["type"] = "string", ["description"] = "When the agent should take this edge." },
            ["requires"]    = new JsonObject
            {
                ["description"] = "Gates that must pass before the handoff takes effect.",
                ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "array", ["items"] = gate.DeepClone() }, gate.DeepClone(), new JsonObject { ["type"] = "null" }),
            },
            ["onStuck"]     = new JsonObject { ["type"] = "string", ["description"] = "An agent to hand to, or abort, when the gates keep failing." },
            ["join"]        = new JsonObject { ["type"] = "string" },
            ["merge"]       = Export<MergeConfig>(),
            ["maxRevisits"] = new JsonObject { ["type"] = "integer" },
            ["approve"]     = new JsonObject { ["type"] = "boolean", ["description"] = "Ask a person before the handoff takes effect." },
        };
        foreach (var field in TeamCompiler.EdgeFields)
            if (props[field] is null) throw new InvalidOperationException($"TeamSchema: edge field '{field}' has no schema.");

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray("to"),
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject Subset(JsonObject schema, IReadOnlySet<string> fields, string description)
    {
        var props = (JsonObject)schema["properties"]!;
        foreach (var key in props.Select(p => p.Key).ToList())
            if (!fields.Contains(key)) props.Remove(key);
        foreach (var field in fields)
            if (!props.Any(p => p.Key.Equals(field, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"TeamSchema: '{field}' isn't a field of the record it's taken from.");
        schema["description"] = description;
        schema.Remove("required");
        return schema;
    }

    private static JsonObject Ref(string def) => new() { ["$ref"] = $"#/$defs/{def}" };

    private static JsonObject Export<T>() =>
        (JsonObject)ShareModels(Inline(Options.GetJsonSchemaAsNode(typeof(T), Exporter)));

    // The exporter points repeated types at their first occurrence by path; those paths break once subtrees move.
    private static JsonNode Inline(JsonNode root)
    {
        JsonNode Walk(JsonNode node, string path)
        {
            switch (node)
            {
                case JsonObject obj when obj["$ref"]?.GetValue<string>() is { } pointer && pointer.StartsWith('#'):
                    var target = pointer.Length == 1 ? root : Resolve(root, pointer[2..]);
                    if (target is JsonObject t && t[ModelMarker] is not null) return Ref("model");
                    var targetPath = pointer[1..];
                    return path.StartsWith(targetPath + "/", StringComparison.Ordinal) || target is null
                        ? new JsonObject()
                        : Walk(target.DeepClone(), path);
                case JsonObject obj:
                    var copy = new JsonObject();
                    foreach (var (key, value) in obj) copy[key] = value is null ? null : Walk(value, $"{path}/{key}");
                    return copy;
                case JsonArray arr:
                    return new JsonArray([.. arr.Select((v, i) => v is null ? null : Walk(v, $"{path}/{i}"))]);
                default:
                    return node.DeepClone();
            }
        }
        return Walk(root, "");
    }

    private static JsonNode? Resolve(JsonNode root, string pointer)
    {
        JsonNode? node = root;
        foreach (var raw in pointer.Split('/'))
        {
            var part = raw.Replace("~1", "/").Replace("~0", "~");
            node = node switch
            {
                JsonObject o => o[part],
                JsonArray a when int.TryParse(part, out var i) && i < a.Count => a[i],
                _ => null,
            };
            if (node is null) return null;
        }
        return node;
    }

    // Every nested model field refers to the one shared model definition.
    private static JsonNode ShareModels(JsonNode node) => node switch
    {
        JsonObject obj when obj[ModelMarker] is not null => Ref("model"),
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, p.Value is null ? null : ShareModels(p.Value)))),
        JsonArray arr  => new JsonArray([.. arr.Select(v => v is null ? null : ShareModels(v))]),
        _ => node.DeepClone(),
    };
}
