using System.Globalization;
using fuseraft.Cli.Commands;
using fuseraft.Core.Models.Config;

namespace FuseraftCli.Tests;

/// <summary>The catalog is what clients edit settings from, so every key in it has to set and read back through <c>settings set</c>.</summary>
public sealed class SettingsCatalogTests
{
    private static string ValidSample(SettingInfo s) => s.Kind switch
    {
        SettingKind.Bool    => s.Default == "true" ? "false" : "true",
        SettingKind.Integer => ((long)(s.Min ?? 1)).ToString(CultureInfo.InvariantCulture),
        SettingKind.Number  => (s.Min ?? 0.5).ToString(CultureInfo.InvariantCulture),
        SettingKind.Choice  => s.Choices![^1],
        SettingKind.List    => "Scratchpad,Http",
        _                   => "http://localhost:4317",
    };

    public static TheoryData<string> Keys() => [.. SettingsCatalog.All.Select(s => s.Key)];

    [Theory]
    [MemberData(nameof(Keys))]
    public void EveryKey_SetsAndReadsBack(string key)
    {
        var info = SettingsCatalog.Find(key)!;
        var config = new UserConfig { Telemetry = new TelemetryConfig() };
        var sample = ValidSample(info);

        Assert.Null(SettingsSetCommand.Apply(config, key, sample));
        Assert.Equal(sample, info.Read(config), StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void EveryClearableKey_ClearsBackToUnset(string key)
    {
        var info = SettingsCatalog.Find(key)!;
        if (!info.Clearable) return;
        var config = new UserConfig { Telemetry = new TelemetryConfig { ServiceName = "x" } };
        Assert.Null(SettingsSetCommand.Apply(config, key, ValidSample(info)));

        Assert.Null(SettingsSetCommand.Apply(config, key, ""));
        Assert.Null(info.Read(config));
    }

    [Fact]
    public void Keys_AreUnique() =>
        Assert.Equal(SettingsCatalog.All.Count, SettingsCatalog.All.Select(s => s.Key.ToLowerInvariant()).Distinct().Count());

    [Fact]
    public void Defaults_LieWithinTheirRange()
    {
        foreach (var s in SettingsCatalog.All.Where(s => s.Kind is SettingKind.Integer or SettingKind.Number && s.Default is not null))
        {
            var d = double.Parse(s.Default!, CultureInfo.InvariantCulture);
            Assert.True(d >= (s.Min ?? double.MinValue) && d <= (s.Max ?? double.MaxValue), s.Key);
        }
    }

    [Fact]
    public void Apply_UnknownKey_IsAnError() =>
        Assert.Contains("Unknown setting key", SettingsSetCommand.Apply(new UserConfig(), "repl.nope", "1"));

    [Fact]
    public void Apply_OutOfRange_IsRejected_AndLeavesTheValue()
    {
        var config = new UserConfig();
        Assert.Contains("from 0 to 2", SettingsSetCommand.Apply(config, "sampling.temperature", "2.5"));
        Assert.Null(config.Sampling.Temperature);
    }

    [Fact]
    public void Apply_ChoiceOutsideTheList_IsRejected() =>
        Assert.Contains("one of: bottom, top", SettingsSetCommand.Apply(new UserConfig(), "repl.dock", "left"));

    [Fact]
    public void Apply_ClearingTheModelId_IsRejected() =>
        Assert.Contains("can't be cleared", SettingsSetCommand.Apply(new UserConfig { ModelId = "m" }, "provider.modelId", ""));

    [Fact]
    public void Apply_ClearingABool_IsRejected() =>
        Assert.Contains("can't be cleared", SettingsSetCommand.Apply(new UserConfig(), "repl.yolo", ""));
}
