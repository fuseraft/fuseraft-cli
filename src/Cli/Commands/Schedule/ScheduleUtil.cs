using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using fuseraft.Core.Models;

namespace fuseraft.Cli.Commands.Schedule;

internal static class ScheduleUtil
{
    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithTypeConverter(new DateTimeOffsetConverter())
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithTypeConverter(new DateTimeOffsetConverter())
        .IgnoreUnmatchedProperties()
        .Build();

    public static string Serialize(ScheduledJob job)     => YamlSerializer.Serialize(job);
    public static ScheduledJob? Deserialize(string yaml) => YamlDeserializer.Deserialize<ScheduledJob>(yaml);
    public static string ToSlug(string name)             =>
        System.Text.RegularExpressions.Regex
            .Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-")
            .Trim('-');

    /// <summary>Writes timestamps as one ISO 8601 scalar; also reads the property-bag mapping older versions wrote.</summary>
    // YamlDotNet has no DateTimeOffset support: by default it writes every public property as a
    // mapping and can't read that back, so NextRun always loaded as default and every job looked due.
    private sealed class DateTimeOffsetConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(DateTimeOffset) || type == typeof(DateTimeOffset?);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                if (string.IsNullOrEmpty(scalar.Value) || scalar.Value is "~" or "null")
                    return type == typeof(DateTimeOffset?) ? null : default(DateTimeOffset);
                return DateTimeOffset.Parse(scalar.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }

            parser.Consume<MappingStart>();
            string? utcTicks = null, utcDateTime = null;
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                var key = parser.Consume<Scalar>().Value;
                if (parser.TryConsume<Scalar>(out var value))
                {
                    if (key == "utc_ticks") utcTicks = value.Value;
                    else if (key == "utc_date_time") utcDateTime = value.Value;
                }
                else parser.SkipThisAndNestedEvents();
            }

            if (long.TryParse(utcTicks, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                return new DateTimeOffset(ticks, TimeSpan.Zero);
            if (utcDateTime is not null)
                return DateTimeOffset.Parse(utcDateTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            return type == typeof(DateTimeOffset?) ? null : default(DateTimeOffset);
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
            emitter.Emit(new Scalar(value is DateTimeOffset d
                ? d.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                : "~"));
    }
}
