using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Tests;

public class DirectoryFiltersTests
{
    [Fact]
    public void PathWithExcludedDirectoryBelowRoot_IsExcluded()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");
        var path = Path.Combine(root, "src", "node_modules", "package", "index.js");

        Assert.True(DirectoryFilters.IsExcluded(path, root));
    }

    [Fact]
    public void ExcludedDirectoryRelativeToRoot_IsExcluded()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");
        var path = Path.Combine(root, "node_modules");

        Assert.True(DirectoryFilters.IsExcluded(path, root));
    }

    [Fact]
    public void NormalPathBelowRoot_IsNotExcluded()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");
        var path = Path.Combine(root, "src", "Infrastructure", "Plugins", "DirectoryFilters.cs");

        Assert.False(DirectoryFilters.IsExcluded(path, root));
    }

    [Fact]
    public void RootInsideExcludedLookingParent_DoesNotExcludeDescendants()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo", "vendor");
        var path = Path.Combine(root, "pkg", "file.cs");

        Assert.False(DirectoryFilters.IsExcluded(path, root));
    }

    [Fact]
    public void CustomExcludedDirectories_OverrideDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");
        var customExcluded = new[] { "generated" };

        Assert.True(DirectoryFilters.IsExcluded(
            Path.Combine(root, "src", "generated", "output.cs"),
            root,
            customExcluded));
        Assert.False(DirectoryFilters.IsExcluded(
            Path.Combine(root, "node_modules", "package", "index.js"),
            root,
            customExcluded));
    }

    [Fact]
    public void ExcludedDirectoryComparison_MatchesPlatformCaseSensitivity()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");
        var path = Path.Combine(root, "src", "NODE_MODULES", "package", "index.js");

        Assert.Equal(OperatingSystem.IsWindows(), DirectoryFilters.IsExcluded(path, root));
    }
}
