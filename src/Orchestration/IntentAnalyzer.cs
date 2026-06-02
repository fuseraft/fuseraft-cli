namespace fuseraft.Orchestration;

/// <summary>
/// Signals extracted from a task description by <see cref="IntentAnalyzer"/>.
/// </summary>
public sealed record IntentSignals
{
    /// <summary>Significant domain terms after stop-word filtering.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>PascalCase identifiers likely to be type or method names.</summary>
    public IReadOnlyList<string> ReferencedSymbols { get; init; } = [];

    /// <summary>Failure-related tokens adjacent to error keywords in the task text.</summary>
    public IReadOnlyList<string> FailurePatterns { get; init; } = [];

    public bool IsEmpty =>
        Keywords.Count == 0 && ReferencedSymbols.Count == 0 && FailurePatterns.Count == 0;
}

/// <summary>
/// Extracts intent signals from a task or brief description for use by
/// <see cref="KnowledgeRetriever"/> when querying the knowledge layer.
///
/// <para>Three signal classes are extracted:</para>
/// <list type="bullet">
///   <item><b>Keywords</b> — Significant domain terms after stop-word filtering.</item>
///   <item><b>ReferencedSymbols</b> — PascalCase identifiers likely to be type/method names.</item>
///   <item><b>FailurePatterns</b> — Failure-related tokens adjacent to error keywords.</item>
/// </list>
/// </summary>
public static class IntentAnalyzer
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "but", "for", "nor", "on", "at", "to", "by",
        "in", "of", "is", "it", "its", "as", "be", "do", "if", "no", "so", "we",
        "us", "our", "my", "your", "this", "that", "with", "from", "into", "have",
        "has", "had", "not", "all", "any", "was", "are", "will", "can", "may",
        "use", "used", "using", "when", "then", "than", "get", "set", "new", "add",
        "run", "file", "path", "type", "name", "value", "data", "true", "false",
        "null", "void", "var", "let", "out", "ref", "via", "also", "each", "per",
    };

    private static readonly HashSet<string> FailureKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "error", "fail", "failed", "failure", "broken", "crash", "exception", "invalid",
        "missing", "undefined", "wrong", "unexpected", "bug", "issue", "problem",
    };

    private static readonly char[] Delimiters =
        [' ', '\t', '\n', '\r', ',', ';', ':', '.', '(', ')', '[', ']',
         '{', '}', '"', '\'', '`', '/', '\\', '=', '<', '>', '!', '?',
         '@', '#', '*', '+', '-', '&', '|', '^', '%'];

    /// <summary>Extracts intent signals from <paramref name="task"/>.</summary>
    public static IntentSignals Analyze(string? task)
    {
        if (string.IsNullOrWhiteSpace(task))
            return new IntentSignals();

        var words = task.Split(Delimiters,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new IntentSignals
        {
            Keywords          = ExtractKeywords(words),
            ReferencedSymbols = ExtractSymbols(words),
            FailurePatterns   = ExtractFailurePatterns(words),
        };
    }

    private static IReadOnlyList<string> ExtractKeywords(string[] words) =>
        words
            .Where(w => w.Length > 2 && !StopWords.Contains(w) && !IsPascalCase(w))
            .Select(w => w.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(15)
            .ToList();

    private static IReadOnlyList<string> ExtractSymbols(string[] words) =>
        words
            .Where(IsPascalCase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

    private static IReadOnlyList<string> ExtractFailurePatterns(string[] words)
    {
        var patterns = new List<string>();
        for (int i = 0; i < words.Length; i++)
        {
            if (!FailureKeywords.Contains(words[i])) continue;

            patterns.Add(words[i].ToLowerInvariant());
            if (i + 1 < words.Length && IsPascalCase(words[i + 1]))
                patterns.Add(words[i + 1]);
            if (i > 0 && IsPascalCase(words[i - 1]))
                patterns.Add(words[i - 1]);
        }
        return patterns.Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
    }

    private static bool IsPascalCase(string word) =>
        word.Length >= 2 && char.IsUpper(word[0]) && word.Any(char.IsLower);
}
