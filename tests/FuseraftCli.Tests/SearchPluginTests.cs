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
    public void SearchContent_DirectoryOutsideSandbox_ReturnsDenial()
    {
        var sandboxDir = CreateTempDir();
        var outsideDir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(outsideDir, "secret.txt"), "needle-marker");

            var plugin = new SearchPlugin(sandboxRoot: sandboxDir);
            var result = plugin.SearchContent("needle-marker", outsideDir);

            Assert.StartsWith("[DENIED]", result);
        }
        finally
        {
            Directory.Delete(sandboxDir, recursive: true);
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Fact]
    public void SearchCallers_DirectoryOutsideSandbox_ReturnsDenial()
    {
        var sandboxDir = CreateTempDir();
        var outsideDir = CreateTempDir();
        try
        {
            var plugin = new SearchPlugin(sandboxRoot: sandboxDir);
            var result = plugin.SearchCallers("Widget", outsideDir);

            Assert.StartsWith("[DENIED]", result);
        }
        finally
        {
            Directory.Delete(sandboxDir, recursive: true);
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Fact]
    public void SearchSymbol_DirectoryOutsideSandbox_ReturnsDenial()
    {
        var sandboxDir = CreateTempDir();
        var outsideDir = CreateTempDir();
        try
        {
            var plugin = new SearchPlugin(sandboxRoot: sandboxDir);
            var result = plugin.SearchSymbol("Widget", outsideDir);

            Assert.StartsWith("[DENIED]", result);
        }
        finally
        {
            Directory.Delete(sandboxDir, recursive: true);
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Fact]
    public void SearchContent_DirectoryUnderIncludedRoot_IsAllowed()
    {
        var sandboxDir = CreateTempDir();
        var includedDir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(includedDir, "other.txt"), "needle-marker");

            var included = new IncludedRootsState([includedDir]);
            var plugin = new SearchPlugin(sandboxRoot: sandboxDir, includedRoots: included);
            var result = plugin.SearchContent("needle-marker", includedDir);

            Assert.Contains("[RESULTS]", result);
            Assert.Contains("needle-marker", result);
        }
        finally
        {
            Directory.Delete(sandboxDir, recursive: true);
            Directory.Delete(includedDir, recursive: true);
        }
    }

    [Fact]
    public void SearchContent_DefaultDirectory_ResolvesAgainstSandboxRoot_NotProcessCwd()
    {
        var sandboxDir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(sandboxDir, "here.txt"), "needle-marker");

            var plugin = new SearchPlugin(sandboxRoot: sandboxDir);
            // No 'directory' argument — must resolve against sandboxDir, not Directory.GetCurrentDirectory().
            var result = plugin.SearchContent("needle-marker");

            Assert.Contains("[RESULTS]", result);
            Assert.Contains("here.txt", result);
        }
        finally
        {
            Directory.Delete(sandboxDir, recursive: true);
        }
    }

    [Fact]
    public void SearchContent_BoundsAbnormallyLongMatchLine()
    {
        var dir = CreateTempDir();
        try
        {
            var longLine = "needle-marker " + new string('x', 5000);
            File.WriteAllText(Path.Combine(dir, "data.txt"), longLine);

            var plugin = new SearchPlugin();
            var result = plugin.SearchContent("needle-marker", dir);

            Assert.Contains("[RESULTS]", result);
            Assert.Contains("col 1: needle-marker", result);
            Assert.True(result.Length < MatchExcerpt.MaxLineLength + 500, $"Expected a bounded result, got {result.Length} chars.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A Boomi profile/map export is a single ~38k-character line. The match used to be shown as
    // the line's first 500 characters, which didn't contain it, so the model saw a "hit" with
    // nothing useful in it and fell back to writing throwaway Python inspectors.
    private static string OneLineXml(string field) =>
        "<Component>" +
        string.Concat(Enumerable.Range(0, 400).Select(i => $"<JSONProfileEntry name=\"Field{i}\" dataType=\"character\"/>")) +
        $"<JSONProfileEntry name=\"{field}\" dataType=\"datetime\"><ProfileDateFormat/></JSONProfileEntry>" +
        string.Concat(Enumerable.Range(400, 200).Select(i => $"<JSONProfileEntry name=\"Field{i}\" dataType=\"character\"/>")) +
        "</Component>";

    [Fact]
    public void SearchContent_MatchDeepInOneLineFile_ShowsTheMatchWithItsSurroundings()
    {
        var dir = CreateTempDir();
        try
        {
            var xml = OneLineXml("EffectiveDate");
            File.WriteAllText(Path.Combine(dir, "Vehicle Contracts.xml"), xml);

            var result = new SearchPlugin().SearchContent("EffectiveDate", dir);

            Assert.Contains("[RESULTS] 1 match(es)", result);
            Assert.Contains($"col {xml.IndexOf("EffectiveDate", StringComparison.Ordinal) + 1}:", result);
            Assert.Contains("name=\"EffectiveDate\" dataType=\"datetime\"><ProfileDateFormat/>", result);
            Assert.True(result.Length < MatchExcerpt.MaxLineLength + 500, $"Expected a bounded result, got {result.Length} chars.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SearchContent_ManyMatchesOnOneLongLine_CapsExcerptsAndSaysHowManyWereLeftOut()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "profile.xml"), OneLineXml("EffectiveDate"));

            var result = new SearchPlugin().SearchContent("dataType=\"character\"", dir, maxResults: 100);

            Assert.Equal(MatchExcerpt.MaxExcerptsPerLine, result.Split("col ").Length - 1);
            Assert.Contains("more match(es) on this", result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SearchContent_DirectoryIsAFile_SearchesJustThatFile()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "target.md"), "needle-marker in the target");
            File.WriteAllText(Path.Combine(dir, "other.md"), "needle-marker elsewhere");

            var result = new SearchPlugin().SearchContent("needle-marker", Path.Combine(dir, "target.md"));

            Assert.Contains("[RESULTS] 1 match(es) in 1 file(s)", result);
            Assert.Contains("target.md", result);
            Assert.DoesNotContain("other.md", result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SearchContent_DirectoryIsADeniedFile_FindsNothing()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env"), "SECRET=needle-marker");

            var result = new SearchPlugin(sandboxRoot: dir, denyPatterns: [".env"]).SearchContent("needle-marker", Path.Combine(dir, ".env"));

            Assert.Contains("No matches found", result);
            Assert.DoesNotContain("SECRET", result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
