using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json;

namespace fuseraft.Infrastructure.Mcp;

/// <summary>Reads the CallToolResult envelope an MCP tool returns, for display and failure checks.</summary>
public static class McpToolResult
{
    /// <summary>
    /// True when <paramref name="text"/> is a CallToolResult; <paramref name="content"/> is then its text content
    /// (other content kinds as their JSON) and <paramref name="isError"/> its <c>isError</c> flag. Text after the
    /// result, such as a notice the REPL's loop guard appended, is kept at the end of the content.
    /// </summary>
    /// <summary>
    /// The text to show for a tool's result: the text of the content an MCP tool returned (as the SDK's
    /// <see cref="TextContent"/>, a list of contents, or a CallToolResult's JSON), and any other result as its
    /// string. With <paramref name="indentJson"/>, an MCP tool's JSON text is indented; a string result never is,
    /// so a file a tool read is shown exactly as it is.
    /// </summary>
    public static string Display(object? result, bool indentJson = false) => result switch
    {
        null                         => "",
        TextContent t                => indentJson ? Indented(t.Text) : t.Text,
        DataContent d                => $"[{d.MediaType}]",
        IEnumerable<AIContent> items => string.Join("\n", items.Select(i => Display(i, indentJson))),
        _ => TryRead(result.ToString(), out var content, out _, indentJson) ? content : result.ToString() ?? "",
    };

    public static bool TryRead(string? text, out string content, out bool isError) =>
        TryRead(text, out content, out isError, indentJson: false);

    private static bool TryRead(string? text, out string content, out bool isError, bool indentJson)
    {
        content = "";
        isError = false;
        if (text is null || !text.TrimStart().StartsWith('{'))
            return false;
        try
        {
            var bytes  = Encoding.UTF8.GetBytes(text);
            var reader = new Utf8JsonReader(bytes);
            using var doc = JsonDocument.ParseValue(ref reader);
            var after  = Encoding.UTF8.GetString(bytes, (int)reader.BytesConsumed, bytes.Length - (int)reader.BytesConsumed).Trim();
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("content", out var items) || items.ValueKind != JsonValueKind.Array)
                return false;
            var parts = new List<string>();
            foreach (var item in items.EnumerateArray())
                parts.Add(item.ValueKind == JsonValueKind.Object &&
                          item.TryGetProperty("type", out var type) && type.ValueEquals("text") &&
                          item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                    ? indentJson ? Indented(t.GetString()!) : t.GetString()!
                    : item.GetRawText());
            if (after.Length > 0)
                parts.Add("\n" + after);
            content = string.Join("\n", parts);
            isError = root.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // A server's JSON answer, often a single long line, laid out so it can be read. Anything else is left as sent.
    private static string Indented(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            return text;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(doc.RootElement, IndentedOptions);
        }
        catch (JsonException)
        {
            return text;
        }
    }
}
