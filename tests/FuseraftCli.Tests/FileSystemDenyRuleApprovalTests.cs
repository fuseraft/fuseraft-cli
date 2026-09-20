using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// A FileSystem deny rule (<c>.env</c>, a credentials file, a configured <c>Deny</c> glob) is an
/// explicit "never" — it must not be turned into a sandbox-escape prompt that a single <c>y</c>
/// answers. Regression: before this, <see cref="IncludedRootsState.DenyOrEscalateAsync"/> offered
/// ANY denial as "… is outside the current sandbox — grants '&lt;dir&gt;'", and approving returned
/// "proceed", so in the default REPL (HITL on) one approval read, overwrote or deleted a
/// protected file through any of the 17 FileSystem tool call sites. Found by driving the real REPL
/// against a fake nested <c>backend/.env</c>. These tests use an approver that says "yes" to
/// everything, which is the worst case.
/// </summary>
public sealed class FileSystemDenyRuleApprovalTests : IDisposable
{
    private readonly string _root;
    private readonly string _outside;
    private readonly List<string> _approvalsAsked = [];
    private readonly FileSystemPlugin _plugin;
    private readonly FileSystemManagementOps _ops;

    private const string Secret = "TOP-SECRET-VALUE";

    public FileSystemDenyRuleApprovalTests()
    {
        _root    = Directory.CreateTempSubdirectory("fuseraft_denyapproval_").FullName;
        _outside = Directory.CreateTempSubdirectory("fuseraft_denyapproval_outside_").FullName;

        var roots = new IncludedRootsState();
        Func<string, string, Task<bool>> yesToEverything = (tool, _) => { _approvalsAsked.Add(tool); return Task.FromResult(true); };

        _plugin = new FileSystemPlugin(
            sandboxRoot: _root, approveAction: yesToEverything, includedRoots: roots,
            denyPatterns: DefaultSecurityPolicy.MergeFileSystemDeny(null));
        _ops = new FileSystemManagementOps(_plugin, sandboxRoot: _root, includedRoots: roots);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    private string Write(string relative, string content = Secret)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public static TheoryData<string> ProtectedFiles => new()
    {
        ".env", "backend/.env", "apps/web/.env.local", "deploy/id_rsa", "home/u/.aws/credentials", ".netrc",
    };

    private void AssertDeniedWithoutAsking(string result)
    {
        Assert.StartsWith("[DENIED]", result);
        Assert.Contains("deny rule", result);
        Assert.DoesNotContain(Secret, result);
        Assert.Empty(_approvalsAsked);            // never even offered as an escapable prompt
    }

    [Theory, MemberData(nameof(ProtectedFiles))]
    public async Task ReadFile_OfAProtectedFile_IsDenied_AndNeverEscalatedToAPrompt(string relative)
    {
        var path = Write(relative);

        AssertDeniedWithoutAsking(await _plugin.ReadFileAsync(path));
    }

    [Theory, MemberData(nameof(ProtectedFiles))]
    public async Task WriteFile_OverAProtectedFile_IsDenied_AndTheFileIsUntouched(string relative)
    {
        var path = Write(relative);

        AssertDeniedWithoutAsking(await _plugin.WriteFileAsync(path, "overwritten"));
        Assert.Equal(Secret, await File.ReadAllTextAsync(path));
    }

    [Theory, MemberData(nameof(ProtectedFiles))]
    public async Task PatchFile_OnAProtectedFile_IsDenied_AndTheFileIsUntouched(string relative)
    {
        var path = Write(relative);

        AssertDeniedWithoutAsking(await _plugin.PatchFileAsync(path, Secret, "patched"));
        Assert.Equal(Secret, await File.ReadAllTextAsync(path));
    }

    [Theory, MemberData(nameof(ProtectedFiles))]
    public async Task DeleteFile_OnAProtectedFile_IsDenied_AndTheFileSurvives(string relative)
    {
        var path = Write(relative);

        AssertDeniedWithoutAsking(await _ops.DeleteFileAsync(path));
        Assert.True(File.Exists(path));
    }

    [Theory, MemberData(nameof(ProtectedFiles))]
    public async Task CopyFile_FromAProtectedFile_IsDenied_AndNoCopyIsMade(string relative)
    {
        var src = Write(relative);
        var dst = Path.Combine(_root, "stolen.txt");

        AssertDeniedWithoutAsking(await _ops.CopyFileAsync(src, dst));
        Assert.False(File.Exists(dst));
    }

    [Theory, MemberData(nameof(ProtectedFiles))]
    public async Task CopyFile_OntoAProtectedFile_IsDenied_AndItIsNotOverwritten(string relative)
    {
        var target = Write(relative);
        var src = Write("innocent.txt", "harmless");

        AssertDeniedWithoutAsking(await _ops.CopyFileAsync(src, target));
        Assert.Equal(Secret, await File.ReadAllTextAsync(target));
    }

    [Theory, MemberData(nameof(ProtectedFiles))]
    public async Task MoveFile_FromAProtectedFile_IsDenied_AndItStaysPut(string relative)
    {
        var src = Write(relative);

        AssertDeniedWithoutAsking(await _ops.MoveFileAsync(src, Path.Combine(_root, "moved.txt")));
        Assert.True(File.Exists(src));
    }

    [Theory, MemberData(nameof(ProtectedFiles))]
    public async Task GrepFile_OnAProtectedFile_IsDenied(string relative)
    {
        var path = Write(relative);

        AssertDeniedWithoutAsking(await _ops.GrepFileAsync(path, "SECRET"));
    }

    [Theory, MemberData(nameof(ProtectedFiles))]
    public async Task GetFileSummary_OfAProtectedFile_IsDenied(string relative)
    {
        var path = Write(relative);

        AssertDeniedWithoutAsking(await _ops.GetFileSummaryAsync(path));
    }

    [Fact]
    public async Task AConfiguredDenyGlob_GetsTheSameTreatment()
    {
        var plugin = new FileSystemPlugin(
            sandboxRoot: _root, approveAction: (t, _) => { _approvalsAsked.Add(t); return Task.FromResult(true); },
            includedRoots: new IncludedRootsState(),
            denyPatterns: DefaultSecurityPolicy.MergeFileSystemDeny(new FileSystemPermissions { Deny = ["secrets/**"] }));
        var path = Write("secrets/api-key.txt");

        AssertDeniedWithoutAsking(await plugin.ReadFileAsync(path));
    }

    // A protected file OUTSIDE the sandbox — the deny rule must apply there too, not only the
    // sandbox-escape prompt.

    [Fact]
    public async Task AProtectedFileOutsideTheSandbox_IsDeniedByTheRule_NotOfferedAsAnEscape()
    {
        var path = Path.Combine(_outside, ".env");
        await File.WriteAllTextAsync(path, Secret);

        AssertDeniedWithoutAsking(await _plugin.ReadFileAsync(path));
    }

    [Fact]
    public async Task ADeepProtectedFileOutsideTheSandbox_IsDeniedByTheRule()
    {
        Directory.CreateDirectory(Path.Combine(_outside, ".aws"));
        var path = Path.Combine(_outside, ".aws", "credentials");
        await File.WriteAllTextAsync(path, Secret);

        AssertDeniedWithoutAsking(await _plugin.ReadFileAsync(path));
    }

    // What must NOT change: the sandbox boundary is still something a human can widen.

    [Fact]
    public async Task AnOrdinaryFileOutsideTheSandbox_StillGetsTheEscapePrompt_AndReadsWhenApproved()
    {
        var path = Path.Combine(_outside, "notes.txt");
        await File.WriteAllTextAsync(path, "outside but fine");

        var result = await _plugin.ReadFileAsync(path);

        Assert.Equal(["read_file"], _approvalsAsked);
        Assert.Contains("outside but fine", result);
    }

    [Fact]
    public async Task AnApprovedEscape_DoesNotUnlockAProtectedFileInTheSameGrantedDirectory()
    {
        var ordinary  = Path.Combine(_outside, "notes.txt");
        var protectedFile = Path.Combine(_outside, ".env");
        await File.WriteAllTextAsync(ordinary, "fine");
        await File.WriteAllTextAsync(protectedFile, Secret);

        await _plugin.ReadFileAsync(ordinary);                    // approve: grants _outside for the session
        _approvalsAsked.Clear();
        var result = await _plugin.ReadFileAsync(protectedFile);  // same directory, now "included"

        AssertDeniedWithoutAsking(result);
    }

    [Fact]
    public async Task AnOrdinaryFileInsideTheSandbox_IsUnaffected()
    {
        var path = Write("src/app.py", "print('hi')");

        var result = await _plugin.ReadFileAsync(path);

        Assert.Contains("print('hi')", result);
        Assert.Empty(_approvalsAsked);
    }

    // The choke point itself

    [Fact]
    public async Task DenyOrEscalateAsync_ADenyRuleDenial_IsReturnedAsIs_WithoutPrompting()
    {
        var prompted = false;
        var denial = PluginResult.Denied($"Path '/x/.env' matches a FileSystem deny rule and is blocked for all operations.");

        var outcome = await new IncludedRootsState().DenyOrEscalateAsync(
            denial, "/x/.env", "read_file", (_, _) => { prompted = true; return Task.FromResult(true); });

        Assert.Equal(denial, outcome);
        Assert.False(prompted);
    }

    [Fact]
    public async Task DenyOrEscalateAsync_ASandboxBoundaryDenial_IsStillEscalated()
    {
        var prompted = false;

        var outcome = await new IncludedRootsState().DenyOrEscalateAsync(
            PluginResult.Denied("Path '/x/y' is outside the configured sandbox '/root'."), "/x/y", "read_file",
            (_, _) => { prompted = true; return Task.FromResult(true); });

        Assert.Null(outcome);          // approved -> proceed
        Assert.True(prompted);
    }

    [Theory]
    [InlineData("Path '/a/.env' matches a FileSystem deny rule and is blocked for all operations", true)]
    [InlineData("Path '/a/b' is outside the configured sandbox '/a'.", false)]
    [InlineData("Sandbox-escape request blocked by user.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDenyRuleDenial_RecognisesOnlyTheDenyRuleWording(string? denial, bool expected) =>
        Assert.Equal(expected, FileSystemSandbox.IsDenyRuleDenial(denial));

    [Fact]
    public void TheMessageResolveSafeProducesForADenyRule_IsRecognisedByIsDenyRuleDenial()
    {
        // Ties the message and the check together end to end, so rewording one without the other fails here.
        var matcher = FileSystemSandbox.BuildDenyMatcher(["**/.env"]);
        Write(".env");

        var denial = FileSystemSandbox.ResolveSafe(Path.Combine(_root, ".env"), _root, [], [], out _, matcher);

        Assert.True(FileSystemSandbox.IsDenyRuleDenial(denial));
    }
}
