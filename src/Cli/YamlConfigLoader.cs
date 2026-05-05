using System.Text;
using Microsoft.Extensions.Configuration;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace fuseraft.Cli;

/// <summary>
/// Converts YAML orchestration configs into an <see cref="IConfiguration"/> so that
/// <see cref="OrchestratorBuilder"/> can bind them with the same code path used for JSON.
/// </summary>
internal static class YamlConfigLoader
{
    /// <summary>
    /// Returns true if <paramref name="path"/> has a .yaml or .yml extension.
    /// </summary>
    internal static bool IsYamlPath(string path) =>
        path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".yml",  StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Loads a YAML config file and returns an <see cref="IConfiguration"/> backed by
    /// its content. The YAML must have a top-level <c>Orchestration:</c> key that
    /// mirrors the JSON schema expected by <c>BindConfig</c>.
    /// </summary>
    internal static IConfiguration LoadAsConfiguration(string yamlPath)
    {
        var yaml = File.ReadAllText(yamlPath);
        var json = ConvertYamlToJson(yaml);

        return new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
    }

    /// <summary>
    /// Parses <paramref name="yamlContent"/> and throws
    /// <see cref="YamlDotNet.Core.YamlException"/> if the syntax is invalid.
    /// </summary>
    internal static void ValidateSyntax(string yamlContent)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .Build();

        // Deserialising to object is sufficient to surface parse errors.
        deserializer.Deserialize<object>(yamlContent);
    }

    // ---------------------------------------------------------------------------
    // Internal helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Converts a YAML string to a JSON string by round-tripping through YamlDotNet's
    /// object graph and the built-in JSON-compatible serializer.
    /// </summary>
    internal static string ConvertYamlToJson(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .WithAttemptingUnquotedStringTypeDeserialization()
            .Build();

        var graph = deserializer.Deserialize<object>(yaml);

        var serializer = new SerializerBuilder()
            .JsonCompatible()
            .Build();

        return serializer.Serialize(graph);
    }
}
