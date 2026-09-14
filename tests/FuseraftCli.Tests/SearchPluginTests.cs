using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Tests;

// Regression coverage for a real incident: search_content walked into a legacy .NET project's
// packages\ folder, read a compiled .dll as text (File.ReadAllLines doesn't throw on invalid
// UTF-8 — it substitutes replacement characters), and returned one ~215KB "line" as a single
// match, blowing a session's context from ~10K to ~65K tokens in one tool call.
public class SearchPluginTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuseraft-search-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SearchContent_SkipsBinaryFileEvenWhenItContainsMatchingText()
    {
        var dir = CreateTempDir();
        try
        {
            // Simulate a compiled assembly: readable ASCII text (the kind of symbol name you'd
            // find embedded in a .dll's metadata) surrounded by NUL bytes, which never appear in
            // genuine text files.
            var bytes = new List<byte>();
            bytes.AddRange("header"u8.ToArray());
            bytes.Add(0);
            bytes.Add(0);
            bytes.AddRange("needle-marker"u8.ToArray());
            bytes.Add(0);
            bytes.AddRange(new byte[4000]); // pad well past the binary sniff window

            File.WriteAllBytes(Path.Combine(dir, "vendor.dll"), bytes.ToArray());

            var plugin = new SearchPlugin();
            var result = plugin.SearchContent("needle-marker", dir);

            Assert.Contains("No matches found", result);
            Assert.Contains("unreadable file(s) skipped", result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SearchContent_SkipsPackagesDirectoryByDefault()
    {
        var dir = CreateTempDir();
        try
        {
            var packagesDir = Path.Combine(dir, "packages", "SomeLib.1.0.0", "lib");
            Directory.CreateDirectory(packagesDir);
            File.WriteAllText(Path.Combine(packagesDir, "notes.txt"), "needle-marker appears here");

            var plugin = new SearchPlugin();
            var result = plugin.SearchContent("needle-marker", dir);

            Assert.Contains("No matches found", result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SearchContent_TruncatesAbnormallyLongMatchLine()
    {
        var dir = CreateTempDir();
        try
        {
            var longLine = "needle-marker " + new string('x', 5000);
            File.WriteAllText(Path.Combine(dir, "data.txt"), longLine);

            var plugin = new SearchPlugin();
            var result = plugin.SearchContent("needle-marker", dir);

            Assert.Contains("[RESULTS]", result);
            Assert.Contains("chars truncated", result);
            Assert.True(result.Length < 1000, $"Expected a bounded result, got {result.Length} chars.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
