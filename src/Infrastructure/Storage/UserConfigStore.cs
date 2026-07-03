using System.Text.Json;
using System.Text.Json.Serialization;
using fuseraft.Core;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure.Storage;

public static class UserConfigStore
{
    private static string ConfigDir => FuseraftPaths.GlobalRoot;

    public static string ConfigPath => FuseraftPaths.GlobalConfig;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    // Returns the UserConfig and any API key found in a legacy plain-text location (the old
    // "apiKey" config field, or a leftover ~/.fuseraft/.key file from a fuseraft version that
    // still had the plain-text keychain fallback). Callers are responsible for migrating a
    // non-null legacy key to the keychain.
    public static (UserConfig? Config, string? LegacyKey) Load()
    {
        var legacyKeyFile = ConsumeLegacyKeyFile();

        if (!File.Exists(ConfigPath)) return (null, legacyKeyFile);
        try
        {
            var json    = File.ReadAllText(ConfigPath);
            var onDisk  = JsonSerializer.Deserialize<OnDiskConfig>(json, JsonOptions);
            if (onDisk is null) return (null, legacyKeyFile);

            var config = new UserConfig
            {
                ModelId      = onDisk.ModelId      ?? string.Empty,
                Endpoint     = onDisk.Endpoint     ?? string.Empty,
                Provider     = onDisk.Provider     ?? string.Empty,
                ApiKeyEnvVar = onDisk.ApiKeyEnvVar ?? string.Empty,
            };
            return (config, onDisk.ApiKey ?? legacyKeyFile);
        }
        catch
        {
            return (null, legacyKeyFile);
        }
    }

    // Reads and unconditionally deletes ~/.fuseraft/.key, the plain-text fallback file
    // written by fuseraft versions predating the keychain-only policy. Runs on every Load()
    // so any leftover plaintext key is scrubbed from disk on the next command, regardless of
    // whether the caller manages to migrate it into an OS keychain.
    private static string? ConsumeLegacyKeyFile()
    {
        var path = FuseraftPaths.GlobalKeyFile;
        if (!File.Exists(path)) return null;
        string? key = null;
        try { key = File.ReadAllText(path).Trim(); } catch { /* best-effort read */ }
        try { File.Delete(path); } catch { /* best-effort delete */ }
        return string.IsNullOrEmpty(key) ? null : key;
    }

    // Saves only the non-secret fields. The API key is managed by the keychain.
    public static void Save(UserConfig config)
    {
        Directory.CreateDirectory(ConfigDir);
        var onDisk = new OnDiskConfig
        {
            ModelId      = config.ModelId,
            Endpoint     = config.Endpoint,
            Provider     = config.Provider,
            ApiKeyEnvVar = config.ApiKeyEnvVar,
        };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(onDisk, JsonOptions));
    }

    // Private DTO — used only for reading/writing the JSON file.
    // ApiKey is included so we can detect and migrate old plain-text configs.
    private sealed class OnDiskConfig
    {
        [JsonPropertyName("modelId")]
        public string? ModelId { get; set; }

        [JsonPropertyName("endpoint")]
        public string? Endpoint { get; set; }

        [JsonPropertyName("provider")]
        public string? Provider { get; set; }

        [JsonPropertyName("apiKeyEnvVar")]
        public string? ApiKeyEnvVar { get; set; }

        // Present only in configs created before keychain support was added.
        [JsonPropertyName("apiKey")]
        public string? ApiKey { get; set; }
    }
}
