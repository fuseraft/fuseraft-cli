using fuseraft.Cli.Tui;

namespace FuseraftCli.Tests;

public sealed class UnifiedDiffTests
{
    [Fact]
    public void NewFile_IsAllAdded()
    {
        var d = UnifiedDiff.Build("", "a\nb\n");

        Assert.Equal(2, d.Added);
        Assert.Equal(0, d.Removed);
        Assert.Equal("@@ -0,0 +1,2 @@\n+a\n+b\n", d.Text);
    }

    [Fact]
    public void Change_ShowsContextAndCounts()
    {
        var before = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line{i}")) + "\n";
        var after = before.Replace("line10\n", "LINE10\n");

        var d = UnifiedDiff.Build(before, after);

        Assert.Equal((1, 1), (d.Added, d.Removed));
        Assert.StartsWith("@@ -7,7 +7,7 @@\n line7\n", d.Text);
        Assert.Contains("-line10\n+LINE10\n", d.Text);
        Assert.DoesNotContain("line3\n", d.Text);
        Assert.DoesNotContain("line14", d.Text);
    }

    [Fact]
    public void DistantChanges_AreSeparateHunks()
    {
        var before = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"l{i}")) + "\n";
        var after = before.Replace("l2\n", "X\n").Replace("l35\n", "Y\n");

        Assert.Equal(2, UnifiedDiff.Build(before, after).Text.Split("@@ -").Length - 1);
    }
}
