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
    /// <c>Removals</c>: names of existing memories to remove — merged into another entry,
    /// shown wrong by the conversation, or tracking work that is finished. Only names in
    /// <paramref name="existingMemories"/> and not saved again in <c>Entries</c>.
    /// <c>ParseFailed</c>: true when the model returned non-empty text that was not
    /// a valid JSON array — a signal the caller can use to show a warning.
    /// </returns>
    public async Task<(List<MemoryEntry> Entries, List<string> Removals, bool ParseFailed)> ExtractAsync(
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<MemoryEntry> existingMemories,
        CancellationToken ct = default)
    {
        var excerpt = BuildExcerpt(history);
        if (string.IsNullOrWhiteSpace(excerpt)) return ([], [], false);

        var prompt = BuildPrompt(excerpt, BuildIndex(existingMemories));
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            cancellationToken: ct);
        var raw    = response.Text ?? string.Empty;
        var parsed = ParseResponse(raw);
        if (parsed is null) return ([], [], !string.IsNullOrWhiteSpace(raw));

        var (entries, removals) = parsed.Value;
        var removable = removals
            .Where(r => existingMemories.Any(m => m.Name.Equals(r, StringComparison.OrdinalIgnoreCase))
                     && !entries.Any(e => e.Name.Equals(r, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (entries, removable, false);
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

        Extract facts that will still be true and useful in a future session. Every memory is loaded into
        every later session in this project, so a wrong or stale one does harm; saving nothing is often right.
        Focus on:
        - user: standing preferences, expertise, working style
        - feedback: what approaches worked or did not, and why
        - project: goals, decisions and constraints that the code does not show
        - reference: where to find important information

        Never save:
        - Task progress or status: what was done this session, what is left, review findings, a to-do list.
          It is out of date by the next session
        - How the code works or how the project is laid out: components, commands, file locations, conventions
          visible in the source. A future session can read the code, and the code changes
        - An instruction meant for one reply or one task ("answer in two sentences", "use this format"). Save a
          preference only when the user states it as a general rule or repeats it across tasks, and quote them
        - A guess about why something failed, or a workaround for a one-time glitch
        - Data the assistant read or retrieved with tools: query results, records, file contents, logs. It can
          be read again, and it may be private
        - Personal information about anyone other than the user (names with salaries, contact details, account
          data), or secrets (passwords, tokens, keys, connection strings)

        Keep the set small:
        - Before adding a memory, check the existing ones. If one covers the same topic, update it (same name)
          instead of adding a near-duplicate. An update replaces the stored memory, so its body must keep
          everything still true from the existing body
        - Use a new name only for a new topic; reusing an existing name overwrites that memory
        - Remove an existing memory when you merged it into another one in this response, when the conversation
          shows it is wrong, or when it only tracks work that is finished. Do not remove a memory just because
          this conversation did not mention it

        Output ONLY a JSON array, no prose and no markdown fences. Return [] when nothing is worth saving.
        To save or update a memory:
          {"name": "short_snake_case_id", "description": "one-line summary under 100 chars",
           "type": "user | feedback | project | reference", "body": "full memory text"}
        To remove an existing memory:
          {"name": "existing_name", "remove": true}
        """;
    }

    // Returns null when no JSON array could be parsed (malformed JSON, or prose with no array),
    // distinguishing a parse failure from a successful extraction that found nothing ([]).
    internal static List<MemoryEntry>? Parse(string text) => ParseResponse(text)?.Entries;

    internal static (List<MemoryEntry> Entries, List<string> Removals)? ParseResponse(string text)
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

        var removals = items
            .Where(i => i.Remove)
            .Select(i => SingleLine(i.Name))
            .Where(n => n.Length > 0)
            .ToList();
        var entries = items
            .Where(i => !i.Remove && !string.IsNullOrWhiteSpace(i.Body))
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
        return (entries, removals);
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
        [JsonPropertyName("remove")]      public bool    Remove      { get; init; }
    }
}
