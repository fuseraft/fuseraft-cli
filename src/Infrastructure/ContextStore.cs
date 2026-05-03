using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Infrastructure;

/// <summary>
/// Manages the session context store at <c>.fuseraft/context/</c>.
///
/// <para>
/// Each imported item is copied to <c>.fuseraft/context/&lt;name&gt;/</c> and recorded
/// in <c>.fuseraft/context/index.json</c>. Because the context directory lives inside
/// the project working directory it is always within the sandbox and readable by every
/// agent via the standard <c>read_file</c> tool.
/// </para>
///
/// <para>
/// Call <see cref="BuildPromptSummaryAsync"/> at session start to produce a compact
/// block that can be appended to every agent's system prompt, giving agents awareness
/// of available context without requiring a dedicated discovery tool call.
/// </para>
/// </summary>
public sealed class ContextStore
{
    public const string DefaultContextDir = ".fuseraft/context";

    private const string IndexFileName = "index.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented              = true,
        DefaultIgnoreCondition     = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy       = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _contextDir;

    public ContextStore(string contextDir = DefaultContextDir)
    {
        _contextDir = contextDir;
    }

    // Mutating operations

    /// <summary>
    /// Copies <paramref name="sourcePath"/> (file or directory) into the context store
    /// under <paramref name="name"/> and records it in the index.
    /// If an item with the same name already exists its files are replaced.
    /// </summary>
    public async Task AddAsync(
        string sourcePath,
        string name,
        string? description  = null,
        CancellationToken ct = default)
    {
        if (!IsValidName(name))
            throw new ArgumentException(
                $"Invalid name '{name}'. Use only letters, digits, hyphens, and underscores.");

        var fullSource = Path.GetFullPath(ProcessHelper.ExpandHome(sourcePath));
        bool isFile = File.Exists(fullSource);
        bool isDir  = !isFile && Directory.Exists(fullSource);

        if (!isFile && !isDir)
            throw new FileNotFoundException($"Source not found: {fullSource}");

        var destDir = Path.Combine(_contextDir, name);
        // Wipe any previous copy so stale files from a renamed import don't linger.
        if (Directory.Exists(destDir))
            Directory.Delete(destDir, recursive: true);
        Directory.CreateDirectory(destDir);

        var files = new List<ContextFileEntry>();

        if (isFile)
        {
            var fileName = Path.GetFileName(fullSource);
            File.Copy(fullSource, Path.Combine(destDir, fileName));
            files.Add(new ContextFileEntry(fileName, new FileInfo(fullSource).Length));
        }
        else
        {
            foreach (var src in Directory.EnumerateFiles(fullSource, "*", SearchOption.AllDirectories))
            {
                var rel  = Path.GetRelativePath(fullSource, src);
                var dest = Path.Combine(destDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest);
                files.Add(new ContextFileEntry(rel.Replace('\\', '/'), new FileInfo(src).Length));
            }
        }

        var index = await LoadIndexAsync(ct);
        index.Items[name] = new ContextItem
        {
            Name        = name,
            Description = description,
            SourcePath  = fullSource,
            ImportedAt  = DateTime.UtcNow,
            Files       = files,
        };
        await SaveIndexAsync(index, ct);
    }

    /// <summary>
    /// Removes the named item from the context store and deletes its files.
    /// Throws <see cref="KeyNotFoundException"/> when the name does not exist.
    /// </summary>
    public async Task RemoveAsync(string name, CancellationToken ct = default)
    {
        var index = await LoadIndexAsync(ct);
        if (!index.Items.ContainsKey(name))
            throw new KeyNotFoundException($"Context item '{name}' not found.");

        var destDir = Path.Combine(_contextDir, name);
        if (Directory.Exists(destDir))
            Directory.Delete(destDir, recursive: true);

        index.Items.Remove(name);

        // Remove the index file entirely when no items remain, keeping the directory clean.
        if (index.Items.Count == 0 && Directory.Exists(_contextDir))
        {
            var indexPath = Path.Combine(_contextDir, IndexFileName);
            if (File.Exists(indexPath)) File.Delete(indexPath);
        }
        else
        {
            await SaveIndexAsync(index, ct);
        }
    }

    // Read-only operations

    /// <summary>
    /// Loads the current index, or returns an empty one when no index file exists.
    /// </summary>
    public async Task<ContextIndex> LoadIndexAsync(CancellationToken ct = default)
    {
        var indexPath = Path.Combine(_contextDir, IndexFileName);
        if (!File.Exists(indexPath)) return new ContextIndex();

        try
        {
            var json = await File.ReadAllTextAsync(indexPath, ct);
            return JsonSerializer.Deserialize<ContextIndex>(json, JsonOpts) ?? new ContextIndex();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"[fuseraft] Warning: context index at '{indexPath}' could not be read ({ex.GetType().Name}: {ex.Message}). " +
                "Context items may appear missing. Delete and re-import the affected items to recover.");
            return new ContextIndex();
        }
    }

    /// <summary>
    /// Returns a formatted block suitable for injection into agent system prompts,
    /// listing every context item with its readable path and size.
    /// Returns <c>null</c> when the store is empty.
    /// </summary>
    public async Task<string?> BuildPromptSummaryAsync(CancellationToken ct = default)
    {
        var index = await LoadIndexAsync(ct);
        if (index.Items.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("CONTEXT — reference material imported for this session (use read_file to access):");

        foreach (var (_, item) in index.Items.OrderBy(x => x.Key))
        {
            var desc = item.Description is not null ? $" — {item.Description}" : string.Empty;

            if (item.Files.Count == 1)
            {
                var filePath = $".fuseraft/context/{item.Name}/{item.Files[0].RelativePath}";
                sb.AppendLine($"  [{item.Name}]{desc}: {filePath}");
            }
            else
            {
                sb.AppendLine($"  [{item.Name}]{desc} ({item.Files.Count} files):");
                foreach (var f in item.Files.OrderBy(f => f.RelativePath))
                    sb.AppendLine($"    .fuseraft/context/{item.Name}/{f.RelativePath}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    // Helpers

    private async Task SaveIndexAsync(ContextIndex index, CancellationToken ct)
    {
        Directory.CreateDirectory(_contextDir);
        var indexPath = Path.Combine(_contextDir, IndexFileName);
        await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(index, JsonOpts), ct);
    }

    private static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
}

// DTOs

public sealed class ContextIndex
{
    [JsonPropertyName("items")]
    public Dictionary<string, ContextItem> Items { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ContextItem
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("sourcePath")]
    public string SourcePath { get; init; } = string.Empty;

    [JsonPropertyName("importedAt")]
    public DateTime ImportedAt { get; init; }

    [JsonPropertyName("files")]
    public List<ContextFileEntry> Files { get; init; } = [];
}

public sealed record ContextFileEntry(
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("sizeBytes")]    long   SizeBytes);
