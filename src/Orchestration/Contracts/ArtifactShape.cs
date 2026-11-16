using System.Text.Json;

namespace fuseraft.Orchestration.Contracts;

/// <summary>Checks a JSON value against a compact shape sketch (<c>string</c>, <c>int</c>, <c>A|B</c>, <c>[T]</c>, nested maps).</summary>
public static class ArtifactShape
{
    private static readonly HashSet<string> Scalars =
        new(StringComparer.OrdinalIgnoreCase) { "string", "int", "number", "bool", "any" };

    /// <summary>Returns one message per mismatch; empty when <paramref name="value"/> fits <paramref name="shape"/>.</summary>
    public static List<string> Check(JsonElement value, JsonElement shape)
    {
        var errors = new List<string>();
        Walk(value, shape, "$", errors);
        return errors;
    }

    /// <summary>Returns a config error for a malformed shape sketch, or null when it is well-formed.</summary>
    public static string? Describe(JsonElement shape, string at = "Shape")
    {
        switch (shape.ValueKind)
        {
            case JsonValueKind.String:
                var s = shape.GetString()!;
                if (s.Contains('|'))
                    return s.Split('|').Any(p => p.Trim().Length == 0) ? $"{at}: '{s}' has an empty alternative." : null;
                if (s.EndsWith('?')) s = s[..^1];
                return Scalars.Contains(s) ? null
                    : $"{at}: unknown type '{s}'. Use string, int, number, bool, any, A|B, [T] or a nested map.";
            case JsonValueKind.Array:
                return shape.GetArrayLength() == 1 ? Describe(shape[0], at + "[]") : $"{at}: a list shape holds exactly one item type, e.g. [string].";
            case JsonValueKind.Object:
                foreach (var p in shape.EnumerateObject())
                    if (Describe(p.Value, $"{at}.{p.Name}") is { } err) return err;
                return null;
            default:
                return $"{at}: expected a type name, a one-item list or a map.";
        }
    }

    private static void Walk(JsonElement value, JsonElement shape, string path, List<string> errors)
    {
        switch (shape.ValueKind)
        {
            case JsonValueKind.Object:
                if (value.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"{path}: expected an object, found {Kind(value)}.");
                    return;
                }
                foreach (var field in shape.EnumerateObject())
                {
                    var optional = field.Value.ValueKind == JsonValueKind.String
                                   && field.Value.GetString()!.EndsWith('?');
                    if (!value.TryGetProperty(field.Name, out var child))
                    {
                        if (!optional) errors.Add($"{path}.{field.Name}: missing.");
                        continue;
                    }
                    Walk(child, field.Value, $"{path}.{field.Name}", errors);
                }
                return;

            case JsonValueKind.Array:
                if (value.ValueKind != JsonValueKind.Array)
                {
                    errors.Add($"{path}: expected a list, found {Kind(value)}.");
                    return;
                }
                var i = 0;
                foreach (var item in value.EnumerateArray())
                    Walk(item, shape[0], $"{path}[{i++}]", errors);
                return;

            case JsonValueKind.String:
                var type = shape.GetString()!;
                if (type.EndsWith('?'))
                {
                    if (value.ValueKind == JsonValueKind.Null) return;
                    type = type[..^1];
                }
                if (type.Contains('|'))
                {
                    var options = type.Split('|', StringSplitOptions.TrimEntries);
                    if (value.ValueKind != JsonValueKind.String
                        || !options.Contains(value.GetString(), StringComparer.OrdinalIgnoreCase))
                        errors.Add($"{path}: expected one of {string.Join(", ", options)}, found {Render(value)}.");
                    return;
                }
                if (!Matches(value, type))
                    errors.Add($"{path}: expected {type}, found {Kind(value)}.");
                return;
        }
    }

    private static bool Matches(JsonElement value, string type) => type.ToLowerInvariant() switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "int"    => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "bool"   => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        _        => true,
    };

    private static string Kind(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array  => "a list",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a bool",
        JsonValueKind.Null   => "null",
        _                    => "nothing",
    };

    private static string Render(JsonElement v) =>
        v.ValueKind == JsonValueKind.String ? $"'{v.GetString()}'" : Kind(v);
}
