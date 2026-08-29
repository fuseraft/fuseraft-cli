using fuseraft.Core.Skills;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="SkillPathGuard"/> — path-containment and symlink-escape checks used by
/// <c>SkillsPlugin</c> (<c>read_skill_resource</c>/<c>run_skill_script</c>) before touching a
/// path the model supplied.
/// </summary>
public sealed class SkillPathGuardTests : IDisposable
{
    private readonly string _root;
    private readonly string _skillDir;
    private readonly string _outsideDir;

    public SkillPathGuardTests()
    {
        _root       = Path.Combine(Path.GetTempPath(), "fuseraft_pathguard_tests_" + Guid.NewGuid().ToString("N")[..8]);
        _skillDir   = Path.Combine(_root, "my-skill");
        _outsideDir = Path.Combine(_root, "outside");
        Directory.CreateDirectory(_skillDir);
        Directory.CreateDirectory(_outsideDir);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>True on platforms/environments where creating symlinks is permitted (skips on restricted CI/sandboxes).</summary>
    private bool CanCreateSymlinks(out string reason)
    {
        try
        {
            var link = Path.Combine(_root, "probe-link-" + Guid.NewGuid().ToString("N")[..6]);
            File.CreateSymbolicLink(link, Path.Combine(_root, "probe-target"));
            File.Delete(link);
            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    [Fact]
    public void TryResolveSafePath_PlainNestedFile_Succeeds()
    {
        Directory.CreateDirectory(Path.Combine(_skillDir, "references"));
        File.WriteAllText(Path.Combine(_skillDir, "references", "guide.md"), "content");

        var ok = SkillPathGuard.TryResolveSafePath(_skillDir, "references/guide.md", out var fullPath, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
        Assert.EndsWith(Path.Combine("references", "guide.md"), fullPath);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("references/../../secret.txt")]
    public void TryResolveSafePath_TraversalOutsideRoot_Fails(string relative)
    {
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "top secret");

        var ok = SkillPathGuard.TryResolveSafePath(_skillDir, relative, out _, out var reason);

        Assert.False(ok);
        Assert.Contains("outside", reason);
    }

    [Fact]
    public void TryResolveSafePath_AbsolutePathEscape_Fails()
    {
        var outsideFile = Path.Combine(_outsideDir, "secret.txt");
        File.WriteAllText(outsideFile, "top secret");

        var ok = SkillPathGuard.TryResolveSafePath(_skillDir, outsideFile, out _, out var reason);

        Assert.False(ok);
        Assert.Contains("outside", reason);
    }

    [Fact]
    public void TryResolveSafePath_SymlinkedFilePointingOutside_Fails()
    {
        if (!CanCreateSymlinks(out _)) return; // environment doesn't allow symlinks — skip

        var secret = Path.Combine(_outsideDir, "secret.txt");
        File.WriteAllText(secret, "top secret");
        var link = Path.Combine(_skillDir, "innocuous.md");
        File.CreateSymbolicLink(link, secret);

        var ok = SkillPathGuard.TryResolveSafePath(_skillDir, "innocuous.md", out _, out var reason);

        Assert.False(ok);
        Assert.Contains("symlink", reason);
    }

    [Fact]
    public void TryResolveSafePath_SymlinkedSubdirectoryPointingOutside_Fails()
    {
        if (!CanCreateSymlinks(out _)) return;

        var secretDir = Path.Combine(_outsideDir, "secret-dir");
        Directory.CreateDirectory(secretDir);
        File.WriteAllText(Path.Combine(secretDir, "file.txt"), "top secret");

        var linkedDir = Path.Combine(_skillDir, "references");
        Directory.CreateSymbolicLink(linkedDir, secretDir);

        var ok = SkillPathGuard.TryResolveSafePath(_skillDir, "references/file.txt", out _, out var reason);

        Assert.False(ok);
        Assert.Contains("symlink", reason);
    }

    [Fact]
    public void TryResolveSafePath_NonExistentPath_StillReportsContainment()
    {
        // A not-yet-existing path inside the root should pass the guard; the caller's own
        // File.Exists check is responsible for reporting "not found".
        var ok = SkillPathGuard.TryResolveSafePath(_skillDir, "references/missing.md", out _, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
    }

    [Fact]
    public void IsReparsePoint_RegularFile_ReturnsFalse()
    {
        var file = Path.Combine(_skillDir, "plain.txt");
        File.WriteAllText(file, "content");

        Assert.False(SkillPathGuard.IsReparsePoint(file));
    }

    [Fact]
    public void IsReparsePoint_NonExistentPath_ReturnsFalse()
    {
        Assert.False(SkillPathGuard.IsReparsePoint(Path.Combine(_skillDir, "missing")));
    }

    [Fact]
    public void IsReparsePoint_Symlink_ReturnsTrue()
    {
        if (!CanCreateSymlinks(out _)) return;

        var target = Path.Combine(_outsideDir, "target.txt");
        File.WriteAllText(target, "content");
        var link = Path.Combine(_skillDir, "link.txt");
        File.CreateSymbolicLink(link, target);

        Assert.True(SkillPathGuard.IsReparsePoint(link));
    }
}
