using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace fuseraft.Core.SubAgents;

/// <summary>
/// A user-defined sub-agent: a Markdown file whose YAML frontmatter says <em>when</em> to use it and
/// <em>what it may touch</em>, and whose body is its system prompt.
/// </summary>
/// <param name="Name">Lowercase slug the parent model calls it by (<c>sub_agent_run agent=&lt;name&gt;</c>).</param>
/// <param name="Description">What it is for — the only thing the parent model sees when deciding to delegate.</param>
/// <param name="Instructions">The sub-agent's system prompt (the Markdown body).</param>
/// <param name="Model">Optional model id; <c>null</c> = the session's sub-agent model.</param>
/// <param name="Tools">
/// Names of tools it may use. <c>null</c> (key omitted) = the read-only explorer set. A list names
/// exact tools from the session's pool; the entry <c>*</c> (<see cref="AllTools"/>) grants the whole
/// write-capable delegate set. An explicit empty list = a pure-reasoning agent with no tools.
/// </param>
/// <param name="MaxIterations">Tool-round cap for one run.</param>
/// <param name="SourcePath">The file it was loaded from.</param>
/// <param name="Scope"><c>project</c> or <c>user</c>.</param>
public sealed record SubAgentDefinition(
    string                Name,
    string                Description,
    string                Instructions,
    string?               Model,
    IReadOnlyList<string>? Tools,
    int                   MaxIterations,
    string                SourcePath,
    string                Scope)
{
    /// <summary>The wildcard entry in <c>tools:</c> that grants the full delegate tool set.</summary>
    public const string AllToolsWildcard = "*";

    public bool AllTools => Tools is not null && Tools.Contains(AllToolsWildcard);
}

/// <summary>Everything discovered under the agent directories, plus non-fatal problems worth showing the user.</summary>
public sealed record SubAgentLoadResult(
    IReadOnlyList<SubAgentDefinition> Definitions,
    IReadOnlyList<string>             Problems);

/// <summary>
/// Discovers and parses sub-agent definition files. One malformed file never hides the rest: it is
/// reported in <see cref="SubAgentLoadResult.Problems"/> and skipped. Deliberately no I/O beyond
/// reading the files, and no model/tool resolution — that belongs to whoever owns the tool pool.
/// </summary>
public static class SubAgentDefinitionLoader
{
    public const int DefaultMaxIterations = 30;
    public const int MaxIterationsCeiling = 100;
    private const long MaxFileBytes       = 64 * 1024;

    private static readonly Regex NamePattern = new(@"^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.Compiled);

    // Fields the OpenHands SDK's agent files carry that fuseraft does not implement. Ignoring them
    // silently would let `permission_mode: always_confirm` look honoured when it is not.
    private static readonly HashSet<string> KnownFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "description", "model", "tools", "max_iterations", "max_iteration_per_run", "color",
    };

    /// <summary>
    /// Priority-ordered directories (project-native → project cross-client → user-native → user
    /// cross-client), mirroring skill discovery. Deduplicated by resolved path because running from
    /// the home directory makes the project and user <c>.agents/agents</c> the same folder.
    /// </summary>
    public static IReadOnlyList<(string Directory, string Scope)> DefaultSearchDirs(string cwd)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        (string, string)[] dirs =
        [
            (Path.Combine(cwd,  ".fuseraft", "agents"), "project"),
            (Path.Combine(cwd,  ".agents",   "agents"), "project"),
            (Path.Combine(FuseraftPaths.GlobalRoot, "agents"), "user"),
            (Path.Combine(home, ".agents",   "agents"), "user"),
        ];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. dirs.Select(d => (Path.GetFullPath(d.Item1), d.Item2)).Where(d => seen.Add(d.Item1))];
    }

    public static SubAgentLoadResult LoadFromDirectories(IEnumerable<(string Directory, string Scope)> dirs)
    {
        var defs     = new List<SubAgentDefinition>();
        var problems = new List<string>();
        var byName   = new Dictionary<string, SubAgentDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var (dir, scope) in dirs)
        {
            if (!Directory.Exists(dir)) continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly)
                    .Where(f => !Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex)
            {
                problems.Add($"{dir}: could not list agent files ({ex.Message})");
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    if (new FileInfo(file).Length > MaxFileBytes)
                    {
                        problems.Add($"{file}: skipped — larger than {MaxFileBytes / 1024} KB");
                        continue;
                    }
                    var content = File.ReadAllText(file);
                    if (!TryParse(file, scope, content, out var def, problems) || def is null) continue;

                    if (byName.TryGetValue(def.Name, out var winner))
                    {
                        problems.Add($"{file}: '{def.Name}' is shadowed by {winner.SourcePath} ({winner.Scope} scope wins) and was ignored");
                        continue;
                    }
                    byName[def.Name] = def;
                    defs.Add(def);
                }
                catch (Exception ex)
                {
                    problems.Add($"{file}: could not be read ({ex.Message})");
                }
            }
        }

        return new SubAgentLoadResult(defs, problems);
    }

    /// <summary>Parses one file's content. On failure appends the reason to <paramref name="problems"/> and returns false.</summary>
    public static bool TryParse(
        string path, string scope, string content, out SubAgentDefinition? definition, List<string> problems)
    {
        definition = null;

        if (!TrySplitFrontmatter(content, out var yaml, out var body))
        {
            problems.Add($"{path}: skipped — no YAML frontmatter (the file must start with a '---' block)");
            return false;
        }

        Dictionary<string, object?> fm;
        try
        {
            var raw = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object?>>(yaml) ?? [];
            // Field names are case-insensitive; keys that differ only by case are ambiguous and rejected here.
            fm = new Dictionary<string, object?>(raw, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            problems.Add($"{path}: skipped — invalid frontmatter YAML ({FirstLine(ex.Message)})");
            return false;
        }

        var name = (Scalar(fm, "name") ?? Path.GetFileNameWithoutExtension(path)).Trim();
        if (!NamePattern.IsMatch(name))
        {
            problems.Add($"{path}: skipped — name '{name}' must be lowercase letters, digits, '-' or '_' (max 64 chars)");
            return false;
        }

        var description = Scalar(fm, "description")?.Trim();
        if (string.IsNullOrEmpty(description))
        {
            problems.Add($"{path}: skipped — 'description' is required (it is how the parent model decides when to use '{name}')");
            return false;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            problems.Add($"{path}: skipped — the Markdown body is empty; it is the sub-agent's system prompt");
            return false;
        }

        var maxIterations = DefaultMaxIterations;
        var rawMax = Scalar(fm, "max_iterations") ?? Scalar(fm, "max_iteration_per_run");
        if (rawMax is not null)
        {
            if (int.TryParse(rawMax, out var n) && n >= 1 && n <= MaxIterationsCeiling)
                maxIterations = n;
            else
                problems.Add($"{path}: max_iterations '{rawMax}' must be 1–{MaxIterationsCeiling}; using {DefaultMaxIterations}");
        }

        var unsupported = fm.Keys.Where(k => !KnownFields.Contains(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        if (unsupported.Count > 0)
            problems.Add($"{path}: ignored unsupported field(s): {string.Join(", ", unsupported)}");

        definition = new SubAgentDefinition(
            name, description, body.Trim(),
            Model:         Scalar(fm, "model")?.Trim() is { Length: > 0 } m ? m : null,
            Tools:         ParseTools(fm),
            MaxIterations: maxIterations,
            SourcePath:    path,
            Scope:         scope);
        return true;
    }

    private static IReadOnlyList<string>? ParseTools(Dictionary<string, object?> fm)
    {
        if (!fm.TryGetValue("tools", out var raw) || raw is null)
            return fm.ContainsKey("tools") ? [] : null;   // `tools:` with no value = explicitly none

        IEnumerable<string> items = raw switch
        {
            string s               => s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            IEnumerable<object> xs => xs.Select(x => x?.ToString() ?? string.Empty),
            _                      => [raw.ToString() ?? string.Empty],
        };

        return [.. items.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string? Scalar(Dictionary<string, object?> fm, string key) =>
        fm.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static bool TrySplitFrontmatter(string content, out string yaml, out string body)
    {
        yaml = body = string.Empty;
        var text  = content.TrimStart('﻿').Replace("\r\n", "\n");
        var lines = text.Split('\n');

        var start = 0;
        while (start < lines.Length && lines[start].Trim().Length == 0) start++;
        if (start >= lines.Length || lines[start].TrimEnd() != "---") return false;

        for (var i = start + 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() != "---") continue;
            yaml = string.Join('\n', lines[(start + 1)..i]);
            body = string.Join('\n', lines[(i + 1)..]);
            return true;
        }
        return false;
    }

    private static string FirstLine(string s) => s.Split('\n', 2)[0].Trim();
}
