using System.Reflection;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Encodes the invariant that motivated consolidating path resolution/boundary-checking onto
/// shared <see cref="FileSystemSandbox"/> primitives (<see cref="FileSystemSandbox.ResolveAgainstRoot"/>,
/// <see cref="FileSystemSandbox.IsOutsideSandbox"/>): the REPL sandbox path
/// (<c>FileSystemPlugin</c>/<c>ShellPlugin</c>/<c>GitPlugin</c>, via <see cref="FileSystemSandbox"/>
/// directly) and the orchestration sandbox path (<see cref="SandboxEnforcementFilter"/>, MAF
/// middleware) must reach the same in-bounds/out-of-bounds verdict for the same path against the
/// same root. Before this refactor each maintained its own separate implementation of that
/// check; this test would have caught them disagreeing.
///
/// Does not attempt to cover SandboxEnforcementFilter's orchestration-only capabilities (ring
/// checks, read/write/deny glob permissions, change envelope, shell-command string scanning,
/// prompt-injection detection) — those have no REPL equivalent by design and are out of scope
/// for a parity check.
/// </summary>
public sealed class SandboxParityTests : IDisposable
{
    private readonly string _sandboxRoot;

    public SandboxParityTests()
    {
        _sandboxRoot = Directory.CreateTempSubdirectory("sandbox-parity-test-").FullName;
    }

    public void Dispose() => Directory.Delete(_sandboxRoot, recursive: true);

    // SandboxEnforcementFilter.FuseraftHomePrefix is a `static readonly` field, computed once
    // from HOME/FUSERAFT_HOME the first time the class is touched by *any* test in this
    // process and cached forever after — it does not track later env-var changes. Several other
    // test classes deliberately override that env var for their own isolation (see
    // FuseraftHomeEnvCollection), so freshly calling FuseraftPaths.ExpandPath("~/.fuseraft")
    // here can legitimately disagree with the filter's already-cached value depending on test
    // execution order — not a bug in either side, just two different points in time. Reading the
    // filter's actual cached value via reflection makes both sides of this comparison use the
    // same one, whatever it is, instead of a value that's `~/.fuseraft/`-correct-right-now but
    // may not match what the filter is actually enforcing in this process.
    private static readonly string ExpandedFuseraftHomeExemption =
        (string)typeof(SandboxEnforcementFilter)
            .GetField("FuseraftHomePrefix", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private bool ReplSaysOutside(string rawPath)
    {
        var denial = FileSystemSandbox.ResolveSafe(
            rawPath, _sandboxRoot, exemptedPrefixes: [ExpandedFuseraftHomeExemption], additionalRoots: [], out _);
        return denial is not null;
    }

    private bool OrchestrationSaysOutside(string rawPath)
    {
        var filter = new SandboxEnforcementFilter(_sandboxRoot);
        var denial = filter.Inspect("read_file", new Dictionary<string, object?> { ["path"] = rawPath });
        return denial is not null;
    }

    [Theory]
    [InlineData("inside.txt", false)]
    [InlineData("nested/inside.txt", false)]
    [InlineData("./inside.txt", false)]
    [InlineData("../outside.txt", true)]
    [InlineData("../../etc/passwd", true)]
    public void RelativePath_ReplAndOrchestrationAgree(string rawPath, bool expectedOutside)
    {
        Assert.Equal(expectedOutside, ReplSaysOutside(rawPath));
        Assert.Equal(expectedOutside, OrchestrationSaysOutside(rawPath));
    }

    [Fact]
    public void AbsolutePathInsideSandbox_ReplAndOrchestrationAgree()
    {
        var absoluteInside = Path.Combine(_sandboxRoot, "inside.txt");

        Assert.False(ReplSaysOutside(absoluteInside));
        Assert.False(OrchestrationSaysOutside(absoluteInside));
    }

    [Fact]
    public void AbsolutePathOutsideSandbox_ReplAndOrchestrationAgree()
    {
        var outsideRoot = Directory.CreateTempSubdirectory("sandbox-parity-outside-").FullName;
        try
        {
            var absoluteOutside = Path.Combine(outsideRoot, "secret.txt");

            Assert.True(ReplSaysOutside(absoluteOutside));
            Assert.True(OrchestrationSaysOutside(absoluteOutside));
        }
        finally
        {
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public void FuseraftHomeDirectory_ExemptOnBothSides()
    {
        // Both sides special-case the fuseraft home dir as always-accessible session-state
        // storage, regardless of the project sandbox — REPL via an explicit exemptedPrefixes
        // entry callers pass in, orchestration via its cached FuseraftHomePrefix field (see
        // that field's comment above for why this test reads it rather than recomputing
        // ~/.fuseraft fresh). Different mechanisms, same resulting exemption; this pins that
        // they still agree.
        var homeEventsPath = Path.Combine(ExpandedFuseraftHomeExemption, "state/sessions/test/events.jsonl");

        Assert.False(ReplSaysOutside(homeEventsPath));
        Assert.False(OrchestrationSaysOutside(homeEventsPath));
    }

    [Fact]
    public void WrappingQuotes_StrippedIdenticallyOnBothSides()
    {
        // A model sometimes passes a quoted path argument out of shell-quoting habit — both
        // sides must resolve `"inside.txt"` the same way `inside.txt` resolves.
        Assert.Equal(ReplSaysOutside("inside.txt"), ReplSaysOutside("\"inside.txt\""));
        Assert.Equal(OrchestrationSaysOutside("inside.txt"), OrchestrationSaysOutside("\"inside.txt\""));
        Assert.False(OrchestrationSaysOutside("\"inside.txt\""));
    }
}
