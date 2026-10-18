using System.Text.Json;
using System.Text.Json.Serialization;

namespace fuseraft.Infrastructure.Tools;

/// <summary>
/// Offloads large tool results to disk so they never enter the conversation history verbatim.
/// Each oversized result is written under the session artifacts directory, as a JSON record
/// and as the plain text itself, and the tool's inline result is replaced with a compact stub
/// naming the text file, so the agent can read sections of it or search it with the file tools
/// (which have to be allowed to read the directory — see the REPL's FileSystemPlugin).
/// </summary>
public sealed class ToolResultArtifactStore
{
    private string?                 _artifactsDir;
    private readonly EventEmitter?  _emitter;

    /// <summary>Results larger than this are offloaded. Default: 40,000 chars (~10k tokens).</summary>
    public int ThresholdChars { get; init; } = 40_000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented              = true,
        DefaultIgnoreCondition     = JsonIgnoreCondition.WhenWritingNull,
    };

    public ToolResultArtifactStore(string? artifactsDir, EventEmitter? eventEmitter = null)
    {
        _artifactsDir = artifactsDir;
        _emitter      = eventEmitter;
    }

    /// <summary>
    /// Re-scopes this store to a different session's artifacts directory. Needed because
    /// <c>fuseraft serve</c> builds one orchestrator — and therefore one
    /// <see cref="ToolResultArtifactStore"/> instance — for the whole daemon's lifetime and
    /// reuses it across every dispatched task; without this, offloading stays permanently bound
    /// to (or disabled by) whichever session ID the daemon happened to start with.
    /// </summary>
    public void Rebind(string? artifactsDir) => _artifactsDir = artifactsDir;

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
            // The JSON record holds the content as one escaped string, which line-based reads and searches
            // can't use; this copy is what the stub points the agent at.
            File.WriteAllText(TextPath(id), content);
        }
        catch
        {
            // Best-effort: if the write fails, return content unchanged.
            stub = content;
            return false;
        }

        if (_emitter is not null)
            _ = _emitter.EmitAsync(EventTypes.ArtifactCreated, payload: new
            {
                id,
                tool  = toolName,
                chars = content.Length,
            });

        stub = BuildStub(toolName, hint, content.Length, id, TextPath(id));
        return true;
    }

    private string TextPath(string id) => Path.GetFullPath(Path.Combine(_artifactsDir!, $"{id}.txt"));

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

    private static string BuildStub(string toolName, string hint, int chars, string id, string textPath) =>
        $"[result offloaded — {chars:N0} chars stored to artifact store]\n" +
        $"Tool: {toolName} | {hint}\n" +
        $"Artifact: {id}\n" +
        $"Full result: {textPath}\n" +
        "Read sections of that file with read_file (startLine/maxLines), or search it with grep_file, rather than running the tool again.";
}

internal sealed record ToolResultArtifact
{
    [JsonPropertyName("id")]      public string Id      { get; init; } = "";
    [JsonPropertyName("tool")]    public string Tool    { get; init; } = "";
    [JsonPropertyName("hint")]    public string Hint    { get; init; } = "";
    [JsonPropertyName("chars")]   public int    Chars   { get; init; }
    [JsonPropertyName("content")] public string Content { get; init; } = "";
}
