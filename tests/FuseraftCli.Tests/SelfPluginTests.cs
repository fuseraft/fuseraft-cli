using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="SelfPlugin"/> — an agent's read-only introspection into its own
/// actually-resolved tool list, so it can check a capability claim against ground truth
/// instead of reasoning about it from memory or trusting another agent's notes.
/// </summary>
public sealed class SelfPluginTests
{
    private static SelfPlugin Make(params string[] toolNames) =>
        new(new HashSet<string>(toolNames, StringComparer.Ordinal));

    [Fact]
    public async Task HasCapability_TrueForToolInSet()
    {
        var plugin = Make("read_file", "patch_file", "write_file");

        Assert.Equal("true", await plugin.HasCapabilityAsync("patch_file"));
    }

    [Fact]
    public async Task HasCapability_FalseForToolNotInSet()
    {
        var plugin = Make("read_file", "list_files");

        Assert.Equal("false", await plugin.HasCapabilityAsync("patch_file"));
    }

    [Fact]
    public async Task HasCapability_IsCaseSensitive()
    {
        // Tool names are exact identifiers from the function-calling schema — deliberately
        // not case-insensitive, so a near-miss doesn't silently report a false "true".
        var plugin = Make("patch_file");

        Assert.Equal("false", await plugin.HasCapabilityAsync("Patch_File"));
    }

    [Fact]
    public async Task ListCapabilities_ReturnsAllNamesSorted()
    {
        var plugin = Make("write_file", "patch_file", "read_file");

        var result = await plugin.ListCapabilitiesAsync();

        Assert.Equal("patch_file, read_file, write_file", result);
    }

    [Fact]
    public async Task ListCapabilities_EmptySetReturnsEmptyString()
    {
        var plugin = Make();

        Assert.Equal(string.Empty, await plugin.ListCapabilitiesAsync());
    }
}
