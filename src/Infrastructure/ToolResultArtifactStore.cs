using System.Text.Json;
using System.Text.Json.Serialization;

namespace fuseraft.Infrastructure;

/// <summary>
/// Offloads large tool results to disk so they never enter the conversation history verbatim.
/// Each oversized result is written as a JSON file under the session artifacts directory;
/// the tool's inline result is replaced with a compact stub that tells the agent how to
/// access specific sections via targeted tools.
/// </summary>
public sealed class ToolResultArtifactStore
{
    private readonly string? _artifactsDir;

    /// <summary>Results larger than this are offloaded. Default: 40,000 chars (~10k tokens).</summary>
    public int ThresholdChars { get; init; } = 40_000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented              = true,
        DefaultIgnoreCondition     = JsonIgnoreCondition.WhenWritingNull,
    };

    public ToolResultArtifactStore(string? artifactsDir)
        => _artifactsDir = artifactsDir;

    /// <summary>
    /// If <paramref name="content"/> exceeds <see cref="ThresholdChars"/>, writes it to disk
    /// and returns <c>true</c> with a compact reference <paramref name="stub"/>. Otherwise
    /// returns <c>false</c> and <paramref name="stub"/> is set to <paramref name="content"/>.
    /// </summary>
    public bool TryOffload(string toolName, string hint, string content, out string stub)
    {
        if (_artifactsDir is null || content.Length <= ThresholdChars)
        {
            stub = content;
            return false;
        }

        var id = Guid.NewGuid().ToString("N")[..12];
        try
        {
            Directory.CreateDirectory(_artifactsDir);
            File.WriteAllText(
                Path.Combine(_artifactsDir, $"{id}.json"),
                JsonSerializer.Serialize(new ToolResultArtifact
                {
                    Id      = id,
                    Tool    = toolName,
                    Hint    = hint,
                    Chars   = content.Length,
                    Content = content,
                }, JsonOpts));
        }
        catch
        {
            // Best-effort: if the write fails, return content unchanged.
            stub = content;
            return false;
        }

        stub = BuildStub(toolName, hint, content.Length, id);
        return true;
    }

    /// <summary>Loads artifact content by ID. Returns null if not found.</summary>
    public string? TryResolve(string id)
    {
        if (_artifactsDir is null) return null;
        var path = Path.Combine(_artifactsDir, $"{id}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var artifact = JsonSerializer.Deserialize<ToolResultArtifact>(
                File.ReadAllText(path), JsonOpts);
            return artifact?.Content;
        }
        catch { return null; }
    }

    private static string BuildStub(string toolName, string hint, int chars, string id) =>
        $"[result offloaded — {chars:N0} chars stored to artifact store]\n" +
        $"Tool: {toolName} | {hint}\n" +
        $"Artifact: {id}\n" +
        "Use targeted tools (e.g. read_file with startLine/maxLines, or grep_in_file) for specific sections.";
}

internal sealed record ToolResultArtifact
{
    [JsonPropertyName("id")]      public string Id      { get; init; } = "";
    [JsonPropertyName("tool")]    public string Tool    { get; init; } = "";
    [JsonPropertyName("hint")]    public string Hint    { get; init; } = "";
    [JsonPropertyName("chars")]   public int    Chars   { get; init; }
    [JsonPropertyName("content")] public string Content { get; init; } = "";
}
