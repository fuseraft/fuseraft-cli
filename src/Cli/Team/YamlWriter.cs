using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace fuseraft.Cli.Team;

/// <summary>Writes a <see cref="JsonNode"/> tree as readable YAML: block maps, literal multi-line strings, flow lists of short scalars.</summary>
internal static partial class YamlWriter
{
    /// <summary>Renders <paramref name="root"/>, separating top-level keys with a blank line unless <paramref name="spaced"/> is false.</summary>
    public static string Write(JsonObject root, bool spaced = true)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var (key, value) in root)
        {
            if (!first && spaced) sb.Append('\n');
            first = false;
            WriteEntry(sb, key, value, 0);
        }
        return sb.ToString();
    }

    private static void WriteEntry(StringBuilder sb, string key, JsonNode? value, int indent)
    {
        var pad = new string(' ', indent);
        sb.Append(pad).Append(Key(key)).Append(':');
        WriteValue(sb, value, indent);
    }

    // Writes whatever follows "key:" — inline for scalars and empty/flow collections, else on new lines.
    private static void WriteValue(StringBuilder sb, JsonNode? value, int indent)
    {
        switch (value)
        {
            case JsonObject obj when obj.Count == 0:
                sb.Append(" {}\n");
                break;
            case JsonObject obj:
                sb.Append('\n');
                foreach (var (k, v) in obj) WriteEntry(sb, k, v, indent + 2);
                break;
            case JsonArray arr when arr.Count == 0:
                sb.Append(" []\n");
                break;
            case JsonArray arr when IsFlowList(arr):
                sb.Append(" [").Append(string.Join(", ", arr.Select(Scalar))).Append("]\n");
                break;
            case JsonArray arr:
                sb.Append('\n');
                WriteList(sb, arr, indent + 2);
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String && v.GetValue<string>().Contains('\n'):
                WriteBlock(sb, v.GetValue<string>(), indent + 2);
                break;
            default:
                sb.Append(' ').Append(Scalar(value)).Append('\n');
                break;
        }
    }

    private static void WriteList(StringBuilder sb, JsonArray arr, int indent)
    {
        var pad = new string(' ', indent);
        foreach (var item in arr)
        {
            if (item is JsonObject obj && obj.Count > 0)
            {
                var firstKey = true;
                foreach (var (k, v) in obj)
                {
                    if (firstKey)
                    {
                        sb.Append(pad).Append("- ").Append(Key(k)).Append(':');
                        WriteValue(sb, v, indent + 2);
                        firstKey = false;
                    }
                    else WriteEntry(sb, k, v, indent + 2);
                }
            }
            else if (item is JsonArray inner && !IsFlowList(inner))
            {
                sb.Append(pad).Append("-\n");
                WriteList(sb, inner, indent + 2);
            }
            else
            {
                sb.Append(pad).Append('-');
                WriteValue(sb, item, indent);
            }
        }
    }

    private static void WriteBlock(StringBuilder sb, string text, int indent)
    {
        var pad = new string(' ', indent);
        sb.Append(text.EndsWith('\n') ? " |\n" : " |-\n");
        foreach (var line in text.TrimEnd('\n').Split('\n'))
            sb.Append(line.Length == 0 ? "" : pad + line.TrimEnd()).Append('\n');
    }

    private static bool IsFlowList(JsonArray arr) =>
        arr.All(i => i is JsonValue v && !(v.GetValueKind() == JsonValueKind.String && v.GetValue<string>().Contains('\n')))
        && arr.Sum(i => Scalar(i).Length + 2) < 70;

    private static string Key(string key) => SafePlain().IsMatch(key) ? key : Quote(key);

    private static string Scalar(JsonNode? node)
    {
        if (node is null) return "null";
        var v = (JsonValue)node;
        return v.GetValueKind() switch
        {
            JsonValueKind.True   => "true",
            JsonValueKind.False  => "false",
            JsonValueKind.Number => v.ToJsonString(),
            JsonValueKind.String => StringScalar(v.GetValue<string>()),
            _                    => v.ToJsonString(),
        };
    }

    private static string StringScalar(string s)
    {
        if (s.Length == 0) return "\"\"";
        var looksTyped = s is "true" or "false" or "null" or "~" or "yes" or "no" or "on" or "off"
            || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        return !looksTyped && SafePlain().IsMatch(s) ? s : Quote(s);
    }

    private static string Quote(string s) => JsonSerializer.Serialize(s, QuoteOptions);

    private static readonly JsonSerializerOptions QuoteOptions =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Plain scalars: no leading indicator, no ": " or " #", no trailing space, no flow characters.
    [GeneratedRegex(@"^(?![-?:,\[\]{}#&*!|>'""%@`\s])(?!.*(: |\s#|[\[\]{},]))(?!.*[\s:]$)[^\n]+$")]
    private static partial Regex SafePlain();
}
