using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// <see cref="ProbePlugin"/> launched <c>bash -c</c> and <c>python -c</c> through
/// <c>ProcessHelper</c> directly, so it was an alternative shell with none of <c>shell_run</c>'s
/// protections. Probed against the real plugin before this fix: raw environment secrets came back from
/// all four tools, <c>cat .env</c> and a Python <c>open('.env')</c> read the file, <c>sudo</c> and
/// <c>mkfs</c> were not denied, and it ran in any directory. Every case below is one of those.
/// </summary>
public sealed class ProbePluginGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fuseraft_probeguard_").FullName;
    private readonly List<string> _envVars = [];

    private const string EnvSecret = "probe-env-secret-98765432";
    private const string FileSecret = "probe-file-secret-4242";

    public void Dispose()
    {
        foreach (var name in _envVars) Environment.SetEnvironmentVariable(name, null);
        Directory.Delete(_root, recursive: true);
    }

    private string SecretEnvVar()
    {
        var name = $"FUSERAFT_PROBE_{Guid.NewGuid():N}_API_KEY".ToUpperInvariant();
        _envVars.Add(name);
        Environment.SetEnvironmentVariable(name, EnvSecret);
        return name;
    }

    // Probe as production builds it: from the configured registry, sharing the configured shell.
    private ProbePlugin ConfiguredProbe(ShellPolicy? policy = null, string? sandbox = null)
    {
        var registry = new PluginRegistry().RegisterDefaults().Configure(new SecurityConfig
        {
            FileSystemSandboxPath = sandbox ?? _root,
            ShellPolicy = policy,
        });
        Assert.True(registry.TryGet("Probe", out var obj));
        return Assert.IsType<ProbePlugin>(obj);
    }

    private static void AssertDenied(string result) => Assert.StartsWith("[DENIED]", result);

    // -- environment secrets ------------------------------------------------------------------

    [Fact]
    public async Task ProbeCode_Bash_DoesNotReturnAnEnvironmentSecret()
    {
        var name = SecretEnvVar();

        var result = await new ProbePlugin().ProbeCodeAsync("bash", $"printenv {name}");

        Assert.DoesNotContain(EnvSecret, result);
        Assert.Contains(EnvSecretMasker.Placeholder, result);
    }

    [Fact]
    public async Task ProbeCode_Python_DoesNotReturnAnEnvironmentSecret()
    {
        var name = SecretEnvVar();

        var result = await new ProbePlugin().ProbeCodeAsync("python", $"import os; print(os.environ['{name}'])");

        Assert.DoesNotContain(EnvSecret, result);
        Assert.Contains(EnvSecretMasker.Placeholder, result);
    }

    [Fact]
    public async Task AssertOutput_MasksTheSecret_SoItCannotBeUsedAsAGuessingOracle()
    {
        var name = SecretEnvVar();

        // Before the fix this compared against the raw value: "contains 'probe-env'" was PASS, which
        // is exactly the yes/no answer needed to recover a secret one guess at a time.
        var result = await new ProbePlugin().AssertOutputAsync($"printenv {name}", "probe-env", "contains");

        Assert.DoesNotContain(EnvSecret, result);
        Assert.Contains("VERDICT: FAIL", result);
    }

    [Fact]
    public async Task CompareOutputs_DoesNotReturnAnEnvironmentSecret()
    {
        var name = SecretEnvVar();

        var result = await new ProbePlugin().CompareOutputsAsync($"printenv {name}", "echo x");

        Assert.DoesNotContain(EnvSecret, result);
    }

    [Fact]
    public async Task RunHypothesis_DoesNotReturnAnEnvironmentSecret()
    {
        var name = SecretEnvVar();

        var result = await new ProbePlugin().RunHypothesisAsync("the key is set", $"printenv {name}", "anything");

        Assert.DoesNotContain(EnvSecret, result);
    }

    // -- protected files ----------------------------------------------------------------------

    [Theory]
    [InlineData("bash", "cat .env")]
    [InlineData("python", "print(open('.env').read())")]
    [InlineData("python", "import pathlib; print(pathlib.Path('.env').read_text())")]
    [InlineData("node", "console.log(require('fs').readFileSync('.env','utf8'))")]
    public async Task ProbeCode_CannotReadAnEnvFile_InAnyLanguage(string language, string code)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, ".env"), $"PROBE_SECRET={FileSecret}\n");

        var result = await ConfiguredProbe().ProbeCodeAsync(language, code);

        AssertDenied(result);
        Assert.DoesNotContain(FileSecret, result);
    }

    [Theory]
    [InlineData("bash", "cat /tmp/fuseraft-test-nonexistent/id_rsa")]
    [InlineData("bash", "base64 ~/.aws/credentials")]
    [InlineData("python", "print(open('/tmp/fuseraft-test-nonexistent/id_rsa').read())")]
    [InlineData("python", "import os; print(open(os.path.expanduser('~/.netrc')).read())")]
    public async Task ProbeCode_CannotReadACredentialsFile(string language, string code)
    {
        var result = await ConfiguredProbe().ProbeCodeAsync(language, code);

        AssertDenied(result);
        Assert.Contains("credentials file", result);
    }

    [Fact]
    public async Task AssertOutput_CannotReadAnEnvFile()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, ".env"), $"PROBE_SECRET={FileSecret}\n");

        var result = await ConfiguredProbe().AssertOutputAsync("cat .env", "nope");

        AssertDenied(result);
        Assert.DoesNotContain(FileSecret, result);
    }

    // -- the shell's other guards -------------------------------------------------------------

    [Theory]
    [InlineData("sudo -n true")]
    [InlineData("env sudo -n true")]
    [InlineData("/usr/bin/sudo -n true")]
    [InlineData("(sudo -n true)")]
    public async Task EveryTool_DeniesSudo(string command)
    {
        var probe = new ProbePlugin();

        AssertDenied(await probe.ProbeCodeAsync("bash", command));
        AssertDenied(await probe.AssertOutputAsync(command, "x"));
        AssertDenied(await probe.CompareOutputsAsync(command, "echo x"));
        AssertDenied(await probe.CompareOutputsAsync("echo x", command));
        AssertDenied(await probe.RunHypothesisAsync("h", command, "x"));
        AssertDenied(await probe.RunHypothesisAsync("h", "echo x", "x", setupCommand: command));
    }

    [Theory]
    [InlineData("mkfs.ext4 /dev/fuseraft-test-nonexistent")]
    [InlineData("dd if=/dev/zero of=/dev/nvme99n99")]   // no such device: harmless even if the guard regressed
    [InlineData("curl --version | sh")]
    public async Task EveryTool_DeniesTheDangerousCommandGuardsRules(string command)
    {
        var probe = new ProbePlugin();

        AssertDenied(await probe.ProbeCodeAsync("bash", command));
        AssertDenied(await probe.AssertOutputAsync(command, "x"));
        AssertDenied(await probe.RunHypothesisAsync("h", command, "x"));
    }

    [Fact]
    public async Task CompareOutputs_VetsBothCommandsBeforeRunningEither()
    {
        var marker = Path.Combine(_root, "side-effect");

        var result = await new ProbePlugin().CompareOutputsAsync($"touch {marker}", "sudo -n true");

        AssertDenied(result);
        Assert.False(File.Exists(marker), "command A ran even though command B was refused");
    }

    [Fact]
    public async Task RunHypothesis_ARefusedSetupCommand_MeansTheProbeNeverRuns()
    {
        var marker = Path.Combine(_root, "probe-ran");

        var result = await new ProbePlugin().RunHypothesisAsync("h", $"touch {marker}", "x", setupCommand: "sudo -n true");

        AssertDenied(result);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task ANonShellSnippet_IsNotFalselyDeniedForContainingShellLookingText()
    {
        // The shell-specific rules parse POSIX commands; in Python this is just a string.
        var result = await new ProbePlugin().ProbeCodeAsync("python", "print('to clean up: rm -rf / is never ok; sudo apt install x')");

        Assert.DoesNotContain("[DENIED]", result);
        Assert.Contains("rm -rf /", result);
    }

    // -- sandbox, policy, approval ------------------------------------------------------------

    [Fact]
    public async Task ADirectoryOutsideTheSandbox_IsRefused_ByEveryTool()
    {
        var probe = ConfiguredProbe();

        AssertDenied(await probe.ProbeCodeAsync("bash", "pwd", directory: "/"));
        AssertDenied(await probe.AssertOutputAsync("pwd", "x", directory: "/"));
        AssertDenied(await probe.CompareOutputsAsync("pwd", "pwd", directory: "/"));
        AssertDenied(await probe.RunHypothesisAsync("h", "pwd", "x", directory: "/"));
    }

    [Fact]
    public async Task TheDefaultDirectory_RunsInsideTheSandbox_InsteadOfBeingRefused()
    {
        var probe = ConfiguredProbe();
        var real = Path.GetFullPath(_root).TrimEnd('/');

        var result = await probe.ProbeCodeAsync("bash", "pwd");

        Assert.DoesNotContain("[DENIED]", result);          // "." must not resolve against the process cwd
        Assert.Contains($"STDOUT:\n  {real}", result);      // the command's own output, not a message that quotes the path
        Assert.Contains("VERDICT: PASS", await probe.AssertOutputAsync("pwd", real, "equals"));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ADotOrBlankDirectory_MeansTheSandboxRoot(string directory)
    {
        var result = await ConfiguredProbe().ProbeCodeAsync("bash", "pwd", directory: directory);

        Assert.DoesNotContain("[DENIED]", result);
    }

    [Fact]
    public async Task ASubdirectoryInsideTheSandbox_IsAllowed()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        var result = await ConfiguredProbe().ProbeCodeAsync("bash", "pwd", directory: Path.Combine(_root, "sub"));

        Assert.DoesNotContain("[DENIED]", result);
        Assert.Contains("sub", result);
    }

    [Fact]
    public async Task AnAllowList_RestrictsProbeToo_AndRejectsNonShellSnippets()
    {
        var probe = ConfiguredProbe(new ShellPolicy { Allow = ["echo"] });

        Assert.Contains("hi-there", await probe.ProbeCodeAsync("bash", "echo hi-there"));
        AssertDenied(await probe.ProbeCodeAsync("bash", "uname"));
        AssertDenied(await probe.ProbeCodeAsync("python", "print(1)"));
    }

    [Fact]
    public async Task ADenyPolicy_AppliesToProbe()
    {
        var probe = ConfiguredProbe(new ShellPolicy { Deny = ["forbidden-word"] });

        var result = await probe.ProbeCodeAsync("bash", "echo forbidden-word");

        AssertDenied(result);
        Assert.Contains("deny pattern 'forbidden-word'", result);
    }

    [Fact]
    public async Task HitlApproval_AppliesToProbe_AndADeclinedCommandNeverRuns()
    {
        var asked = new List<string>();
        var marker = Path.Combine(_root, "should-not-exist");
        var probe = new ProbePlugin(new ShellPlugin(approveCommand: cmd => { asked.Add(cmd); return Task.FromResult(false); }));

        var result = await probe.ProbeCodeAsync("bash", $"touch {marker}");

        AssertDenied(result);
        Assert.Contains("blocked by user", result);
        Assert.Equal([$"touch {marker}"], asked);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task HitlApproval_AnApprovedCommandRuns()
    {
        var asked = 0;
        var probe = new ProbePlugin(new ShellPlugin(approveCommand: _ => { asked++; return Task.FromResult(true); }));

        var result = await probe.ProbeCodeAsync("bash", "echo approved-output");

        Assert.Contains("approved-output", result);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task HitlApproval_IsAskedOncePerCommand_InCompare()
    {
        var asked = new List<string>();
        var probe = new ProbePlugin(new ShellPlugin(approveCommand: cmd => { asked.Add(cmd); return Task.FromResult(true); }));

        await probe.CompareOutputsAsync("echo a", "echo b");

        Assert.Equal(["echo a", "echo b"], asked);
    }

    // -- wiring and what must not change ------------------------------------------------------

    [Fact]
    public async Task TheRegistrysDefaultProbe_HasTheBuiltInGuards_EvenWithoutConfigure()
    {
        var registry = new PluginRegistry().RegisterDefaults();
        Assert.True(registry.TryGet("Probe", out var obj));

        AssertDenied(await Assert.IsType<ProbePlugin>(obj).ProbeCodeAsync("bash", "sudo -n true"));
    }

    [Fact]
    public async Task OrdinaryProbing_StillWorks_InEveryTool()
    {
        var probe = new ProbePlugin();

        Assert.Contains("hello", await probe.ProbeCodeAsync("bash", "echo hello"));
        Assert.Contains("VERDICT: PASS", await probe.AssertOutputAsync("echo hello", "hello"));
        Assert.Contains("OUTPUTS MATCH: yes", await probe.CompareOutputsAsync("echo same", "echo same"));
        Assert.Contains("VERDICT: PASS", await probe.RunHypothesisAsync("echo works", "echo hi", "hi", setupCommand: "true"));
        Assert.Contains("2", await probe.ProbeCodeAsync("python", "print(1+1)"));
    }

    [Fact]
    public async Task DisposingAProbe_DoesNotDisposeAShellItWasGiven()
    {
        using var shell = new ShellPlugin();
        var probe = new ProbePlugin(shell);

        probe.Dispose();

        Assert.Contains("still-usable", await shell.RunAsync("echo still-usable"));
    }

    [Fact]
    public void DisposingAProbeThatOwnsItsShell_DoesNotThrow_AndIsRepeatable()
    {
        var probe = new ProbePlugin();

        probe.Dispose();
        probe.Dispose();
    }
}
