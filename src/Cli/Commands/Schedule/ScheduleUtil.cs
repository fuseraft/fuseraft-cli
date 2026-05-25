using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using fuseraft.Core.Models;

namespace fuseraft.Cli.Commands.Schedule;

internal static class ScheduleUtil
{
    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static string Serialize(ScheduledJob job)     => YamlSerializer.Serialize(job);
    public static ScheduledJob? Deserialize(string yaml) => YamlDeserializer.Deserialize<ScheduledJob>(yaml);
    public static string ToSlug(string name)             =>
        System.Text.RegularExpressions.Regex
            .Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-")
            .Trim('-');
}
