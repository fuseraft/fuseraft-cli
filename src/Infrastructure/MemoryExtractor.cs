using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure;

/// <summary>
/// Extracts memory entries from a conversation using a single LLM call.
///
/// <para>
/// The extraction prompt includes the existing memory index so the model can update
/// existing entries in-place rather than creating duplicates. Network and JSON errors
/// are swallowed so the calling session is never interrupted by a failed save.
/// </para>
/// <para>
/// <see cref="ExtractAsync"/> returns <c>ParseFailed = true</c> when the model returned
/// a non-empty response that could not be parsed as a JSON array, letting callers
/// surface a diagnostic hint without crashing.
/// </para>
/// </summary>
public sealed class MemoryExtractor(IChatClient client)
{
    private const int MaxHistoryChars = 40_000;

    /// <returns>
    /// <c>Entries</c>: memories to save (may be empty when nothing was found).
    /// <c>ParseFailed</c>: true when the model returned non-empty text that was not
    /// a valid JSON array — a signal the caller can use to show a warning.
    /// </returns>
    public async Task<(List<MemoryEntry> Entries, bool ParseFailed)> ExtractAsync(
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<MemoryEntry> existingMemories,
        CancellationToken ct = default)
    {
        var excerpt = BuildExcerpt(history);
        if (string.IsNullOrWhiteSpace(excerpt)) return ([], false);

        var prompt = BuildPrompt(excerpt, BuildIndex(existingMemories));
        try
        {
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                cancellationToken: ct);
            var raw     = response.Text ?? string.Empty;
            var entries = Parse(raw);
            var parseFailed = entries is null && !string.IsNullOrWhiteSpace(raw);
            return (entries ?? [], parseFailed);
        }
        catch (OperationCanceledException) { throw; }
        catch { return ([], false); }
    }

    internal static string BuildExcerpt(IReadOnlyList<ChatMessage> history)
    {
        var budget = MaxHistoryChars;
        var turns  = new List<string>();

        for (int i = history.Count - 1; i >= 0 && budget > 0; i--)
        {
            var msg = history[i];
            if (msg.Role == ChatRole.System || msg.Role == ChatRole.Tool) continue;

            var parts = new List<string>();

            var textContent = msg.Text?.Trim();
            if (!string.IsNullOrEmpty(textContent))
                parts.Add(textContent);

            // Include tool names so the extractor can see what actions were taken,
            // even when the assistant turn has no prose (only function calls).
            var toolNames = msg.Contents.OfType<FunctionCallContent>().Select(fc => fc.Name).ToList();
            if (toolNames.Count > 0)
                parts.Add($"[called: {string.Join(", ", toolNames)}]");

            if (parts.Count == 0) continue;

            var text = string.Join(" ", parts);
            if (text.Length > budget) text = text[..budget] + "…";
            budget -= text.Length;
            turns.Add($"[{(msg.Role == ChatRole.User ? "user" : "assistant")}]: {text}");
        }

        turns.Reverse();

        // Insert a blank separator before each [user] turn (except the first) so
        // the model can see where one Q/A exchange ends and the next begins.
        var formatted = new List<string>(turns.Count * 2);
        for (int j = 0; j < turns.Count; j++)
        {
            if (j > 0 && turns[j].StartsWith("[user]:"))
                formatted.Add(string.Empty);
            formatted.Add(turns[j]);
        }

        return string.Join('\n', formatted);
    }

    private const int MaxIndexChars = 4_000;

    private static string BuildIndex(IReadOnlyList<MemoryEntry> memories)
    {
        if (memories.Count == 0) return "(none)";
        var lines = new System.Text.StringBuilder();
        var remaining = MaxIndexChars;
        foreach (var m in memories)
        {
            var line = $"- [{m.Name}] ({m.Type}): {m.Description}\n";
            if (line.Length > remaining) break;
            lines.Append(line);
            remaining -= line.Length;
        }
        return lines.Length > 0 ? lines.ToString().TrimEnd() : "(none)";
    }

    private static string BuildPrompt(string excerpt, string index)
    {
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        return $$"""
        You are a memory extraction assistant. Identify facts worth saving for future sessions.
        Today's date: {{date}}

        EXISTING MEMORIES:
        {{index}}

        RECENT CONVERSATION:
        {{excerpt}}

        Extract facts that are genuinely useful in a future session. Focus on:
        - user: preferences, expertise, working style
        - feedback: what approaches worked or did not (include why)
        - project: goals, decisions, constraints
        - reference: where to find important information

        Rules:
        - Only extract non-obvious facts a future assistant would benefit from knowing
        - Update an existing memory (same name) only when there is new or corrected information
        - Return an empty array if nothing worth saving was found
        - Output ONLY valid JSON — no prose, no markdown fences

        JSON format:
        [
          {
            "name": "short_snake_case_id",
            "description": "one-line summary under 100 chars",
            "type": "user | feedback | project | reference",
            "body": "full memory text"
          }
        ]
        """;
    }

    // Returns null when parsing fails (malformed JSON found but undeserializable),
    // distinguishing a true parse failure from a successful extraction that found nothing.
    // Returns [] when the model produced no JSON array at all (prose "nothing to save" responses).
    internal static List<MemoryEntry>? Parse(string text)
    {
        var t = StripCodeFences(text.Trim());

        var s = t.LastIndexOf('[');
        var e = t.LastIndexOf(']');
        if (s < 0 || e <= s) return null;

        try
        {
            var opts  = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var items = JsonSerializer.Deserialize<List<ExtractionDto>>(t[s..(e + 1)], opts) ?? [];
            return items
                .Where(i => !string.IsNullOrWhiteSpace(i.Name) && !string.IsNullOrWhiteSpace(i.Body))
                .Select(i => new MemoryEntry
                {
                    Name        = i.Name.Trim(),
                    Description = (i.Description ?? string.Empty).Trim(),
                    Type        = i.Type?.ToLowerInvariant() switch
                    {
                        "user"      => "user",
                        "feedback"  => "feedback",
                        "reference" => "reference",
                        _           => "project",
                    },
                    Body = i.Body.Trim(),
                })
                .ToList();
        }
        catch (JsonException) { return null; }
    }

    private static string StripCodeFences(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length < 2) return text;
        var first = lines[0].Trim();
        var last  = lines[^1].Trim();
        if (last == "```" && (first == "```json" || first == "```" || first == "```jsonc"))
            return string.Join('\n', lines[1..^1]).Trim();
        return text;
    }

    private sealed class ExtractionDto
    {
        [JsonPropertyName("name")]        public string  Name        { get; init; } = string.Empty;
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("type")]        public string? Type        { get; init; }
        [JsonPropertyName("body")]        public string  Body        { get; init; } = string.Empty;
    }
}
