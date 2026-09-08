using System.Text.Json.Serialization;

namespace fuseraft.Core.Models.Config;

/// <summary>
/// Persisted default sampling parameters applied to every new REPL session.
/// A null value means "use the model/provider's own default." The REPL's
/// <c>/temperature</c>, <c>/top-p</c>, and <c>/seed</c> commands override these for the
/// current session only — they never write back here.
/// </summary>
public sealed class SamplingDefaultsConfig
{
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("topP")]
    public double? TopP { get; set; }

    [JsonPropertyName("seed")]
    public long? Seed { get; set; }

    /// <summary>0 or null means "use the provider default" (see <c>ReplSessionContext.MaxOutputTokens</c>).</summary>
    [JsonPropertyName("maxOutputTokens")]
    public int? MaxOutputTokens { get; set; }
}
