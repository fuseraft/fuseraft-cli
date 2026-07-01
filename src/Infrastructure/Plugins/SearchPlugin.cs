using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Gives agents the ability to explore a codebase or directory tree:
/// find files by name, search file contents by pattern, and locate
/// symbol definitions (classes, functions, interfaces, etc.).
/// </summary>
public sealed class SearchPlugin
{
    // Compiled Regex instances are expensive to create and are safe to share across calls.
    // Keyed by (pattern, options) so different case-sensitivity settings stay independent.
    private static readonly ConcurrentDictionary<(string Pattern, RegexOptions Options), Regex> RegexCache = new();

    private static Regex GetOrCreateRegex(string pattern, RegexOptions options) =>
        RegexCache.GetOrAdd((pattern, options), key => new Regex(key.Pattern, key.Options | RegexOptions.Compiled));

    // Language-agnostic definition patterns keyed by common keyword.
    private static readonly (string Keyword, string Pattern)[] SymbolPatterns =
    [
        ("class",     @"(class|record|struct|enum)\s+{0}"),
        ("interface", @"interface\s+{0}"),
        ("function",  @"(function|func|fn|def|sub)\s+{0}"),
        ("method",    @"(public|private|protected|internal|static|async|override|virtual)[\w\s]*\s+{0}\s*[(<]"),
        ("variable",  @"(var|let|const|val)\s+{0}\s*[=:]"),
    ];

    // File search

    [Description("Find files by name pattern.")]
    public string SearchFiles(
        [Description("Filename wildcard, e.g. '*.cs'.")] string pattern,
        [Description("Root directory.")] string directory = ".",
        [Description("Max results.")] int maxResults = 100)
    {
        if (!Directory.Exists(directory))
            return PluginResult.Error($"Directory not found: {directory}");

        try
        {
            var files = Directory
                .EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
                .Where(f => !DirectoryFilters.IsExcluded(f))
                .Take(maxResults)
                .ToList();

            if (files.Count == 0)
                return PluginResult.Info($"No files matched '{pattern}' under {directory}");

            var sb = new StringBuilder();
            sb.AppendLine($"[RESULTS] {files.Count} file(s) matched '{pattern}':");
            foreach (var f in files)
                sb.AppendLine($"  {f}");

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return PluginResult.Error(ex.Message);
        }
    }

    // Content search

    [Description("Search file contents by text or regex (like grep). 'query' is the pattern, not a path — use 'directory' to scope.")]
    public string SearchContent(
        [Description("Text or regex to search for.")] string query,
        [Description("Root directory.")] string directory = ".",
        [Description("File filter, e.g. '*.cs'.")] string filePattern = "*",
        [Description("Max matching lines.")] int maxResults = 100,
        [Description("Case-sensitive search.")] bool caseSensitive = false)
    {
        // Guard: catch agents passing a directory path as the query instead of as 'directory'.
        if (!string.IsNullOrEmpty(query) &&
            Regex.IsMatch(query, @"^[\w./-]+/?$") &&
            Directory.Exists(query))
            return PluginResult.Error(
                $"'{query}' looks like a directory path, not a search pattern. " +
                $"Did you mean: SearchContent(query: \"<pattern>\", directory: \"{query}\")?");

        if (!Directory.Exists(directory))
            return PluginResult.Error($"Directory not found: {directory}");

        // Some models HTML-encode characters in tool arguments (e.g. &lt; for <).
        // Decode so that a query like "&lt;TargetFramework" still matches "<TargetFramework".
        query = System.Net.WebUtility.HtmlDecode(query);

        Regex regex;
        try
        {
            var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            regex = GetOrCreateRegex(query, options);
        }
        catch (ArgumentException ex)
        {
            return PluginResult.Error($"Invalid regex pattern: {ex.Message}");
        }

        var sb = new StringBuilder();
        int totalMatches = 0;
        int filesWithMatches = 0;
        int skippedFiles = 0;

        foreach (var file in Directory.EnumerateFiles(directory, filePattern, SearchOption.AllDirectories)
                     .Where(f => !DirectoryFilters.IsExcluded(f)))
        {
            if (totalMatches >= maxResults) break;

            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { skippedFiles++; continue; }  // skip unreadable files (binary, locked, etc.)

            var fileMatches = new List<string>();

            for (int i = 0; i < lines.Length && totalMatches < maxResults; i++)
            {
                if (regex.IsMatch(lines[i]))
                {
                    fileMatches.Add($"  L{i + 1}: {lines[i].Trim()}");
                    totalMatches++;
                }
            }

            if (fileMatches.Count > 0)
            {
                filesWithMatches++;
                sb.AppendLine(file);
                foreach (var match in fileMatches)
                    sb.AppendLine(match);
            }
        }

        if (totalMatches == 0)
        {
            var noMatchNote = skippedFiles > 0 ? $" ({skippedFiles} unreadable file(s) skipped)" : string.Empty;
            return PluginResult.Info($"No matches found for '{query}' under {directory}{noMatchNote}");
        }

        var header = $"[RESULTS] {totalMatches} match(es) in {filesWithMatches} file(s)";
        if (totalMatches >= maxResults)
            header += " (limit reached — increase maxResults to see more)";
        if (skippedFiles > 0)
            header += $" ({skippedFiles} unreadable file(s) skipped)";

        return header + "\n\n" + sb.ToString().TrimEnd();
    }

    // Caller search

    [Description("Find call sites and usages of a symbol across source files.")]
    public string SearchCallers(
        [Description("Symbol name to find usages of.")] string symbol,
        [Description("Root directory.")] string directory = ".",
        [Description("File extension filter, e.g. '.cs'.")] string extension = "",
        [Description("Max results.")] int maxResults = 100)
    {
        if (!Directory.Exists(directory))
            return PluginResult.Error($"Directory not found: {directory}");

        var escapedSymbol = Regex.Escape(symbol);

        // Call-site pattern: symbol used as invocation, constructor, type annotation, or inheritance.
        Regex callSiteRegex;
        try
        {
            callSiteRegex = GetOrCreateRegex(
                $@"\b{escapedSymbol}\s*[(<\.:]|\bnew\s+{escapedSymbol}\b|:\s*{escapedSymbol}\b",
                RegexOptions.IgnoreCase);
        }
        catch (ArgumentException ex)
        {
            return PluginResult.Error($"Could not build caller pattern: {ex.Message}");
        }

        // Exclude definition lines — same patterns used by SearchSymbol — so we return
        // only references, not the declaration of the symbol itself.
        var defPattern = string.Join("|", SymbolPatterns.Select(p => string.Format(p.Pattern, escapedSymbol)));
        Regex? defRegex = null;
        try { defRegex = GetOrCreateRegex(defPattern, RegexOptions.IgnoreCase); }
        catch { /* best-effort; if pattern fails, skip exclusion */ }

        var filePattern = string.IsNullOrWhiteSpace(extension)
            ? "*"
            : $"*{(extension.StartsWith('.') ? extension : '.' + extension)}";

        var sb = new StringBuilder();
        int totalMatches = 0;
        int skippedFiles = 0;

        foreach (var file in Directory.EnumerateFiles(directory, filePattern, SearchOption.AllDirectories)
                     .Where(f => !DirectoryFilters.IsExcluded(f)))
        {
            if (totalMatches >= maxResults) break;

            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { skippedFiles++; continue; }

            for (int i = 0; i < lines.Length && totalMatches < maxResults; i++)
            {
                var line = lines[i];
                if (!callSiteRegex.IsMatch(line)) continue;
                if (defRegex?.IsMatch(line) == true) continue;

                sb.AppendLine($"{file}:L{i + 1}  {line.Trim()}");
                totalMatches++;
            }
        }

        if (totalMatches == 0)
        {
            var note = skippedFiles > 0 ? $" ({skippedFiles} unreadable file(s) skipped)" : string.Empty;
            return PluginResult.Info($"No call sites found for '{symbol}' under {directory}{note}");
        }

        var header = $"[RESULTS] {totalMatches} call site(s) found for '{symbol}'";
        if (totalMatches >= maxResults)
            header += " (limit reached — increase maxResults to see more)";
        if (skippedFiles > 0)
            header += $" ({skippedFiles} unreadable file(s) skipped)";

        return header + ":\n\n" + sb.ToString().TrimEnd();
    }

    // Symbol search

    [Description("Find symbol definitions across source files.")]
    public string SearchSymbol(
        [Description("Symbol name.")] string symbol,
        [Description("Root directory.")] string directory = ".",
        [Description("File extension filter, e.g. '.cs'.")] string extension = "",
        [Description("Max results.")] int maxResults = 50)
    {
        if (!Directory.Exists(directory))
            return PluginResult.Error($"Directory not found: {directory}");

        // Build a combined pattern that matches any known definition form.
        var escapedSymbol = Regex.Escape(symbol);
        var combined = string.Join("|", SymbolPatterns.Select(p => string.Format(p.Pattern, escapedSymbol)));
        Regex regex;
        try
        {
            regex = GetOrCreateRegex(combined, RegexOptions.IgnoreCase);
        }
        catch (ArgumentException ex)
        {
            return PluginResult.Error($"Could not build symbol pattern: {ex.Message}");
        }

        var filePattern = string.IsNullOrWhiteSpace(extension)
            ? "*"
            : $"*{(extension.StartsWith('.') ? extension : '.' + extension)}";

        var sb = new StringBuilder();
        int totalMatches = 0;
        int skippedFiles = 0;

        foreach (var file in Directory.EnumerateFiles(directory, filePattern, SearchOption.AllDirectories)
                     .Where(f => !DirectoryFilters.IsExcluded(f)))
        {
            if (totalMatches >= maxResults) break;

            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { skippedFiles++; continue; }

            for (int i = 0; i < lines.Length && totalMatches < maxResults; i++)
            {
                if (regex.IsMatch(lines[i]))
                {
                    sb.AppendLine($"{file}:L{i + 1}  {lines[i].Trim()}");
                    totalMatches++;
                }
            }
        }

        if (totalMatches == 0)
        {
            var noMatchNote = skippedFiles > 0 ? $" ({skippedFiles} unreadable file(s) skipped)" : string.Empty;
            return PluginResult.Info($"No definition found for '{symbol}' under {directory}{noMatchNote}");
        }

        var header = $"[RESULTS] {totalMatches} definition(s) found for '{symbol}'";
        if (skippedFiles > 0)
            header += $" ({skippedFiles} unreadable file(s) skipped)";

        return header + ":\n\n" + sb.ToString().TrimEnd();
    }
}
