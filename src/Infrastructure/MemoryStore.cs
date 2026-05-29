using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using fuseraft.Core;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure;

/// <summary>
/// Persistent memory store backed by MEMORY.md index + per-entry markdown files.
///
/// <para>
/// REPL: <c>~/.fuseraft/memory/repl/</c> — memories that survive between REPL sessions.
/// Agents: <c>~/.fuseraft/memory/agents/{name}/</c> — per-agent persistent facts.
/// </para>
///
/// <para>
/// Each entry is written as a markdown file with YAML frontmatter. The index file
/// (<c>MEMORY.md</c>) is a human-readable, one-line-per-entry listing that is also
/// the authoritative order for prompt injection.
/// </para>
///
/// <para>
/// When a <c>localCwd</c> is supplied to load/save methods, memories are scoped to
/// that directory via <c>.fuseraft/memory/memory_refs.json</c>, which records the GUIDs of
/// entries saved there. Directories that contain a <c>.fuseraft/</c> folder but no
/// refs file start with an empty memory set; directories without <c>.fuseraft/</c>
/// fall back to loading all globals (legacy behaviour).
/// </para>
/// </summary>
public sealed class MemoryStore
{
    private const string IndexFile    = "MEMORY.md";
    private const string IndexHeader  = "# Memory Index";
    // Relative path from cwd to the memory refs index (kept in sync with FuseraftPaths.LocalMemoryRefs).
    private const string LocalRefsFile = "memory/memory_refs.json";

    private readonly string _dir;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public static MemoryStore ForRepl() => new(FuseraftPaths.GlobalMemoryRepl);

    public static MemoryStore ForAgent(string agentName) => new(FuseraftPaths.GlobalMemoryAgent(SafeFileName(agentName)));

    internal static MemoryStore CreateForTest(string dir) => new(dir);

    private MemoryStore(string dir) => _dir = dir;

    // Read

    public async Task<List<MemoryEntry>> LoadAllAsync(CancellationToken ct = default)
    {
        CleanupStaleTmpFiles();
        var indexPath = Path.Combine(_dir, IndexFile);
        if (!File.Exists(indexPath)) return [];

        var entries = new List<MemoryEntry>();
        foreach (var line in await File.ReadAllLinesAsync(indexPath, ct))
        {
            var m = Regex.Match(line, @"^\s*-\s+\[([^\]]+)\]\(([^)]+)\)");
            if (!m.Success) continue;

            var filePath = Path.Combine(_dir, m.Groups[2].Value);
            if (!File.Exists(filePath)) continue;

            var entry = await ParseFileAsync(filePath, ct);
            if (entry is not null) entries.Add(entry);
        }
        return entries;
    }

    /// <summary>
    /// Loads only the memories whose GUIDs are listed in
    /// <c>{localCwd}/.fuseraft/memory/memory_refs.json</c>. Falls back to loading all
    /// globals when <c>.fuseraft/</c> does not exist in <paramref name="localCwd"/>.
    /// </summary>
    public Task<List<MemoryEntry>> LoadAllAsync(string localCwd, CancellationToken ct = default)
        => LoadByCwdAsync(localCwd, ct);

    /// <summary>
    /// Synchronous variant for callers that cannot await (e.g. synchronous factory methods).
    /// Uses blocking file I/O — call only from non-async contexts such as agent creation.
    /// </summary>
    public string? BuildPromptBlock()
    {
        const int MaxChars = 8_000;
        var entries = LoadAllSync();
        if (entries.Count == 0) return null;
        return FormatPromptBlock(entries, MaxChars);
    }

    /// <summary>
    /// Formats all memories as a block suitable for appending to a system prompt.
    /// Returns null when no memories exist.
    /// </summary>
    public async Task<string?> BuildPromptBlockAsync(CancellationToken ct = default)
    {
        const int MaxChars = 8_000;
        var entries = await LoadAllAsync(ct);
        return entries.Count == 0 ? null : FormatPromptBlock(entries, MaxChars);
    }

    /// <summary>
    /// Loads memories scoped to <paramref name="localCwd"/> (via its
    /// <c>.fuseraft/memory/memory_refs.json</c>) and formats them as a prompt block.
    /// </summary>
    public async Task<string?> BuildPromptBlockAsync(string localCwd, CancellationToken ct = default)
    {
        const int MaxChars = 8_000;
        var entries = await LoadAllAsync(localCwd, ct);
        return entries.Count == 0 ? null : FormatPromptBlock(entries, MaxChars);
    }

    private static string FormatPromptBlock(List<MemoryEntry> entries, int maxChars)
    {
        var sb        = new StringBuilder();
        var remaining = maxChars;
        sb.AppendLine("MEMORY — facts recalled from prior sessions:");

        foreach (var e in entries.OrderBy(e => e.Type).ThenBy(e => e.Name))
        {
            if (remaining <= 0) break;

            var header = $"[{e.Type}] {e.Name}: {e.Description}";

            if (!string.IsNullOrWhiteSpace(e.Body))
            {
                var indented = string.Join("\n", e.Body.Split('\n').Select(l => $"  {l}"));
                var full     = $"{header}\n{indented}";
                if (full.Length <= remaining)
                {
                    sb.AppendLine(full);
                    remaining -= full.Length;
                }
                else
                {
                    // Body doesn't fit — include header only so the name stays visible.
                    sb.AppendLine(header);
                    remaining -= header.Length;
                }
            }
            else
            {
                sb.AppendLine(header);
                remaining -= header.Length;
            }
        }

        return sb.ToString().TrimEnd();
    }

    private List<MemoryEntry> LoadAllSync()
    {
        CleanupStaleTmpFiles();
        var indexPath = Path.Combine(_dir, IndexFile);
        if (!File.Exists(indexPath)) return [];

        var entries = new List<MemoryEntry>();
        foreach (var line in File.ReadAllLines(indexPath))
        {
            var m = Regex.Match(line, @"^\s*-\s+\[([^\]]+)\]\(([^)]+)\)");
            if (!m.Success) continue;

            var filePath = Path.Combine(_dir, m.Groups[2].Value);
            if (!File.Exists(filePath)) continue;

            try
            {
                var entry = ParseFile(File.ReadAllText(filePath), filePath);
                if (entry is not null) entries.Add(entry);
            }
            catch { /* corrupt entry — skip */ }
        }
        return entries;
    }

    // Write

    public async Task<string> SaveAsync(MemoryEntry entry, string? localCwd = null, CancellationToken ct = default)
    {
        // Reuse an existing GUID when a same-named entry is already stored so that
        // repeated saves of the same memory update the file in-place rather than
        // creating orphaned files and duplicate refs.
        if (string.IsNullOrEmpty(entry.Guid))
        {
            var prior = (await LoadAllAsync(ct))
                .FirstOrDefault(e => e.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
            if (prior is not null && !string.IsNullOrEmpty(prior.Guid))
                entry = entry with { Guid = prior.Guid };
        }

        var guid = string.IsNullOrEmpty(entry.Guid) ? System.Guid.NewGuid().ToString("N") : entry.Guid;
        entry = entry with { Guid = guid };

        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_dir);
            var fileName = $"memory_{guid}.md";
            var path     = Path.Combine(_dir, fileName);
            await File.WriteAllTextAsync(path, FormatFile(entry), ct);
            await UpsertIndexLineAsync(entry, fileName, ct);
        }
        finally { _lock.Release(); }

        if (localCwd is not null)
            await AddLocalRefAsync(localCwd, guid, ct);

        return guid;
    }

    public async Task<bool> DeleteAsync(string name, string? localCwd = null, CancellationToken ct = default)
    {
        // Look up by stored Name (case-insensitive) so the caller doesn't need
        // to know the exact casing or SafeFileName transformation that was used.
        // Load before acquiring the lock to avoid holding it during directory enumeration.
        var entries = localCwd is not null
            ? await LoadAllAsync(localCwd, ct)
            : await LoadAllAsync(ct);
        var entry = entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return false;

        await _lock.WaitAsync(ct);
        try
        {
            var path = Path.Combine(_dir, entry.FilePath);
            if (File.Exists(path)) File.Delete(path);
            await RemoveIndexLineAsync(entry.Name, ct);
        }
        finally { _lock.Release(); }

        if (localCwd is not null && !string.IsNullOrEmpty(entry.Guid))
            await RemoveLocalRefAsync(localCwd, entry.Guid, ct);

        return true;
    }

    // Helpers — local-refs (cwd scoping)

    private async Task<List<MemoryEntry>> LoadByCwdAsync(string cwd, CancellationToken ct)
    {
        var fuseraftDir = Path.Combine(cwd, ".fuseraft");
        var refsPath    = Path.Combine(fuseraftDir, LocalRefsFile);

        if (!Directory.Exists(fuseraftDir))
            return await LoadAllAsync(ct); // not a fuseraft project — load all globals

        if (!File.Exists(refsPath))
            return []; // fuseraft project but no memories saved here yet

        var json  = await File.ReadAllTextAsync(refsPath, ct);
        var guids = JsonSerializer.Deserialize<string[]>(json) ?? [];

        var entries = new List<MemoryEntry>();
        foreach (var guid in guids)
        {
            var filePath = Path.Combine(_dir, $"memory_{guid}.md");
            if (!File.Exists(filePath)) continue;
            var entry = await ParseFileAsync(filePath, ct);
            if (entry is not null) entries.Add(entry);
        }
        return entries;
    }

    private static async Task AddLocalRefAsync(string cwd, string guid, CancellationToken ct)
    {
        var fuseraftDir = Path.Combine(cwd, ".fuseraft");
        var refsPath    = Path.Combine(fuseraftDir, LocalRefsFile);
        Directory.CreateDirectory(Path.GetDirectoryName(refsPath)!);

        string[] existing = [];
        if (File.Exists(refsPath))
        {
            var json = await File.ReadAllTextAsync(refsPath, ct);
            existing = JsonSerializer.Deserialize<string[]>(json) ?? [];
        }

        if (Array.IndexOf(existing, guid) >= 0) return; // already registered

        var updated = existing.Append(guid).ToArray();
        await WriteAtomicAsync(refsPath, JsonSerializer.Serialize(updated) + '\n', ct);
    }

    private static async Task RemoveLocalRefAsync(string cwd, string guid, CancellationToken ct)
    {
        var refsPath = Path.Combine(cwd, ".fuseraft", LocalRefsFile);
        if (!File.Exists(refsPath)) return;

        var json    = await File.ReadAllTextAsync(refsPath, ct);
        var guids   = JsonSerializer.Deserialize<string[]>(json) ?? [];
        var updated = guids.Where(g => g != guid).ToArray();
        await WriteAtomicAsync(refsPath, JsonSerializer.Serialize(updated) + '\n', ct);
    }

    // Helpers — index

    private async Task UpsertIndexLineAsync(MemoryEntry entry, string fileName, CancellationToken ct)
    {
        var indexPath = Path.Combine(_dir, IndexFile);
        List<string> lines;
        if (File.Exists(indexPath))
            lines = [.. await File.ReadAllLinesAsync(indexPath, ct)];
        else
            lines = [IndexHeader, string.Empty];

        var newLine  = $"- [{entry.Name}]({fileName}) — {entry.Description}";
        var existing = lines.FindIndex(l => Regex.IsMatch(l, $@"\[{Regex.Escape(entry.Name)}\]\("));
        if (existing >= 0) lines[existing] = newLine;
        else lines.Add(newLine);

        await WriteAtomicAsync(indexPath, string.Join('\n', lines) + '\n', ct);
    }

    private async Task RemoveIndexLineAsync(string name, CancellationToken ct)
    {
        var indexPath = Path.Combine(_dir, IndexFile);
        if (!File.Exists(indexPath)) return;
        var lines = (await File.ReadAllLinesAsync(indexPath, ct))
            .Where(l => !Regex.IsMatch(l, $@"\[{Regex.Escape(name)}\]\("))
            .ToList();
        await WriteAtomicAsync(indexPath, string.Join('\n', lines) + '\n', ct);
    }

    private void CleanupStaleTmpFiles()
    {
        if (!Directory.Exists(_dir)) return;
        foreach (var f in Directory.GetFiles(_dir, "*.tmp"))
            try { File.Delete(f); } catch { /* best effort */ }
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, path, overwrite: true);
    }

    private static async Task<MemoryEntry?> ParseFileAsync(string path, CancellationToken ct)
    {
        try   { return ParseFile(await File.ReadAllTextAsync(path, ct), path); }
        catch { return null; }
    }

    internal static MemoryEntry? ParseFile(string text, string path)
    {
        // Require --- at the very start, then find the first \n--- that is immediately
        // followed by \n, \r, or end-of-string — i.e., --- occupies its own line.
        // This prevents \n--- a/Foo.cs (git diff header) and \n---old-section
        // (markdown section separator) from being mistaken for the close delimiter.
        const string Open     = "---";
        const string CloseTag = "\n---";

        if (!text.StartsWith(Open)) return null;

        int closeIdx = -1;
        int search   = Open.Length;
        while (true)
        {
            var candidate = text.IndexOf(CloseTag, search);
            if (candidate < 0) break;
            var after = candidate + CloseTag.Length;
            if (after >= text.Length || text[after] == '\n' || text[after] == '\r')
            {
                closeIdx = candidate;
                break;
            }
            search = candidate + 1;
        }
        if (closeIdx < 0) return null;

        var guid = string.Empty;
        var name = string.Empty;
        var desc = string.Empty;
        var type = "project";

        foreach (var line in text[Open.Length..closeIdx].Split('\n'))
        {
            var c = line.IndexOf(':');
            if (c < 0) continue;
            switch (line[..c].Trim().ToLowerInvariant())
            {
                case "guid":        guid = line[(c + 1)..].Trim(); break;
                case "name":        name = line[(c + 1)..].Trim(); break;
                case "description": desc = line[(c + 1)..].Trim(); break;
                case "type":        type = line[(c + 1)..].Trim(); break;
            }
        }

        if (string.IsNullOrEmpty(name)) return null;

        return new MemoryEntry
        {
            Guid        = guid,
            Name        = name,
            Description = desc,
            Type        = type,
            Body        = text[(closeIdx + CloseTag.Length)..].Trim(),
            FilePath    = Path.GetFileName(path),
        };
    }

    internal static string FormatFile(MemoryEntry e) =>
        $"---\nguid: {e.Guid}\nname: {e.Name}\ndescription: {e.Description}\ntype: {e.Type}\n---\n\n{e.Body}\n";

    internal static string SafeFileName(string name)
    {
        var safe = Regex.Replace(name.ToLowerInvariant().Replace(' ', '_'), @"[^a-z0-9_\-]", string.Empty);
        if (string.IsNullOrEmpty(safe))
            throw new ArgumentException($"Memory name '{name}' produces an empty filename after sanitization.", nameof(name));
        return safe;
    }
}
