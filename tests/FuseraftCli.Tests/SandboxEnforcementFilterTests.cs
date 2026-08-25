using System.Text.Json;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Covers two gaps that let agents write outside a configured
/// <see cref="FileSystemPermissions.Write"/> scope despite it correctly denying the
/// same path when passed as a plain string:
///
/// 1. Tool-call arguments can reach the middleware as a <see cref="JsonElement"/> rather
///    than a CLR <c>string</c> (the function-invocation framework's per-parameter type
///    coercion runs later, inside the actual function call). The old `is not string`
///    check treated that as "argument absent" and skipped validation entirely.
/// 2. Shell commands were only checked for absolute paths escaping the sandbox root —
///    never against the write glob, and never for relative paths at all. A relative-path
///    <c>sed -i</c> or output redirection could mutate any file inside the sandbox root
///    even when <c>Write</c> confines agents to <c>workspace/**</c>.
/// </summary>
public sealed class SandboxEnforcementFilterTests : IDisposable
{
    private readonly string _sandboxRoot;

    public SandboxEnforcementFilterTests()
    {
        _sandboxRoot = Directory.CreateTempSubdirectory("sandbox-filter-test-").FullName;
    }

    public void Dispose() => Directory.Delete(_sandboxRoot, recursive: true);

    private SandboxEnforcementFilter MakeFilter() => new(
        sandboxRoot: _sandboxRoot,
        fsPermissions: new FileSystemPermissions
        {
            Write = ["workspace/**", ".fuseraft/tests/**", ".fuseraft/artifacts/**"],
        });

    // ── JsonElement argument coercion ──────────────────────────────────────

    [Fact]
    public void PatchFile_BarePathAsPlainString_IsDeniedByWriteGlob()
    {
        var result = MakeFilter().Inspect("patch_file",
            new Dictionary<string, object?> { ["path"] = "README.md" });

        Assert.NotNull(result);
        Assert.Contains("DENIED", result);
    }

    [Fact]
    public void PatchFile_BarePathAsJsonElement_IsAlsoDeniedByWriteGlob()
    {
        using var doc = JsonDocument.Parse("\"README.md\"");
        var result = MakeFilter().Inspect("patch_file",
            new Dictionary<string, object?> { ["path"] = doc.RootElement });

        Assert.NotNull(result);
        Assert.Contains("DENIED", result);
    }

    [Fact]
    public void WriteFile_WorkspacePathAsJsonElement_IsAllowed()
    {
        using var doc = JsonDocument.Parse("\"workspace/weather.py\"");
        var result = MakeFilter().Inspect("write_file",
            new Dictionary<string, object?> { ["path"] = doc.RootElement });

        Assert.Null(result);
    }

    // ── Shell write-target scanning ────────────────────────────────────────

    [Fact]
    public void ShellRun_SedInPlaceOnBareReadme_IsDenied()
    {
        var result = MakeFilter().Inspect("shell_run",
            new Dictionary<string, object?> { ["command"] = "sed -i 's/$/ /' README.md" });

        Assert.NotNull(result);
        Assert.Contains("DENIED", result);
    }

    [Fact]
    public void ShellRun_RedirectionToBareReadme_IsDenied()
    {
        var result = MakeFilter().Inspect("shell_run",
            new Dictionary<string, object?> { ["command"] = "echo 'hi' > README.md" });

        Assert.NotNull(result);
        Assert.Contains("DENIED", result);
    }

    [Fact]
    public void ShellRun_RedirectionIntoWorkspace_IsAllowed()
    {
        var result = MakeFilter().Inspect("shell_run",
            new Dictionary<string, object?> { ["command"] = "echo 'hi' > workspace/out.txt" });

        Assert.Null(result);
    }

    [Fact]
    public void ShellRun_SedInPlaceInsideWorkspace_IsAllowed()
    {
        var result = MakeFilter().Inspect("shell_run",
            new Dictionary<string, object?> { ["command"] = "sed -i 's/$/ /' workspace/weather.py" });

        Assert.Null(result);
    }

    [Fact]
    public void ShellRun_PlainReadOfBareReadme_IsNotFalselyDenied()
    {
        // cat/grep/git diff on a file outside workspace/ are legitimate reads — the
        // write-target scan must not treat every path-looking token as a write.
        var catResult  = MakeFilter().Inspect("shell_run", new Dictionary<string, object?> { ["command"] = "cat README.md" });
        var grepResult = MakeFilter().Inspect("shell_run", new Dictionary<string, object?> { ["command"] = "grep -n TODO README.md" });
        var diffResult = MakeFilter().Inspect("shell_run", new Dictionary<string, object?> { ["command"] = "git diff README.md" });

        Assert.Null(catResult);
        Assert.Null(grepResult);
        Assert.Null(diffResult);
    }

    [Fact]
    public void ShellRun_SedRegexContainingSlashes_IsNotFalselyDeniedAsAPath()
    {
        // The sed script itself ("s/$/ /") contains slashes; only the trailing file
        // argument is a write target, on a file inside the write scope.
        var result = MakeFilter().Inspect("shell_run",
            new Dictionary<string, object?> { ["command"] = "sed -i 's/foo/bar/' workspace/weather.py" });

        Assert.Null(result);
    }

    // ── create_directory vs. a file-shaped write glob ──────────────────────

    [Fact]
    public void CreateDirectory_BareAncestorOfWriteGlob_IsAllowed()
    {
        // "workspace/**" only matches files under workspace/, never the literal
        // "workspace" segment itself — but creating that directory is a prerequisite
        // for writes the glob already permits, so it must not be denied.
        var result = MakeFilter().Inspect("create_directory",
            new Dictionary<string, object?> { ["path"] = "workspace" });

        Assert.Null(result);
    }

    [Fact]
    public void CreateDirectory_NestedAncestorOfWriteGlob_IsAllowed()
    {
        var result = MakeFilter().Inspect("create_directory",
            new Dictionary<string, object?> { ["path"] = "workspace/src" });

        Assert.Null(result);
    }

    [Fact]
    public void CreateDirectory_OutsideWriteGlob_IsStillDenied()
    {
        var result = MakeFilter().Inspect("create_directory",
            new Dictionary<string, object?> { ["path"] = "other" });

        Assert.NotNull(result);
        Assert.Contains("DENIED", result);
    }

    [Fact]
    public void WriteFile_BareAncestorDirectory_IsNotAllowedByAncestorRule()
    {
        // The ancestor relaxation is scoped to create_directory only — write_file must
        // still match the glob on its own merits, so a bare "workspace" (not a file
        // under it) stays denied.
        var result = MakeFilter().Inspect("write_file",
            new Dictionary<string, object?> { ["path"] = "workspace" });

        Assert.NotNull(result);
        Assert.Contains("DENIED", result);
    }
}
