using fuseraft.Cli.Display;

namespace FuseraftCli.Tests;

public sealed class DisplayPathTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "proj");

    [Fact]
    public void A_path_under_the_root_is_shown_relative_to_it()
    {
        Assert.Equal(Path.Combine("src", "Foo.cs"), DisplayPath.Relative(Path.Combine(Root, "src", "Foo.cs"), Root));
    }

    [Fact]
    public void A_sibling_sharing_the_roots_name_as_a_prefix_stays_absolute()
    {
        var sibling = Path.Combine(Path.GetTempPath(), "project", "Foo.cs");

        Assert.Equal(sibling, DisplayPath.Relative(sibling, Root));
    }

    [Fact]
    public void The_root_itself_and_paths_outside_it_are_unchanged()
    {
        var outside = Path.Combine(Path.GetTempPath(), "other", "Foo.cs");

        Assert.Equal(Root, DisplayPath.Relative(Root, Root));
        Assert.Equal(outside, DisplayPath.Relative(outside, Root));
    }
}
