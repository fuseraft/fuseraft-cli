using System.Text.Json;
using fuseraft.Cli.Commands;
using fuseraft.Cli.Protocol;
using fuseraft.Core;
using fuseraft.Infrastructure.Storage;
using Spectre.Console.Cli;

namespace FuseraftCli.Tests;

/// <summary><c>settings show --json</c> and <c>settings set --json</c>: the envelopes the VS Code settings panel reads.</summary>
[Collection("FuseraftHomeEnv")]
public sealed class SettingsJsonTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");
    private readonly TextWriter _originalWriter = WireProtocol.Writer;
    private readonly StringWriter _out = new();

    public SettingsJsonTests()
    {
        Directory.CreateDirectory(_tempHome);
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);
        WireProtocol.Writer = _out;
    }

    public void Dispose()
    {
        WireProtocol.Writer = _originalWriter;
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
    }

    private Task<int> ShowAsync() =>
        ((ICommand<SettingsShowSettings>)new SettingsShowCommand())
            .ExecuteAsync(null!, new SettingsShowSettings { Json = true }, CancellationToken.None);

    private Task<int> SetAsync(string key, string value) =>
        ((ICommand<SettingsSetSettings>)new SettingsSetCommand())
            .ExecuteAsync(null!, new SettingsSetSettings { Key = key, Value = value, Json = true }, CancellationToken.None);

    private JsonElement Envelope() => JsonDocument.Parse(_out.ToString().Trim().Split('\n')[^1]).RootElement;

    private static JsonElement Item(JsonElement items, string key) =>
        items.EnumerateArray().Single(i => i.GetProperty("key").GetString() == key);

    [Fact]
    public async Task Show_WithNoConfig_ListsEveryKeyUnset()
    {
        Assert.Equal(0, await ShowAsync());

        var items = Envelope().GetProperty("items");
        Assert.Equal(SettingsCatalog.All.Count, items.GetArrayLength());
        var temperature = Item(items, "sampling.temperature");
        Assert.Equal("number", temperature.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, temperature.GetProperty("value").ValueKind);
        Assert.Equal(2, temperature.GetProperty("max").GetDouble());
        Assert.Equal(new[] { "bottom", "top" }, Item(items, "repl.dock").GetProperty("choices").EnumerateArray().Select(c => c.GetString()));
        Assert.False(Item(items, "repl.yolo").GetProperty("clearable").GetBoolean());
    }

    [Fact]
    public async Task Set_ReturnsTheUpdatedSetting_AndShowSeesIt()
    {
        Assert.Equal(0, await SetAsync("sampling.temperature", "0.7"));
        var item = Envelope().GetProperty("item");
        Assert.Equal("sampling.temperature", item.GetProperty("key").GetString());
        Assert.Equal("0.7", item.GetProperty("value").GetString());

        Assert.Equal(0, await ShowAsync());
        Assert.Equal("0.7", Item(Envelope().GetProperty("items"), "sampling.temperature").GetProperty("value").GetString());
    }

    [Fact]
    public async Task Set_InvalidValue_IsAnErrorEnvelope_AndWritesNothing()
    {
        Assert.Equal(1, await SetAsync("repl.maxStreamRetries", "9"));

        Assert.Contains("from 0 to 5", Envelope().GetProperty("error").GetString());
        Assert.False(File.Exists(UserConfigStore.ConfigPath));
    }

    [Fact]
    public async Task Set_ApiKey_IsRefused() =>
        Assert.Equal(1, await SetAsync("provider.apiKey", "sk-test"));
}
