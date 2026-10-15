using fuseraft.Cli.Tui;

namespace FuseraftCli.Tests;

public sealed class FileIndexTests
{
    static readonly string[] Files = ["README.md", "src/Cli/Tui.cs", "src/Core/Agent.cs", "src/main.py"];

    [Fact]
    public void Complete_offers_the_next_level_down_first() =>
        Assert.Equal(["README.md", "src/"], FileIndex.Complete(Files, ""));

    [Fact]
    public void Complete_then_files_anywhere_whose_name_starts_with_it() =>
        Assert.Equal(["src/Cli/Tui.cs"], FileIndex.Complete(Files, "tui"));

    [Fact]
    public void Complete_leaves_out_what_is_already_typed() =>
        Assert.Empty(FileIndex.Complete(Files, "README.md"));

    [Fact]
    public async Task The_index_leaves_out_denied_files()
    {
        var root = Directory.CreateTempSubdirectory("fuseraft-index-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, ".env"), "SECRET=1");
            File.WriteAllText(Path.Combine(root, "app.py"), "");
            var index = new FileIndex(root, path => path == ".env").Warm();
            for (var i = 0; i < 100 && index.Complete("").Count == 0; i++)
                await Task.Delay(20);

            Assert.Equal(["app.py"], index.Complete(""));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
