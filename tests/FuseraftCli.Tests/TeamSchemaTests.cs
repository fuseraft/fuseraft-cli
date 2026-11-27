using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using fuseraft.Cli;
using fuseraft.Cli.Commands;
using fuseraft.Cli.Team;

namespace FuseraftCli.Tests;

public sealed class TeamSchemaTests
{
    private static readonly JsonObject Schema = TeamSchema.Build();

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "config", "examples")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException("repo root");
    }

    public static TheoryData<string> Examples()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(Path.Combine(RepoRoot(), "config", "examples"), "*.yaml").Order())
            data.Add(Path.GetFileName(f));
        return data;
    }

    public static TheoryData<string> Templates()
    {
        var data = new TheoryData<string>();
        foreach (var t in InitTemplatesReadingTests.Templates()) data.Add(t);
        return data;
    }

    private static JsonNode Yaml(string yaml) => JsonNode.Parse(YamlConfigLoader.ConvertYamlToJson(yaml))!;

    [Theory]
    [MemberData(nameof(Examples))]
    public void Example_MatchesSchema(string file) =>
        Assert.Empty(SchemaCheck.Errors(Schema, Yaml(File.ReadAllText(Path.Combine(RepoRoot(), "config", "examples", file)))));

    [Theory]
    [MemberData(nameof(Templates))]
    public void Template_MatchesSchema(string template) =>
        Assert.Empty(SchemaCheck.Errors(Schema, Yaml(InitTemplates.Build(template, "gpt-4o", "https://example.com/v1").MainConfig)));

    [Fact]
    public void CheckedInSchema_IsCurrent() =>
        Assert.Equal(TeamSchema.Render(), File.ReadAllText(Path.Combine(RepoRoot(), "schema", "team.schema.json")).ReplaceLineEndings("\n"));

    [Fact]
    public void MarkdownAgentPath_MatchesSchema() =>
        Assert.Empty(SchemaCheck.Errors(Schema, Yaml("team: T\nagents:\n  W: agents/w.md\nflow: { start: W, steps: { W: [{ to: end }] } }")));

    [Theory]
    [InlineData("team: T\nagents:\n  W: { instructions: x, colour: blue }\nflow: { start: W, steps: { W: [{ to: end }] } }", "colour")]
    [InlineData("team: T\nagents:\n  W: agents/w.yaml\nflow: { start: W, steps: { W: [{ to: end }] } }", "agents/W")]
    [InlineData("team: T\nagents:\n  W: { instructions: x }\nflow: { start: W, steps: { W: [{ to: end, gate: x }] } }", "gate")]
    [InlineData("team: T\nagents:\n  W: { instructions: x }\nflow: { pattern: magentic, rounds: 3 }", "flow")]
    [InlineData("team: T\nagents:\n  W: { instructions: x, advanced: { plugins: [Shell] } }\nflow: { start: W, steps: { W: [{ to: end }] } }", "plugins")]
    public void InvalidTeam_FailsSchema(string yaml, string where) =>
        Assert.Contains(SchemaCheck.Errors(Schema, Yaml(yaml)), e => e.Contains(where));

    [Fact]
    public void EveryTopLevelTeamField_IsInSchema()
    {
        var props = (JsonObject)Schema["properties"]!;
        foreach (var p in typeof(fuseraft.Core.Models.Team.TeamConfig).GetProperties())
            Assert.True(props.ContainsKey(JsonNamingPolicy.CamelCase.ConvertName(p.Name)), p.Name);
    }
}

/// <summary>Just enough JSON Schema to check the keywords <see cref="TeamSchema"/> emits.</summary>
internal static class SchemaCheck
{
    public static List<string> Errors(JsonObject root, JsonNode? value)
    {
        var errors = new List<string>();
        Check(root, root, value, "", errors);
        return errors;
    }

    private static void Check(JsonObject root, JsonNode? schemaNode, JsonNode? value, string at, List<string> errors)
    {
        if (schemaNode is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            if (b.GetValueKind() == JsonValueKind.False) errors.Add($"{at}: not allowed");
            return;
        }
        if (schemaNode is not JsonObject schema) return;

        if (schema["$ref"]?.GetValue<string>() is { } pointer)
        {
            var target = pointer.TrimStart('#', '/').Split('/').Aggregate((JsonNode?)root, (n, part) => n?[part]);
            Check(root, target ?? throw new InvalidOperationException($"unresolved {pointer}"), value, at, errors);
            return;
        }

        if (schema["anyOf"] is JsonArray anyOf)
        {
            var branches = anyOf.Select(s => { var e = new List<string>(); Check(root, s, value, at, e); return e; }).ToList();
            if (branches.All(e => e.Count > 0))
                errors.Add($"{at}: matches none of anyOf ({string.Join(" | ", branches.Select(e => e[0]))})");
        }

        if (schema["type"] is { } type)
        {
            var types = type is JsonArray arr ? arr.Select(t => t!.GetValue<string>()).ToList() : [type.GetValue<string>()];
            if (!types.Any(t => IsType(t, value)))
            {
                errors.Add($"{at}: expected {string.Join("|", types)}");
                return;
            }
        }

        if (schema["const"] is { } c && !JsonNode.DeepEquals(c, value)) errors.Add($"{at}: expected {c.ToJsonString()}");
        if (schema["enum"] is JsonArray en && !en.Any(e => JsonNode.DeepEquals(e, value) || IsCaseInsensitiveMatch(e, value)))
            errors.Add($"{at}: {value?.ToJsonString()} isn't one of {en.ToJsonString()}");
        if (schema["pattern"]?.GetValue<string>() is { } pattern && value is JsonValue sv && sv.GetValueKind() == JsonValueKind.String
            && !Regex.IsMatch(sv.GetValue<string>(), pattern))
            errors.Add($"{at}: doesn't match {pattern}");

        if (value is JsonObject obj)
        {
            var props = schema["properties"] as JsonObject;
            if (schema["required"] is JsonArray req)
                foreach (var r in req)
                    if (!obj.ContainsKey(r!.GetValue<string>())) errors.Add($"{at}: missing {r}");
            foreach (var (key, v) in obj)
            {
                if (props?[key] is { } ps) Check(root, ps, v, $"{at}/{key}", errors);
                else if (schema["additionalProperties"] is { } ap) Check(root, ap, v, $"{at}/{key}", errors);
            }
        }
        if (value is JsonArray items && schema["items"] is { } itemSchema)
            for (var i = 0; i < items.Count; i++) Check(root, itemSchema, items[i], $"{at}[{i}]", errors);
    }

    // The loader reads enums case-insensitively.
    private static bool IsCaseInsensitiveMatch(JsonNode? e, JsonNode? value) =>
        e is JsonValue ev && value is JsonValue vv && ev.GetValueKind() == JsonValueKind.String && vv.GetValueKind() == JsonValueKind.String
        && string.Equals(ev.GetValue<string>(), vv.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

    private static bool IsType(string type, JsonNode? value) => type switch
    {
        "null"    => value is null,
        "object"  => value is JsonObject,
        "array"   => value is JsonArray,
        "string"  => value is JsonValue v && v.GetValueKind() == JsonValueKind.String,
        "boolean" => value is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number"  => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number,
        "integer" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number && decimal.TryParse(v.ToJsonString(), out var d) && d == Math.Truncate(d),
        _ => throw new InvalidOperationException($"unknown type {type}"),
    };
}
