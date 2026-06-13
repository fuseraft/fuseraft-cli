using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using fuseraft.Core;

namespace fuseraft.Infrastructure.Util;

public static class CrashDumper
{
    private static string DumpDir => FuseraftPaths.GlobalCrashDumps;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented            = true,
        DefaultIgnoreCondition   = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Serializes the exception and environment info to ~/.fuseraft/crashdump/{id}.json.
    /// Returns the full path written.
    /// </summary>
    public static string Write(Exception ex, string[] commandArgs)
    {
        var id   = GenerateId();
        var path = Path.Combine(DumpDir, $"{id}.json");

        Directory.CreateDirectory(DumpDir);

        var dump = new CrashReport
        {
            SessionId   = id,
            Timestamp   = DateTimeOffset.UtcNow.ToString("o"),
            Command     = commandArgs,
            Os          = RuntimeInformation.OSDescription,
            Runtime     = RuntimeInformation.FrameworkDescription,
            AppVersion  = typeof(CrashDumper).Assembly
                              .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                              ?.InformationalVersion ?? "unknown",
            Exception   = BuildExceptionInfo(ex),
        };

        File.WriteAllText(path, JsonSerializer.Serialize(dump, JsonOptions));
        return path;
    }

    private static ExceptionInfo BuildExceptionInfo(Exception? ex)
    {
        if (ex is null) return new ExceptionInfo();

        var info = new ExceptionInfo
        {
            Type       = ex.GetType().FullName ?? ex.GetType().Name,
            Message    = ex.Message,
            StackTrace = ex.StackTrace,
            Inner      = ex.InnerException is not null ? BuildExceptionInfo(ex.InnerException) : null,
        };

        // Unpack AggregateException's flattened inner exceptions list.
        if (ex is AggregateException agg && agg.InnerExceptions.Count > 1)
            info.AggregateInners = agg.InnerExceptions.Select(BuildExceptionInfo).ToList();

        return info;
    }

    private static string GenerateId()
    {
        var bytes = new byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    // DTO types used only for JSON serialization.

    private sealed class CrashReport
    {
        [JsonPropertyName("session_id")]   public string          SessionId   { get; set; } = string.Empty;
        [JsonPropertyName("timestamp")]    public string          Timestamp   { get; set; } = string.Empty;
        [JsonPropertyName("command")]      public string[]        Command     { get; set; } = [];
        [JsonPropertyName("os")]           public string          Os          { get; set; } = string.Empty;
        [JsonPropertyName("runtime")]      public string          Runtime     { get; set; } = string.Empty;
        [JsonPropertyName("app_version")] public string          AppVersion  { get; set; } = string.Empty;
        [JsonPropertyName("exception")]    public ExceptionInfo   Exception   { get; set; } = new();
    }

    private sealed class ExceptionInfo
    {
        [JsonPropertyName("type")]               public string?            Type             { get; set; }
        [JsonPropertyName("message")]            public string?            Message          { get; set; }
        [JsonPropertyName("stack_trace")]        public string?            StackTrace       { get; set; }
        [JsonPropertyName("inner")]              public ExceptionInfo?     Inner            { get; set; }
        [JsonPropertyName("aggregate_inners")]   public List<ExceptionInfo>? AggregateInners { get; set; }
    }
}
