using System.Text.Json;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Strategies;

/// <summary>
/// Shared helpers for evaluating <see cref="StructuredCondition"/> predicates against
/// JSON extracted from an agent response text.
/// Used by both <see cref="StructuredSelectionStrategy"/> and
/// <see cref="KeywordSelectionStrategy"/> (when a keyword route carries an optional condition).
/// </summary>
internal static class StructuredConditionEvaluator
{
    /// <summary>
    /// Evaluates <paramref name="condition"/> against the root element of a parsed JSON object.
    /// Returns false when the field is missing or no operator is configured.
    /// </summary>
    public static bool EvaluateCondition(JsonElement root, StructuredCondition condition)
    {
        var fieldValue = GetFieldValue(root, condition.Field);

        if (condition.Is is not null)
            return string.Equals(fieldValue, condition.Is, StringComparison.OrdinalIgnoreCase);

        if (condition.IsNot is not null)
            return !string.Equals(fieldValue, condition.IsNot, StringComparison.OrdinalIgnoreCase);

        if (condition.Contains is not null)
            return fieldValue?.Contains(condition.Contains, StringComparison.OrdinalIgnoreCase) == true;

        if (condition.Exists is true)  return fieldValue is not null;
        if (condition.Exists is false) return fieldValue is null;

        return false;
    }

    /// <summary>
    /// Tries to extract a JSON object from agent response text.
    /// Checks three forms in order:
    /// <list type="number">
    ///   <item>The entire text is valid JSON.</item>
    ///   <item>A <c>```json … ```</c> code fence contains valid JSON.</item>
    ///   <item>The first <c>{</c>…last <c>}</c> substring is valid JSON.</item>
    /// </list>
    /// </summary>
    public static bool TryExtractJson(string text, out JsonDocument? doc)
    {
        doc = null;

        // 1. Whole text.
        if (TryParse(text, out doc)) return true;

        // 2. ```json ... ``` code fence.
        var fenceStart = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (fenceStart >= 0)
        {
            var contentStart = fenceStart + 7;
            var fenceEnd     = text.IndexOf("```", contentStart, StringComparison.Ordinal);
            if (fenceEnd > contentStart)
            {
                var fenced = text[contentStart..fenceEnd].Trim();
                if (TryParse(fenced, out doc)) return true;
            }
        }

        // 3. First { … last } substring.
        var braceOpen  = text.IndexOf('{');
        var braceClose = text.LastIndexOf('}');
        if (braceOpen >= 0 && braceClose > braceOpen)
        {
            var slice = text[braceOpen..(braceClose + 1)];
            if (TryParse(slice, out doc)) return true;
        }

        return false;
    }

    /// <summary>
    /// Navigates a dot-separated field path through a <see cref="JsonElement"/>.
    /// Returns the string representation of the leaf value, or null if the path
    /// does not exist or points to a null/undefined value.
    /// </summary>
    public static string? GetFieldValue(JsonElement root, string path)
    {
        var current = root;
        foreach (var part in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object) return null;
            if (!current.TryGetProperty(part, out current))  return null;
        }

        return current.ValueKind switch
        {
            JsonValueKind.Null      => null,
            JsonValueKind.Undefined => null,
            JsonValueKind.String    => current.GetString(),
            _                       => current.ToString()
        };
    }

    private static bool TryParse(string text, out JsonDocument? doc)
    {
        try
        {
            doc = JsonDocument.Parse(text.Trim());
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            doc = null;
            return false;
        }
    }
}
