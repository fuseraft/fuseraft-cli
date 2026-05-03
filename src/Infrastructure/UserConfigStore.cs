using System.Text.Json;
using System.Text.Json.Serialization;
using fuseraft.Core;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure;

public static class UserConfigStore
{
    private static string ConfigDir => FuseraftPaths.GlobalRoot;

    public static string ConfigPath => FuseraftPaths.GlobalConfig;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    // Returns the UserConfig and any API key found in a legacy plain-text config.
    // Callers are responsible for migrating a non-null legacy key to the keychain.
    public static (UserConfig? Config, string? LegacyKey) Load()
    {
        if (!File.Exists(ConfigPath)) return (null, null);
        try
        {
            var json    = File.ReadAllText(ConfigPath);
            var onDisk  = JsonSerializer.Deserialize<OnDiskConfig>(json, JsonOptions);
            if (onDisk is null) return (null, null);

            var config = new UserConfig
            {
                ModelId  = onDisk.ModelId  ?? string.Empty,
                Endpoint = onDisk.Endpoint ?? string.Empty,
                Provider = onDisk.Provider ?? string.Empty,
            };
            return (config, onDisk.ApiKey);
        }
        catch
        {
            return (null, null);
        }
    }

    // Saves only the non-secret fields. The API key is managed by the keychain.
    public static void Save(UserConfig config)
    {
        Directory.CreateDirectory(ConfigDir);
        var onDisk = new OnDiskConfig
        {
            ModelId  = config.ModelId,
            Endpoint = config.Endpoint,
            Provider = config.Provider,
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

        // Present only in configs created before keychain support was added.
        [JsonPropertyName("apiKey")]
        public string? ApiKey { get; set; }
    }
}
