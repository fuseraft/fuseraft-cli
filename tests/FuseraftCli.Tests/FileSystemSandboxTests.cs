using Microsoft.Extensions.FileSystemGlobbing;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="FileSystemSandbox.ResolveSafe"/> and
/// <see cref="FileSystemSandbox.ResolveSafeDirectory"/> directly — the primitive underneath
/// every plugin's sandbox check, exercised here with multiple roots (primary + --include).
/// No dedicated test file existed for this class before; prior coverage was entirely
/// indirect, via single-root, no-exemption plugin calls.
/// </summary>
public sealed class FileSystemSandboxTests : IDisposable
{
    private readonly string _primary;
    private readonly string _includedA;
    private readonly string _includedB;

    public FileSystemSandboxTests()
    {
        _primary   = Directory.CreateTempSubdirectory("fuseraft_sandbox_primary_").FullName;
        _includedA = Directory.CreateTempSubdirectory("fuseraft_sandbox_includedA_").FullName;
        _includedB = Directory.CreateTempSubdirectory("fuseraft_sandbox_includedB_").FullName;
    }

    public void Dispose()
    {
        Directory.Delete(_primary, recursive: true);
        Directory.Delete(_includedA, recursive: true);
        Directory.Delete(_includedB, recursive: true);
    }

    // ---------------------------------------------------------------
    // ResolveSafe
    // ---------------------------------------------------------------

    [Fact]
    public void ResolveSafe_PathUnderPrimaryRoot_Allowed()
    {
        var path = Path.Combine(_primary, "file.txt");
        var denial = FileSystemSandbox.ResolveSafe(path, _primary, [], [], out _);
        Assert.Null(denial);
    }

    [Fact]
    public void ResolveSafe_PathUnderAdditionalRoot_Allowed()
    {
        var path = Path.Combine(_includedA, "file.txt");
        var denial = FileSystemSandbox.ResolveSafe(path, _primary, [], [_includedA], out _);
        Assert.Null(denial);
    }

    [Fact]
    public void ResolveSafe_PathUnderNeitherRoot_Denied()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"outside_{Guid.NewGuid():N}", "file.txt");
        var denial = FileSystemSandbox.ResolveSafe(outside, _primary, [], [_includedA], out _);
        Assert.NotNull(denial);
        Assert.StartsWith("[DENIED]", denial);
    }

    [Fact]
    public void ResolveSafe_MultipleAdditionalRoots_EachIndependentlyAllowed()
    {
        var pathA = Path.Combine(_includedA, "a.txt");
        var pathB = Path.Combine(_includedB, "b.txt");

        Assert.Null(FileSystemSandbox.ResolveSafe(pathA, _primary, [], [_includedA, _includedB], out _));
        Assert.Null(FileSystemSandbox.ResolveSafe(pathB, _primary, [], [_includedA, _includedB], out _));
    }

    [Fact]
    public void ResolveSafe_SimilarlyNamedSiblingRoot_DoesNotFalsePositive()
    {
        // "/tmp/fuseraft_sandbox_includedA_xxx" vs a sibling directory whose name starts with
        // the same prefix (e.g. "...includedA_xxxExtra") must not be treated as contained —
        // this is the same separator-boundary risk the file's own "/sandbox" vs "/sandboxExtra"
        // comment describes, now checked per-root instead of just for the primary root.
        var lookalike = _includedA.TrimEnd(Path.DirectorySeparatorChar) + "Extra";
        Directory.CreateDirectory(lookalike);
        try
        {
            var path = Path.Combine(lookalike, "file.txt");
            var denial = FileSystemSandbox.ResolveSafe(path, _primary, [], [_includedA], out _);
            Assert.NotNull(denial);
        }
        finally
        {
            Directory.Delete(lookalike, recursive: true);
        }
    }

    [Fact]
    public void ResolveSafe_AbsolutePathUnderAdditionalRoot_Allowed()
    {
        // Absolute paths skip the relative-resolution branch entirely and go straight to
        // Path.GetFullPath(expandedPath) — containment must still hold against every root,
        // not just the primary one.
        var absolute = Path.GetFullPath(Path.Combine(_includedA, "sub", "file.txt"));
        var denial = FileSystemSandbox.ResolveSafe(absolute, _primary, [], [_includedA], out var resolved);
        Assert.Null(denial);
        Assert.Equal(absolute, resolved);
    }

    [Fact]
    public void ResolveSafe_AbsolutePathUnderNeitherRoot_Denied()
    {
        var absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"elsewhere_{Guid.NewGuid():N}", "file.txt"));
        var denial = FileSystemSandbox.ResolveSafe(absolute, _primary, [], [_includedA], out _);
        Assert.NotNull(denial);
    }

    [Fact]
    public void ResolveSafe_ExemptedPrefixFallback_StillWorksAlongsideAdditionalRoots()
    {
        var exemptDir = Directory.CreateTempSubdirectory("fuseraft_sandbox_exempt_").FullName;
        try
        {
            var exemptPrefix = exemptDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.Combine(exemptDir, "state.json");

            // Neither the primary root nor the additional root cover this path — only the
            // exempted prefix does. Proves the two concepts compose without one shadowing
            // the other.
            var denial = FileSystemSandbox.ResolveSafe(path, _primary, [exemptPrefix], [_includedA], out _);
            Assert.Null(denial);
        }
        finally
        {
            Directory.Delete(exemptDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveSafe_NullSandboxRoot_AlwaysAllowed_RegardlessOfAdditionalRoots()
    {
        // --yolo: sandboxRoot is null, meaning fully unsandboxed — additionalRoots is
        // meaningless here and must never cause a denial.
        var anywhere = Path.Combine(Path.GetTempPath(), $"anywhere_{Guid.NewGuid():N}", "file.txt");
        var denial = FileSystemSandbox.ResolveSafe(anywhere, null, [], [_includedA], out _);
        Assert.Null(denial);
    }

    [Fact]
    public void ResolveSafe_DenialMessage_MentionsIncludedRootCount()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"outside_{Guid.NewGuid():N}", "file.txt");
        var denial = FileSystemSandbox.ResolveSafe(outside, _primary, [], [_includedA, _includedB], out _);
        Assert.NotNull(denial);
        Assert.Contains("2 included root(s)", denial);
    }

    // ---------------------------------------------------------------
    // ResolveSafe — deny matcher (secrets protection)
    // ---------------------------------------------------------------

    [Fact]
    public void BuildDenyMatcher_NullPatterns_ReturnsNull()
    {
        Assert.Null(FileSystemSandbox.BuildDenyMatcher(null));
    }

    [Fact]
    public void BuildDenyMatcher_EmptyPatterns_ReturnsNull()
    {
        Assert.Null(FileSystemSandbox.BuildDenyMatcher([]));
    }

    [Fact]
    public void ResolveSafe_DenyMatcherMatchesPath_Denied()
    {
        var matcher = FileSystemSandbox.BuildDenyMatcher([".env", ".env.*"]);
        var path = Path.Combine(_primary, ".env");
        var denial = FileSystemSandbox.ResolveSafe(path, _primary, [], [], out _, matcher);
        Assert.NotNull(denial);
        Assert.StartsWith("[DENIED]", denial);
    }

    [Fact]
    public void ResolveSafe_DenyMatcherMatchesDottedVariant_Denied()
    {
        var matcher = FileSystemSandbox.BuildDenyMatcher([".env", ".env.*"]);
        var path = Path.Combine(_primary, ".env.production");
        var denial = FileSystemSandbox.ResolveSafe(path, _primary, [], [], out _, matcher);
        Assert.NotNull(denial);
    }

    [Fact]
    public void ResolveSafe_DenyMatcherDoesNotMatchOtherFiles_Allowed()
    {
        var matcher = FileSystemSandbox.BuildDenyMatcher([".env", ".env.*"]);
        var path = Path.Combine(_primary, "config.json");
        var denial = FileSystemSandbox.ResolveSafe(path, _primary, [], [], out _, matcher);
        Assert.Null(denial);
    }

    [Fact]
    public void ResolveSafe_DenyMatcher_AppliesEvenWithoutSandboxRoot()
    {
        // "Don't leak secrets into context" must hold in an unsandboxed (--yolo) session too,
        // not just when a directory sandbox happens to be configured.
        var matcher = FileSystemSandbox.BuildDenyMatcher([".env"]);
        var path = Path.Combine(Path.GetTempPath(), $"anywhere_{Guid.NewGuid():N}", ".env");
        var denial = FileSystemSandbox.ResolveSafe(path, null, [], [], out _, matcher);
        Assert.NotNull(denial);
    }

    [Fact]
    public void ResolveSafe_NoDenyMatcher_EnvFileAllowedThroughSandboxCheck()
    {
        // Baseline: without a deny matcher, .env is just an ordinary file subject only to the
        // sandbox boundary — proves the deny check is additive, not a hidden always-on rule.
        var path = Path.Combine(_primary, ".env");
        var denial = FileSystemSandbox.ResolveSafe(path, _primary, [], [], out _);
        Assert.Null(denial);
    }

    // ---------------------------------------------------------------
    // ResolveSafeDirectory
    // ---------------------------------------------------------------

    [Fact]
    public void ResolveSafeDirectory_NullDirectory_DefaultsToPrimaryRoot_NotAdditionalRoot()
    {
        var denial = FileSystemSandbox.ResolveSafeDirectory(null, _primary, [_includedA], out var resolved);
        Assert.Null(denial);
        Assert.Equal(Path.GetFullPath(_primary), resolved);
    }

    [Fact]
    public void ResolveSafeDirectory_DirectoryUnderAdditionalRoot_Allowed()
    {
        var denial = FileSystemSandbox.ResolveSafeDirectory(_includedA, _primary, [_includedA], out _);
        Assert.Null(denial);
    }

    [Fact]
    public void ResolveSafeDirectory_DirectoryUnderNeitherRoot_Denied()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"outside_{Guid.NewGuid():N}");
        var denial = FileSystemSandbox.ResolveSafeDirectory(outside, _primary, [_includedA], out _);
        Assert.NotNull(denial);
        Assert.StartsWith("[DENIED]", denial);
    }

    [Fact]
    public void ResolveSafeDirectory_NullSandboxRoot_PassesDirectoryThroughUnresolved()
    {
        // --yolo: no sandbox — the raw argument (possibly relative, possibly null) is passed
        // straight through for the subprocess to resolve itself, never full-pathed here.
        var denial = FileSystemSandbox.ResolveSafeDirectory("some/relative/dir", null, [_includedA], out var resolved);
        Assert.Null(denial);
        Assert.Equal("some/relative/dir", resolved);
    }
}
