using fuseraft.Infrastructure.Context;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Only a read that returned the whole file counts as "read": a cold read the char cap cut short,
/// or a large file's 30-line preview, must not make the next cold read answer "already read —
/// content is in context" for content the model never got.
/// </summary>
public sealed class FileSystemPluginPartialReadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fuseraft-partial-read-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ColdReadCutShortByTheCharCap_IsNotAnsweredWithAlreadyRead()
    {
        var path = Path.Combine(_dir, "Program.cs");
        await File.WriteAllLinesAsync(path, Enumerable.Range(1, 227).Select(i => $"// line {i} " + new string('x', 40)));
        var fs = new FileSystemPlugin(sandboxRoot: _dir, readFileSizeLimit: 4_000);

        Assert.Contains("TRUNCATED", await fs.ReadFileAsync(path));

        var again = await fs.ReadFileAsync(path);
        Assert.DoesNotContain("already read", again);
        Assert.Contains("// line 1 ", again);
    }

    [Fact]
    public async Task LargeFilePreview_IsNotAnsweredWithAlreadyRead_ThisTurnOrLater()
    {
        var path = Path.Combine(_dir, "Model.Designer.cs");
        await File.WriteAllLinesAsync(path, Enumerable.Range(1, 4_500).Select(i => $"// line {i} " + new string('x', 40)));
        var fs = new FileSystemPlugin(sandboxRoot: _dir, sessionCache: new SessionReadCache());

        Assert.Contains("Large file", await fs.ReadFileAsync(path));
        Assert.Contains("Large file", await fs.ReadFileAsync(path));
        ((fuseraft.Infrastructure.Plugins.ITurnResettable)fs).BeginTurn();
        Assert.Contains("Large file", await fs.ReadFileAsync(path));
    }

    [Fact]
    public async Task WholeFileRead_IsStillAnsweredWithAlreadyRead()
    {
        var path = Path.Combine(_dir, "small.cs");
        await File.WriteAllTextAsync(path, "class Small {}");
        var fs = new FileSystemPlugin(sandboxRoot: _dir);

        Assert.Contains("class Small {}", await fs.ReadFileAsync(path));
        Assert.Contains("already read this turn", await fs.ReadFileAsync(path));
    }
}
