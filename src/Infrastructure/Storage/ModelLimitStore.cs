using System.Collections.Concurrent;
using System.Text.Json;
using fuseraft.Core;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace fuseraft.Infrastructure.Storage;

/// <summary>
/// The input limits learned from providers' model metadata (see <see cref="ModelLimitsClient"/>), kept in memory by
/// model ID for the REPL's context budget and on disk by endpoint and model, so a model is looked up once a week (or
/// once a day while its provider doesn't say) rather than at every start. Lookups are off until
/// <see cref="Enable"/>: only an interactive session asks a provider, never a test or a one-off command.
/// </summary>
public static class ModelLimitStore
{
    internal static readonly TimeSpan KnownFor   = TimeSpan.FromDays(7);
    internal static readonly TimeSpan UnknownFor = TimeSpan.FromDays(1);

    private static readonly ConcurrentDictionary<string, ModelInputLimit> ByModel = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock DiskLock = new();
    private static Func<ModelConfig, CancellationToken, Task<ModelInputLimit?>>? _lookup;
    private static Func<ModelConfig, string>? _endpointOf;

    internal static string CachePath => Path.Combine(FuseraftPaths.GlobalRoot, "cache", "model-limits.json");

    /// <summary>Lets <see cref="Learn"/> ask <paramref name="factory"/>'s providers for limits.</summary>
    public static void Enable(ChatClientFactory factory) =>
        Enable(factory.LookupInputLimitAsync, factory.EndpointOf);

    internal static void Enable(
        Func<ModelConfig, CancellationToken, Task<ModelInputLimit?>> lookup, Func<ModelConfig, string> endpointOf) =>
        (_lookup, _endpointOf) = (lookup, endpointOf);

    internal static void Disable()
    {
        (_lookup, _endpointOf) = (null, null);
        ByModel.Clear();
    }

    /// <summary>The input limit learned for <paramref name="modelId"/> this session, if any.</summary>
    public static ModelInputLimit? Known(string? modelId) =>
        modelId is not null && ByModel.TryGetValue(modelId, out var limit) ? limit : null;

    /// <summary>
    /// Learns <paramref name="config"/>'s input limit from the disk cache or, when that has nothing current, from its
    /// provider (waiting at most <see cref="ModelLimitsClient.Timeout"/>). Returns null when lookups are off or the
    /// provider doesn't say.
    /// </summary>
    public static ModelInputLimit? Learn(ModelConfig config)
    {
        if (_lookup is not { } lookup || _endpointOf is not { } endpointOf || string.IsNullOrEmpty(config.ModelId))
            return null;
        if (Known(config.ModelId) is { } known)
            return known;

        var key = $"{ProviderKeys.Origin(endpointOf(config))}|{config.ModelId}";
        var cache = Load();
        ModelInputLimit? limit;
        if (cache.TryGetValue(key, out var entry)
            && DateTimeOffset.UtcNow - entry.At < (entry.Tokens is null ? UnknownFor : KnownFor))
        {
            limit = entry.Tokens is { } t ? new ModelInputLimit(t, entry.Source ?? "the provider") : null;
        }
        else
        {
            try
            {
                limit = Task.Run(() => lookup(config, CancellationToken.None)).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                limit = null;
            }
            cache[key] = new Entry(limit?.Tokens, limit?.Source, DateTimeOffset.UtcNow);
            Save(cache);
        }

        if (limit is not null)
            ByModel[config.ModelId] = limit;
        return limit;
    }

    private sealed record Entry(int? Tokens, string? Source, DateTimeOffset At);

    private static Dictionary<string, Entry> Load()
    {
        lock (DiskLock)
        {
            try
            {
                return File.Exists(CachePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(CachePath)) ?? []
                    : [];
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    private static void Save(Dictionary<string, Entry> cache)
    {
        lock (DiskLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                var temp = CachePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(cache));
                File.Move(temp, CachePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A cache that can't be written only means looking the model up again next time.
            }
        }
    }
}
