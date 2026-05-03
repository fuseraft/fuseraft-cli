using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Utilities for working with JSON data: formatting, dot-path querying, merging,
/// and schema extraction. Useful for agents processing API responses or config files.
/// </summary>
public sealed class JsonPlugin
{
    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions CompactOptions = new() { WriteIndented = false };

    // Formatting

    [Description("Pretty-print a JSON string.")]
    public string Format([Description("JSON string.")] string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return node?.ToJsonString(PrettyOptions) ?? "null";
        }
        catch (JsonException ex)
        {
            return $"[INVALID JSON] {ex.Message}";
        }
    }

    [Description("Minify a JSON string.")]
    public string Minify([Description("JSON string.")] string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return node?.ToJsonString(CompactOptions) ?? "null";
        }
        catch (JsonException ex)
        {
            return $"[INVALID JSON] {ex.Message}";
        }
    }

    // Querying

    [Description("Get a nested value by dot-separated path.")]
    public string Get(
        [Description("JSON string.")] string json,
        [Description("Dot-separated path.")] string path)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is null) return "[NULL]";

            var current = node;
            foreach (var segment in SplitPath(path))
            {
                if (current is null) return "[NULL] Path does not exist.";

                if (int.TryParse(segment, out var index))
                    current = current.AsArray()[index];
                else
                    current = current.AsObject()[segment];
            }

            return current?.ToJsonString(PrettyOptions) ?? "[NULL]";
        }
        catch (JsonException ex) { return $"[INVALID JSON] {ex.Message}"; }
        catch (Exception ex) { return $"[PATH ERROR] {ex.Message}"; }
    }

    [Description("Get top-level keys of a JSON object or array length.")]
    public string Keys([Description("JSON string.")] string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return node switch
            {
                JsonObject obj => string.Join(", ", obj.Select(kv => kv.Key)),
                JsonArray arr  => $"[ARRAY] length={arr.Count}",
                _              => $"[SCALAR] {node?.GetValue<object>()}",
            };
        }
        catch (JsonException ex)
        {
            return $"[INVALID JSON] {ex.Message}";
        }
    }

    [Description("Find keys matching a name (case-insensitive).")]
    public string Search(
        [Description("JSON string.")] string json,
        [Description("Key name to search for.")] string keyName)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is null) return "[NULL]";

            var results = new List<string>();
            SearchNode(node, string.Empty, keyName.ToLowerInvariant(), results);

            return results.Count == 0
                ? $"[NOT FOUND] No keys matching '{keyName}'."
                : string.Join("\n", results);
        }
        catch (JsonException ex)
        {
            return $"[INVALID JSON] {ex.Message}";
        }
    }

    // Transformation

    [Description("Shallow-merge two JSON objects. Patch keys override base.")]
    public string Merge(
        [Description("Base JSON object.")] string baseJson,
        [Description("Patch JSON object.")] string patchJson)
    {
        try
        {
            var baseObj = JsonNode.Parse(baseJson)?.AsObject()
                ?? throw new ArgumentException("'base' is not a JSON object.");
            var patchObj = JsonNode.Parse(patchJson)?.AsObject()
                ?? throw new ArgumentException("'patch' is not a JSON object.");

            // Copy base into a fresh object, then apply patch.
            var merged = JsonNode.Parse(baseObj.ToJsonString())!.AsObject();
            foreach (var (key, value) in patchObj)
                merged[key] = value?.DeepClone();

            return merged.ToJsonString(PrettyOptions);
        }
        catch (Exception ex)
        {
            return PluginResult.Error(ex.Message);
        }
    }

    [Description("Convert JSON to human-readable text.")]
    public string ToText(
        [Description("JSON string.")] string json,
        [Description("Indentation depth.")] int depth = 0)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return NodeToText(node, depth);
        }
        catch (JsonException ex)
        {
            return $"[INVALID JSON] {ex.Message}";
        }
    }

    [Description("Check if a string is valid JSON.")]
    public string Validate([Description("String to validate.")] string json)
    {
        try
        {
            JsonNode.Parse(json);
            return "valid";
        }
        catch (JsonException ex)
        {
            return $"invalid: {ex.Message} (line {ex.LineNumber}, pos {ex.BytePositionInLine})";
        }
    }

    // Private helpers

    private static IEnumerable<string> SplitPath(string path) =>
        path.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(s =>
            {
                // Handle bracket notation: "items[2]" → ["items", "2"]
                var open = s.IndexOf('[');
                if (open < 0) return [s];
                var close = s.IndexOf(']', open + 1);
                if (close < 0) return [s]; // malformed bracket — treat whole segment as a plain key
                var key = s[..open];
                var idx = s[(open + 1)..close];
                return string.IsNullOrEmpty(key) ? (IEnumerable<string>)[idx] : [key, idx];
            });

    private static void SearchNode(
        JsonNode node, string currentPath, string term, List<string> results)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj)
            {
                var path = string.IsNullOrEmpty(currentPath) ? key : $"{currentPath}.{key}";
                if (key.ToLowerInvariant().Contains(term))
                    results.Add($"{path} = {value?.ToJsonString()}");
                if (value is not null)
                    SearchNode(value, path, term, results);
            }
        }
        else if (node is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i] is not null)
                    SearchNode(arr[i]!, $"{currentPath}[{i}]", term, results);
            }
        }
    }

    private static string NodeToText(JsonNode? node, int depth)
    {
        var indent = new string(' ', depth * 2);
        return node switch
        {
            null => "(null)",
            JsonObject obj => FormatObject(obj, depth, indent),
            JsonArray arr => FormatArray(arr, depth, indent),
            _ => node.ToString()
        };
    }

    private static string FormatObject(JsonObject obj, int depth, string indent)
    {
        if (obj.Count == 0) return "{}";
        var sb = new StringBuilder();
        foreach (var (key, value) in obj)
            sb.AppendLine($"{indent}{key}: {NodeToText(value, depth + 1)}");
        return sb.ToString().TrimEnd();
    }

    private static string FormatArray(JsonArray arr, int depth, string indent)
    {
        if (arr.Count == 0) return "[]";
        var sb = new StringBuilder();
        sb.AppendLine($"{indent}[{arr.Count} items]");
        var preview = Math.Min(arr.Count, 5);
        for (int i = 0; i < preview; i++)
            sb.AppendLine($"{indent}  [{i}] {NodeToText(arr[i], depth + 1)}");
        if (arr.Count > preview)
            sb.AppendLine($"{indent}  ... {arr.Count - preview} more");
        return sb.ToString().TrimEnd();
    }
}
