namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Pure text-diffing and normalization utilities used by <see cref="FileSystemPlugin"/>'s
/// patch and write pipelines: patch-mismatch diagnostics for
/// <see cref="FileSystemPlugin.PatchFileAsync"/>, and the write-diff/typographic guard for
/// <see cref="FileSystemPlugin.WriteFileAsync"/>. <see cref="QuoteNormalizeExtensions"/> is
/// the one piece of state genuinely shared between the two pipelines — the reason both live
/// in this one file rather than two.
/// </summary>
internal static class FilePatchDiffing
{
    internal static string CountLines(string content, string searchText)
    {
        // Try to find the first line of the search text in the file for a useful hint.
        var firstSearchLine = searchText.Split('\n')[0].Trim();
        if (string.IsNullOrEmpty(firstSearchLine)) return string.Empty;

        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(firstSearchLine, StringComparison.Ordinal))
                return $"The first line of oldText ('{firstSearchLine}') was found near line {i + 1} — " +
                       $"check surrounding whitespace or indentation. ";
        }
        return string.Empty;
    }

    // Applies the same text normalisations WriteFileAsync applies so that oldText / newText
    // in a patch call are consistent with what is actually on disk.
    internal static string NormalizePatchText(string text, string ext)
    {
        // Quote normalisation: LLMs sometimes over-escape " as \" in tool-call JSON. The
        // written file has bare ", so oldText must also have bare " or the match fails.
        if (QuoteNormalizeExtensions.Contains(ext) && text.Contains("\\\""))
            text = text.Replace("\\\"", "\"");

        // Escape-sequence expansion: only expand when there are no real newlines but
        // literal \n sequences are present — same heuristic as WriteFileAsync.
        if (!text.Contains('\n') && !text.Contains('\r') && text.Contains("\\n"))
            text = text
                .Replace("\\r\\n", "\r\n")
                .Replace("\\n",    "\n")
                .Replace("\\t",    "\t");

        return text;
    }

    // Returns a context window around the best partial match of searchText in fileContent.
    // Finds the line in fileContent that best matches the first line of searchText
    // (by longest common prefix), then returns contextLines lines before and after it.
    // Returns an empty string when no useful match is found.
    internal static string ExtractExcerpt(string fileContent, string searchText, int contextLines)
    {
        var fileLines   = fileContent.Split('\n');
        var firstSearch = searchText.Split('\n')[0].Trim();
        if (string.IsNullOrEmpty(firstSearch) || fileLines.Length == 0) return string.Empty;

        // Find the line with the longest common prefix to the first search line.
        int bestLine = -1;
        int bestScore = 0;
        for (int i = 0; i < fileLines.Length; i++)
        {
            var fileLine = fileLines[i].Trim();
            int score = 0;
            int maxLen = Math.Min(firstSearch.Length, fileLine.Length);
            while (score < maxLen && firstSearch[score] == fileLine[score]) score++;
            if (score > bestScore) { bestScore = score; bestLine = i; }
        }

        if (bestLine < 0 || bestScore < 4) return string.Empty;

        var from = Math.Max(0, bestLine - contextLines);
        var to   = Math.Min(fileLines.Length - 1, bestLine + contextLines);
        var sb   = new System.Text.StringBuilder();
        for (int i = from; i <= to; i++)
        {
            var marker = i == bestLine ? ">>>" : "   ";
            sb.AppendLine($"{marker} {i + 1,4}: {fileLines[i]}");
        }
        return sb.ToString().TrimEnd();
    }

    // When the first line of searchText can be located in fileContent but a subsequent
    // line diverges, returns a hint identifying the first mismatching line so the agent
    // can correct oldText without a full re-read.
    internal static string FindFirstMismatchingLine(string fileContent, string searchText)
    {
        var searchLines = searchText.Split('\n');
        var fileLines   = fileContent.Split('\n');

        if (searchLines.Length <= 1) return string.Empty;

        var firstLine = searchLines[0];
        for (int i = 0; i <= fileLines.Length - searchLines.Length; i++)
        {
            if (fileLines[i] != firstLine) continue;

            for (int j = 1; j < searchLines.Length; j++)
            {
                if (fileLines[i + j] == searchLines[j]) continue;

                return $"Line {j + 1} of oldText ('{Truncate(searchLines[j])}') " +
                       $"does not match file line {i + j + 1} ('{Truncate(fileLines[i + j])}'). ";
            }
        }

        return string.Empty;
    }

    internal static string Truncate(string s, int max = 60)
        => s.Length <= max ? s : s[..max] + "…";

    // Extensions where a literal \" in the file is almost never intentional.
    // LLMs frequently over-escape quote characters in these languages (writing \" when
    // they mean "), producing syntax errors like `\"\"\"docstring\"\"\"` or
    // `f\"{x}\"`.  Normalising before write prevents the agent needing multiple
    // correction turns just to fix tooling-layer escaping artifacts.
    // C / C++ / C# / Rust are intentionally excluded because \" is a valid and common
    // string-escape sequence in those languages.
    internal static readonly HashSet<string> QuoteNormalizeExtensions =
        [".py", ".js", ".ts", ".jsx", ".tsx", ".rb", ".sh", ".bash", ".zsh",
         ".lua", ".pl", ".r", ".swift", ".kt", ".scala", ".ex", ".exs", ".kiwi"];

    // Source-code file extensions for which typographic-character contamination is
    // checked before writing.  LLMs occasionally substitute Unicode lookalikes for
    // ASCII punctuation (e.g. em-dash for hyphen-minus, curly quotes for straight
    // quotes) when generating code, producing syntax errors that are hard to diagnose
    // because the glyphs look identical in most editors.
    internal static readonly HashSet<string> SourceCodeExtensions =
        [".cs", ".go", ".py", ".ts", ".tsx", ".js", ".jsx",
         ".rs", ".java", ".cpp", ".c", ".h", ".hpp", ".cc",
         ".kt", ".scala", ".swift", ".fs", ".rb", ".php", ".kiwi"];

    // Map of typographic Unicode characters → human-readable names.
    // These are the characters that most commonly bleed from LLM prose generation
    // into code strings, causing compile/parse errors.
    internal static readonly Dictionary<char, string> TypographicCharNames = new()
    {
        ['—'] = "em-dash",
        ['–'] = "en-dash",
        ['“'] = "left double quotation mark",
        ['”'] = "right double quotation mark",
        ['‘'] = "left single quotation mark",
        ['’'] = "right single quotation mark",
        ['…'] = "ellipsis",
        [' '] = "non-breaking space",
        ['·'] = "middle dot",
    };

    internal readonly record struct TypographicHit(char Char, string Name, int Line, string Excerpt);

    // Scans `content` for typographic characters and returns up to `maxHits` findings
    // with the line number and a short excerpt.  Returns an empty list when clean.
    internal static List<TypographicHit> FindTypographicChars(string content, int maxHits = 10)
    {
        var hits = new List<TypographicHit>();
        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length && hits.Count < maxHits; i++)
        {
            var line = lines[i];
            foreach (var (ch, name) in TypographicCharNames)
            {
                if (!line.Contains(ch)) continue;
                var excerpt = line.Length > 80 ? line[..80] + "…" : line;
                hits.Add(new TypographicHit(ch, name, i + 1, excerpt.Trim()));
                if (hits.Count >= maxHits) break;
            }
        }
        return hits;
    }

    // Guard against model output truncation on large existing files.
    // When a model tries to write a file that is substantially larger on disk than the
    // content it is providing, the content is almost certainly truncated — the model ran
    // out of output tokens before finishing the file. Writing truncated content silently
    // would corrupt the file. Instead, return an error so the agent knows to use a
    // targeted edit tool (sed -i, or shell_run with a patch) rather than a full rewrite.
    //
    // Threshold: if the existing file is > 50 lines AND the new content has fewer than
    // 60 % of the existing line count, reject the write.
    // Returns an error string when the truncation guard fires, or null to proceed.
    internal static async Task<string?> EnsureFileExistsAsync(string resolved, string content)
    {
        if (File.Exists(resolved))
        {
            int existingLines = 0;
            await foreach (var _ in File.ReadLinesAsync(resolved)) existingLines++;
            var newLines = content.Split('\n').Length;
            if (existingLines > 50 && newLines < existingLines * 0.6)
                return PluginResult.Error(
                    $"WRITE BLOCKED — truncation guard: '{resolved}' currently has {existingLines} lines " +
                    $"but the content you provided has only {newLines} lines " +
                    $"({(double)newLines / existingLines:P0} of the original). " +
                    $"This almost always means your output was truncated before you finished writing the file.\n\n" +
                    $"DO NOT use write_file to rewrite large files. Instead, make targeted changes:\n" +
                    $"  • Use patch_file(path, oldText, newText) to replace an exact block — " +
                    $"this is the preferred approach for source-code edits.\n" +
                    $"  • Example: patch_file(\"{resolved}\", \"    Include,\\n\", \"    Include,\\n    ModuleIncludeAssign,\\n\")\n" +
                    $"  • Alternatively: shell_run with sed -i to insert/replace specific lines.\n" +
                    $"This approach is safer and avoids the token-limit truncation problem.");
        }
        return null;
    }

    // Encoding detection + line ending normalization: applies quote normalization, JSON
    // artifact stripping, escape-sequence expansion, and the typographic character guard.
    // Quote normalisation runs unconditionally for known extensions — it corrects a
    // JSON serialisation artifact (model double-escaping " as \") and must not be
    // skipped even when raw=true, which only controls escape-sequence expansion.
    // Returns an error string when typographic characters block the write, or null on success
    // (normalizedContent and normalised are set via out parameters).
    internal static string? ComputeAndReportDiff(string resolved, string content, string ext, bool raw,
        out string normalizedContent, out bool normalised)
    {
        normalised = false;

        if (QuoteNormalizeExtensions.Contains(ext) && content.Contains("\\\""))
        {
            content    = content.Replace("\\\"", "\"");
            normalised = true;
        }

        if (!raw)
        {
            // For .json files, normalise common LLM wrapping artifacts before writing.
            if (ext == ".json")
            {
                // Guard against blank/whitespace-only content — the model probably forgot
                // to include the content argument.  Returning an error here is cheaper than
                // a successful write that immediately fails downstream JSON validation.
                if (string.IsNullOrWhiteSpace(content))
                {
                    normalizedContent = content;
                    return PluginResult.Error(
                        "The 'content' argument is empty. Did you forget to include the JSON content? " +
                        "Pass the full JSON object as the 'content' parameter.");
                }

                var trimmed = content.TrimStart();

                // Strip markdown code fences (```json ... ``` or ``` ... ```).
                // A valid JSON file should never start with ``` — strip the fence and trailing
                // ``` so the file contains only the raw JSON object/array.
                if (trimmed.StartsWith("```"))
                {
                    // Skip the opening fence line (```json, ```, etc.)
                    var firstNewline = trimmed.IndexOf('\n');
                    if (firstNewline >= 0)
                        trimmed = trimmed[(firstNewline + 1)..];
                    // Strip the closing ```
                    var lastFence = trimmed.LastIndexOf("```");
                    if (lastFence >= 0)
                        trimmed = trimmed[..lastFence];
                    content    = trimmed.Trim();
                    normalised = true;
                }
                // Strip XML <parameter name="content">…</parameter> wrappers.
                // Some models emit tool-call XML artifacts as literal content, e.g.:
                //   <parameter name="content">{"goal": ...}</parameter>
                // Extract just the inner text so the file contains valid JSON.
                else if (trimmed.StartsWith("<parameter", StringComparison.OrdinalIgnoreCase))
                {
                    var closeTag = trimmed.IndexOf('>');
                    if (closeTag >= 0)
                    {
                        var inner  = trimmed[(closeTag + 1)..];
                        var endTag = inner.LastIndexOf("</parameter>", StringComparison.OrdinalIgnoreCase);
                        if (endTag >= 0) inner = inner[..endTag];
                        content    = inner.Trim();
                        normalised = true;
                    }
                }
            }

            // Detect double-escaped newlines: when a model constructs the tool-call JSON
            // argument by hand, it sometimes writes \\n instead of a real newline, so after
            // JSON deserialization the content string contains literal \n (backslash-n) rather
            // than actual newline characters. The tell-tale sign is a file with zero real
            // newlines but multiple literal \n sequences — replace them so the written file has
            // proper line endings instead of collapsing to a single line of escape sequences.
            if (!content.Contains('\n') && !content.Contains('\r') && content.Contains("\\n"))
            {
                content = content
                    .Replace("\\r\\n", "\r\n")
                    .Replace("\\n", "\n")
                    .Replace("\\t", "\t");
                normalised = true;
            }

            // Typographic character guard: source files that contain em-dashes, curly quotes,
            // non-breaking spaces, or other Unicode lookalikes will fail to compile or parse.
            // These characters appear when an LLM bleeds prose-generation typography into code.
            // Block the write and report each offending character so the agent can correct the
            // content before it reaches disk — preventing the delete/rewrite correction loop
            // caused by files that are syntactically broken from the moment they are written.
            if (SourceCodeExtensions.Contains(ext))
            {
                var hits = FindTypographicChars(content);
                if (hits.Count > 0)
                {
                    normalizedContent = content;
                    return PluginResult.Error(
                        $"WRITE BLOCKED — typographic characters found in source file '{resolved}'.\n" +
                        $"These are Unicode lookalikes for ASCII punctuation that cause compile/parse errors:\n\n" +
                        string.Join("\n", hits.Select(h =>
                            $"  line {h.Line}: U+{(int)h.Char:X4} {h.Name}\n    {h.Excerpt}")) +
                        $"\n\nReplace each with the correct ASCII character:\n" +
                        "  — (em-dash)         → - (hyphen-minus)\n" +
                        "  – (en-dash)         → - (hyphen-minus)\n" +
                        "  “” (curly dquotes) → \" (straight double quote)\n" +
                        "  ‘’ (curly squotes) → ' (apostrophe)\n" +
                        "  … (ellipsis)        → ... (three full stops)\n" +
                        "    (non-breaking sp) →   (regular space)\n" +
                        "\nCorrect the content and call write_file again.");
                }
            }
        }

        normalizedContent = content;
        return null;
    }
}
