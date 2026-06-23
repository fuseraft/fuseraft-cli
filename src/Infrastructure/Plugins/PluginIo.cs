using System.Text.Json;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Small shared helper for plugins that write exactly one fixed-path JSON artifact
/// (<see cref="ReconPlugin"/>, <see cref="PreflightPlugin"/>) — creates the parent directory
/// if missing and serializes with the caller-supplied options.
/// </summary>
internal static class PluginIo
{
    public static async Task<string> WriteJsonAsync<T>(string path, T value, JsonSerializerOptions options)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, options));
        return PluginResult.Ok($"Wrote {Path.GetFileName(path)} → {path}");
    }
}
