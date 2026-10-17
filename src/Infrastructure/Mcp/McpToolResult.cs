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
    public static bool TryRead(string? text, out string content, out bool isError)
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
                    ? t.GetString()!
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
}
