using System.Text;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

public sealed class BinaryFileSnifferTests : IDisposable
{
    private readonly string _dir;

    public BinaryFileSnifferTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fuseraft_binsniff_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string TempPath(string filename) => Path.Combine(_dir, filename);

    [Fact]
    public void PlainAsciiText_IsNotBinary()
    {
        var path = TempPath("plain.txt");
        File.WriteAllText(path, "using System;\nnamespace Foo { class Bar { } }\n", Encoding.ASCII);

        Assert.False(BinaryFileSniffer.LooksBinary(path));
    }

    [Fact]
    public void NulByteWithNoTextBom_IsBinary()
    {
        var path = TempPath("binary.dll");
        var bytes = new List<byte>();
        bytes.AddRange("MZ"u8.ToArray());
        bytes.Add(0);
        bytes.AddRange(new byte[64]);
        File.WriteAllBytes(path, bytes.ToArray());

        Assert.True(BinaryFileSniffer.LooksBinary(path));
    }

    // Notepad's "Save As," PowerShell ISE, and some Visual Studio templates default to this —
    // it embeds a 0x00 byte after every ASCII character, which a bare NUL-byte scan can't tell
    // apart from binary content. This is the false positive an earlier version of LooksBinary
    // had: it rejected these files outright even though File.ReadAllText reads them correctly.
    [Fact]
    public void Utf16LeTextWithBom_IsNotBinary()
    {
        var path = TempPath("utf16le.txt");
        File.WriteAllText(path, "using System;\nnamespace Foo { class Bar { } }\n", Encoding.Unicode);

        Assert.False(BinaryFileSniffer.LooksBinary(path));
        Assert.Equal("using System;\nnamespace Foo { class Bar { } }\n", File.ReadAllText(path));
    }

    [Fact]
    public void Utf16BeTextWithBom_IsNotBinary()
    {
        var path = TempPath("utf16be.txt");
        File.WriteAllText(path, "using System;\nnamespace Foo { class Bar { } }\n", Encoding.BigEndianUnicode);

        Assert.False(BinaryFileSniffer.LooksBinary(path));
    }

    [Fact]
    public void Utf8TextWithBom_IsNotBinary()
    {
        var path = TempPath("utf8bom.txt");
        File.WriteAllText(path, "using System;\nnamespace Foo { class Bar { } }\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.False(BinaryFileSniffer.LooksBinary(path));
    }

    [Fact]
    public void MissingFile_IsNotBinary()
    {
        // LooksBinary is a sniff, not an existence check — callers already handle
        // File.Exists separately. A missing file should fail open (not flagged), not throw.
        Assert.False(BinaryFileSniffer.LooksBinary(TempPath("ghost.dll")));
    }

    [Fact]
    public void EmptyFile_IsNotBinary()
    {
        var path = TempPath("empty.txt");
        File.WriteAllBytes(path, []);

        Assert.False(BinaryFileSniffer.LooksBinary(path));
    }
}
