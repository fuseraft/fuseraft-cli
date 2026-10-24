using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure.Memory;

/// <summary>
/// Extracts memory entries from a conversation using a single LLM call.
///
/// <para>
/// The extraction prompt includes the existing memory index so the model can update
/// existing entries in-place rather than creating duplicates. A failed model call throws,
/// so callers can tell it apart from an extraction that found nothing (and retry later).
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
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            cancellationToken: ct);
        var raw     = response.Text ?? string.Empty;
        var entries = Parse(raw);
        var parseFailed = entries is null && !string.IsNullOrWhiteSpace(raw);
        return (entries ?? [], parseFailed);
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

    private const int MaxIndexChars = 16_000;

    // Lists the existing memories with their bodies, so that updating one (same name) can merge
    // into what it already says rather than replace it with only the new facts. Once the budget
    // runs short, an entry is listed by its header alone; every entry gets at least that, so the
    // model never reuses a name it can't see and overwrites that memory unknowingly.
    internal static string BuildIndex(IReadOnlyList<MemoryEntry> memories)
    {
        if (memories.Count == 0) return "(none)";
        var lines = new System.Text.StringBuilder();
        var remaining = MaxIndexChars - memories.Sum(m => Header(m).Length + 1);
        foreach (var m in memories)
        {
            var header = Header(m);
            var body   = string.Join('\n', m.Body.Split('\n').Select(l => $"    {l}"));
            if (!string.IsNullOrWhiteSpace(m.Body) && body.Length + 1 <= remaining)
            {
                lines.Append(header).Append('\n').Append(body).Append('\n');
                remaining -= body.Length + 1;
            }
            else
                lines.Append(header).Append('\n');
        }
        return lines.ToString().TrimEnd();

        static string Header(MemoryEntry m) => $"- [{m.Name}] ({m.Type}): {m.Description}";
    }

    internal static string BuildPrompt(string excerpt, string index)
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
        - Never save data the assistant read or retrieved with tools: query results, records, file contents, logs.
          What a database or file holds can be read again, and it may be private
        - Never save personal information about anyone other than the user (names with salaries, contact details,
          account data), or secrets (passwords, tokens, keys, connection strings)
        - Don't save what a future session can look up cheaply, such as a table's columns or a file's layout,
          unless the user asked for it to be remembered
        - Update an existing memory (same name) only when there is new or corrected information. An update
          replaces the stored memory, so its body must keep everything still true from the existing body
        - Use a new name for a new fact; reusing an existing name overwrites that memory
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

    // Returns null when no JSON array could be parsed (malformed JSON, or prose with no array),
    // distinguishing a parse failure from a successful extraction that found nothing ([]).
    internal static List<MemoryEntry>? Parse(string text)
    {
        var t = StripCodeFences(text.Trim());

        var e = t.LastIndexOf(']');
        if (e < 0) return null;

        // The array starts at some '[' before the last ']', but not necessarily the first one
        // (prose like "these [important] things" can precede it) or the last one (a body can
        // contain brackets). Take the first '[' from which the rest parses as the array.
        List<ExtractionDto>? items = null;
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        for (var s = t.IndexOf('['); s >= 0 && s < e; s = t.IndexOf('[', s + 1))
        {
            try
            {
                items = JsonSerializer.Deserialize<List<ExtractionDto>>(t[s..(e + 1)], opts) ?? [];
                break;
            }
            catch (JsonException) { }
        }
        if (items is null) return null;

        return items
            .Where(i => !string.IsNullOrWhiteSpace(i.Body))
            .Select(i => new MemoryEntry
            {
                // Name and description are written as frontmatter lines and as the index's
                // "- [name](file)" link, so a line break or a bracket in them would corrupt both.
                Name        = SingleLine(Regex.Replace(i.Name, @"[\[\]()]", string.Empty)),
                Description = SingleLine(i.Description ?? string.Empty),
                Type        = i.Type?.ToLowerInvariant() switch
                {
                    "user"      => "user",
                    "feedback"  => "feedback",
                    "reference" => "reference",
                    _           => "project",
                },
                Body = i.Body.Trim(),
            })
            .Where(m => m.Name.Length > 0)
            .ToList();
    }

    private static string SingleLine(string s) =>
        string.Join(' ', s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

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
