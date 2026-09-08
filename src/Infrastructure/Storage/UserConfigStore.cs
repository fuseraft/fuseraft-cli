using System.Text.Json;
using System.Text.Json.Serialization;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Config;

namespace fuseraft.Infrastructure.Storage;

public static class UserConfigStore
{
    private static string ConfigDir => FuseraftPaths.GlobalRoot;

    public static string ConfigPath => FuseraftPaths.GlobalConfig;

    // Pre-fold-in location of MCP servers saved by the REPL's /mcp add wizard. Migrated into
    // the mcpServers section of ConfigPath the first time Load() runs after an upgrade — see
    // MigrateLegacyMcpServers below. Kept private: nothing outside this file should read or
    // write it directly any more (ReplMcpServerStore delegates through Load()/Save() instead).
    private static string LegacyMcpServersPath => Path.Combine(FuseraftPaths.GlobalRoot, "repl-mcp-servers.json");

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
            var json = File.ReadAllText(ConfigPath);
            var needsRewrite = false;
            string? onDiskApiKey;
            UserConfig config;

            using (var doc = JsonDocument.Parse(json))
            {
                // Shape detection: the old flat file has a top-level "provider" *string*
                // (e.g. "openai"); the current nested file has a "provider" *object*. Anything
                // else (missing entirely, or already an object) is treated as the current shape.
                var isLegacyShape = doc.RootElement.TryGetProperty("provider", out var providerEl)
                    && providerEl.ValueKind == JsonValueKind.String;

                if (isLegacyShape)
                {
                    var legacy = JsonSerializer.Deserialize<LegacyOnDiskConfig>(json, JsonOptions);
                    if (legacy is null) return (null, legacyKeyFile);

                    config = new UserConfig
                    {
                        ModelId       = legacy.ModelId      ?? string.Empty,
                        Endpoint      = legacy.Endpoint     ?? string.Empty,
                        Provider      = legacy.Provider     ?? string.Empty,
                        ApiKeyEnvVar  = legacy.ApiKeyEnvVar ?? string.Empty,
                        SkillCuration = legacy.SkillCuration,
                        Repl          = new ReplDefaultsConfig { ContextBudget = legacy.ReplContextBudget },
                    };
                    onDiskApiKey = legacy.ApiKey;
                    needsRewrite = true;
                }
                else
                {
                    var onDisk = JsonSerializer.Deserialize<OnDiskConfig>(json, JsonOptions);
                    if (onDisk is null) return (null, legacyKeyFile);

                    config = new UserConfig
                    {
                        ModelId       = onDisk.Provider?.ModelId      ?? string.Empty,
                        Endpoint      = onDisk.Provider?.Endpoint     ?? string.Empty,
                        Provider      = onDisk.Provider?.Type         ?? string.Empty,
                        ApiKeyEnvVar  = onDisk.Provider?.ApiKeyEnvVar ?? string.Empty,
                        Sampling      = onDisk.Sampling      ?? new SamplingDefaultsConfig(),
                        Repl          = onDisk.Repl          ?? new ReplDefaultsConfig(),
                        McpServers    = onDisk.McpServers    ?? [],
                        Telemetry     = onDisk.Telemetry,
                        SkillCuration = onDisk.SkillCuration,
                    };
                    onDiskApiKey = null;
                }
            }

            if (MigrateLegacyMcpServers(config))
                needsRewrite = true;

            if (needsRewrite)
                Save(config);

            return (config, onDiskApiKey ?? legacyKeyFile);
        }
        catch
        {
            return (null, legacyKeyFile);
        }
    }

    // Folds a standalone repl-mcp-servers.json (pre-fold-in versions) into config.McpServers
    // and deletes the old file. Best-effort — matches ConsumeLegacyKeyFile's "read once, delete
    // unconditionally" approach for the old plaintext .key file. Returns true if it changed
    // config.McpServers (i.e. the caller needs to persist the migration).
    private static bool MigrateLegacyMcpServers(UserConfig config)
    {
        if (config.McpServers.Count > 0) return false;
        if (!File.Exists(LegacyMcpServersPath)) return false;

        var migrated = false;
        try
        {
            var servers = JsonSerializer.Deserialize<List<McpServerConfig>>(File.ReadAllText(LegacyMcpServersPath), JsonOptions);
            if (servers is { Count: > 0 })
            {
                config.McpServers = servers;
                migrated = true;
            }
        }
        catch { /* best-effort read */ }
        try { File.Delete(LegacyMcpServersPath); } catch { /* best-effort delete */ }

        return migrated;
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
            Provider = new ProviderSection
            {
                ModelId      = config.ModelId,
                Endpoint     = config.Endpoint,
                Type         = config.Provider,
                ApiKeyEnvVar = config.ApiKeyEnvVar,
            },
            Sampling      = config.Sampling,
            Repl          = config.Repl,
            McpServers    = config.McpServers,
            Telemetry     = config.Telemetry,
            SkillCuration = config.SkillCuration,
        };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(onDisk, JsonOptions));
    }

    // Private DTOs — used only for reading/writing the JSON file.

    private sealed class OnDiskConfig
    {
        [JsonPropertyName("provider")]
        public ProviderSection? Provider { get; set; }

        [JsonPropertyName("sampling")]
        public SamplingDefaultsConfig? Sampling { get; set; }

        [JsonPropertyName("repl")]
        public ReplDefaultsConfig? Repl { get; set; }

        [JsonPropertyName("mcpServers")]
        public List<McpServerConfig>? McpServers { get; set; }

        [JsonPropertyName("telemetry")]
        public TelemetryConfig? Telemetry { get; set; }

        [JsonPropertyName("skillCuration")]
        public SkillCurationConfig? SkillCuration { get; set; }
    }

    private sealed class ProviderSection
    {
        [JsonPropertyName("modelId")]
        public string? ModelId { get; set; }

        [JsonPropertyName("endpoint")]
        public string? Endpoint { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("apiKeyEnvVar")]
        public string? ApiKeyEnvVar { get; set; }
    }

    // Mirrors the pre-sectioning flat file exactly. Used only to migrate an existing config on
    // its first Load() after upgrading — never written.
    private sealed class LegacyOnDiskConfig
    {
        [JsonPropertyName("modelId")]
        public string? ModelId { get; set; }

        [JsonPropertyName("endpoint")]
        public string? Endpoint { get; set; }

        [JsonPropertyName("provider")]
        public string? Provider { get; set; }

        [JsonPropertyName("apiKeyEnvVar")]
        public string? ApiKeyEnvVar { get; set; }

        [JsonPropertyName("skillCuration")]
        public SkillCurationConfig? SkillCuration { get; set; }

        [JsonPropertyName("replContextBudget")]
        public int? ReplContextBudget { get; set; }

        // Present only in configs created before keychain support was added.
        [JsonPropertyName("apiKey")]
        public string? ApiKey { get; set; }
    }
}
