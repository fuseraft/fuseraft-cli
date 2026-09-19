using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

public sealed class ShellPluginTests
{
    // RunAsync — quiet parameter (folded in from the removed shell_run_quiet tool)

    [Fact]
    public async Task RunAsync_QuietOnSuccess_ReturnsOk()
    {
        using var plugin = new ShellPlugin();
        var result = await plugin.RunAsync("echo hello", quiet: true);
        Assert.Equal("OK", result);
    }

    [Fact]
    public async Task RunAsync_QuietOnFailure_ReturnsFullOutputAndExitCode()
    {
        using var plugin = new ShellPlugin();
        var result = await plugin.RunAsync("exit 3", quiet: true);
        Assert.NotEqual("OK", result);
        Assert.Contains("[EXIT 3]", result);
    }

    [Fact]
    public async Task RunAsync_NotQuiet_ReturnsFullOutputOnSuccess()
    {
        using var plugin = new ShellPlugin();
        var result = await plugin.RunAsync("echo hello-not-quiet");
        Assert.Contains("hello-not-quiet", result);
    }

    // Regression: cmd.exe's /c parser doesn't follow the same quoting convention .NET uses
    // to encode ArgumentList elements. Passing a command with an embedded quoted, multi-word
    // argument (e.g. git commit -m "...") must reach the child process intact.
    [Fact]
    public async Task RunAsync_CommandWithEmbeddedQuotedMultiWordArg_PreservesQuoting()
    {
        using var plugin = new ShellPlugin();
        var tmpDir = Path.Combine(Path.GetTempPath(), "shellplugin-quoting-" + Guid.NewGuid());
        Directory.CreateDirectory(tmpDir);
        try
        {
            await plugin.RunAsync("git init -q", tmpDir);
            // A clean CI runner has no global git identity configured, and `git commit` refuses
            // to run without one — set a repo-local identity so this test doesn't depend on the
            // ambient environment having one already.
            await plugin.RunAsync("git config user.email \"test@example.com\"", tmpDir);
            await plugin.RunAsync("git config user.name \"Test User\"", tmpDir);
            await File.WriteAllTextAsync(Path.Combine(tmpDir, "test.txt"), "hello");
            await plugin.RunAsync("git add .", tmpDir);

            var result = await plugin.RunAsync(
                "git commit -m \"Initial commit: vendor intake API project files\"", tmpDir);

            Assert.DoesNotContain("pathspec", result);
            Assert.Contains("Initial commit: vendor intake API project files", result);
        }
        finally
        {
            // git marks object files read-only on Windows; clear that before deleting.
            foreach (var file in Directory.EnumerateFiles(tmpDir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // GetSessionTempDir

    [Fact]
    public void GetSessionTempDir_CreatesDirectory()
    {
        using var plugin = new ShellPlugin();
        var path = plugin.GetSessionTempDir();
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void GetSessionTempDir_IsIdempotent()
    {
        using var plugin = new ShellPlugin();
        var first  = plugin.GetSessionTempDir();
        var second = plugin.GetSessionTempDir();
        Assert.Equal(first, second);
    }

    [Fact]
    public void GetSessionTempDir_ReturnsDistinctPathsForDifferentInstances()
    {
        using var a = new ShellPlugin();
        using var b = new ShellPlugin();
        Assert.NotEqual(a.GetSessionTempDir(), b.GetSessionTempDir());
    }

    [Fact]
    public void Dispose_DeletesSessionTempDir()
    {
        string path;
        using (var plugin = new ShellPlugin())
        {
            path = plugin.GetSessionTempDir();
            Assert.True(Directory.Exists(path));
        }
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void Dispose_WhenTempDirNeverRequested_DoesNotThrow()
    {
        var plugin = new ShellPlugin();
        var ex = Record.Exception(() => plugin.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_WhenCalledTwice_DoesNotThrow()
    {
        var plugin = new ShellPlugin();
        plugin.GetSessionTempDir();
        plugin.Dispose();
        var ex = Record.Exception(() => plugin.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void GetSessionTempDir_ConcurrentCalls_ReturnSamePath()
    {
        using var plugin = new ShellPlugin();

        var paths = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 32, _ => paths.Add(plugin.GetSessionTempDir()));

        Assert.Single(paths.Distinct());
    }

    // LooksLikeShellMismatch — Windows cmd.exe/PowerShell fallback detection

    [Theory]
    [InlineData("'Get-ChildItem' is not recognized as an internal or external command, operable program or batch file.")]
    [InlineData("'Where-Object' is not recognized as an internal or external command, operable program or batch file.")]
    [InlineData("'$env:PATH' is not recognized as an internal or external command, operable program or batch file.")]
    public void LooksLikeShellMismatch_CmdUnrecognizedCommandOnFailure_ReturnsTrue(string stderr)
    {
        var result = new ProcessResult(string.Empty, stderr, 1);
        Assert.True(ShellPlugin.LooksLikeShellMismatch(result));
    }

    [Fact]
    public void LooksLikeShellMismatch_MatchesInStdoutToo()
    {
        var result = new ProcessResult(
            "'Test-Path' is not recognized as an internal or external command, operable program or batch file.",
            string.Empty, 1);
        Assert.True(ShellPlugin.LooksLikeShellMismatch(result));
    }

    [Fact]
    public void LooksLikeShellMismatch_SuccessfulResult_ReturnsFalseEvenIfTextMatches()
    {
        // Exit code 0 means the command succeeded — never second-guess a success.
        var result = new ProcessResult(
            "'foo' is not recognized as an internal or external command, operable program or batch file.",
            string.Empty, 0);
        Assert.False(ShellPlugin.LooksLikeShellMismatch(result));
    }

    [Fact]
    public void LooksLikeShellMismatch_UnrelatedFailure_ReturnsFalse()
    {
        var result = new ProcessResult(string.Empty, "fatal: not a git repository", 128);
        Assert.False(ShellPlugin.LooksLikeShellMismatch(result));
    }

    // RunBackgroundAsync — regression coverage for the process-start refactor that added the
    // Windows cmd.exe/PowerShell mismatch retry (the retry itself only triggers on Windows).

    [Fact]
    public async Task RunBackgroundAsync_StartsJobAndReportsCompletion()
    {
        using var plugin = new ShellPlugin();

        var started = await plugin.RunBackgroundAsync("echo background-job-output");
        Assert.Contains("[OK]", started);
        Assert.Contains("Job ID:", started);

        var jobId = started.Split("Job ID: ")[1].Split('\n')[0].Trim();

        string status = "";
        for (var i = 0; i < 50 && !status.Contains("COMPLETED"); i++)
        {
            status = await plugin.GetJobStatus(jobId);
            if (!status.Contains("COMPLETED")) await Task.Delay(50);
        }

        Assert.Contains("[COMPLETED]", status);
        Assert.Contains("background-job-output", await plugin.GetJobOutput(jobId));
    }

    [Fact]
    public async Task RunBackgroundAsync_FailedCommand_ReportsFailureNotMismatch()
    {
        using var plugin = new ShellPlugin();

        var started = await plugin.RunBackgroundAsync("exit 7");
        var jobId = started.Split("Job ID: ")[1].Split('\n')[0].Trim();

        string status = "";
        for (var i = 0; i < 50 && !status.Contains("FAILED"); i++)
        {
            status = await plugin.GetJobStatus(jobId);
            if (!status.Contains("FAILED")) await Task.Delay(50);
        }

        Assert.Contains("[FAILED]", status);
        Assert.Contains("exited 7", status);
    }

    // approveCommand — the HITL gate the REPL's /hitl command and `fuseraft run --hitl`
    // both rely on (OrchestratorBuilder.ResolveSecurityConfig wires the same constructor
    // parameter for the orchestration path; ReplCommand.cs wires it for the REPL path).

    [Fact]
    public async Task RunAsync_ApproveCommandReturnsFalse_BlocksAndDoesNotExecute()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"shellplugin-hitl-{Guid.NewGuid():N}.txt");
        using var plugin = new ShellPlugin(approveCommand: _ => Task.FromResult(false));

        var result = await plugin.RunAsync($"touch \"{marker}\"");

        Assert.Contains("[DENIED]", result);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task RunAsync_ApproveCommandReturnsTrue_ExecutesNormally()
    {
        using var plugin = new ShellPlugin(approveCommand: _ => Task.FromResult(true));

        var result = await plugin.RunAsync("echo hitl-approved");

        Assert.Contains("hitl-approved", result);
    }

    [Fact]
    public async Task RunAsync_ApproveCommandSeesActualCommandText()
    {
        string? seen = null;
        using var plugin = new ShellPlugin(approveCommand: cmd => { seen = cmd; return Task.FromResult(true); });

        await plugin.RunAsync("echo hitl-visibility-check");

        Assert.Equal("echo hitl-visibility-check", seen);
    }

    [Fact]
    public async Task RunScriptAsync_ApproveCommandReturnsFalse_BlocksAndDoesNotExecute()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"shellplugin-hitl-script-{Guid.NewGuid():N}.txt");
        using var plugin = new ShellPlugin(approveCommand: _ => Task.FromResult(false));

        var result = await plugin.RunScriptAsync($"touch \"{marker}\"");

        Assert.Contains("[DENIED]", result);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task RunAsync_NoApproveCommand_ExecutesWithoutBlocking()
    {
        // Default construction (no approver) — the REPL's pre-/hitl behavior, and still the
        // behavior once /hitl is off — must keep working unprompted.
        using var plugin = new ShellPlugin();

        var result = await plugin.RunAsync("echo no-approver-configured");

        Assert.Contains("no-approver-configured", result);
    }

    // sandboxRoot — ValidateWorkingDirectory delegates to the shared
    // FileSystemSandbox.ResolveSafeDirectory helper (also used by GitPlugin's repoPath check;
    // see GitPluginTests). No prior coverage existed for this path before that extraction.

    [Fact]
    public async Task RunAsync_WorkingDirectoryOutsideSandbox_ReturnsDenial()
    {
        var sandboxDir = Path.Combine(Path.GetTempPath(), "fuseraft_shellplugin_sandbox_" + Guid.NewGuid().ToString("N")[..8]);
        var outsideDir = Path.Combine(Path.GetTempPath(), "fuseraft_shellplugin_outside_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandboxDir);
        Directory.CreateDirectory(outsideDir);
        try
        {
            using var plugin = new ShellPlugin(sandboxRoot: sandboxDir);

            var result = await plugin.RunAsync("echo hi", workingDirectory: outsideDir);

            Assert.Contains("[DENIED]", result);
        }
        finally
        {
            Directory.Delete(sandboxDir, recursive: true);
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_NoWorkingDirectory_DefaultsToSandboxRootRatherThanDenying()
    {
        var sandboxDir = Path.Combine(Path.GetTempPath(), "fuseraft_shellplugin_sandbox_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandboxDir);
        try
        {
            using var plugin = new ShellPlugin(sandboxRoot: sandboxDir);

            var result = await plugin.RunAsync("pwd", workingDirectory: null);

            Assert.DoesNotContain("[DENIED]", result);
            Assert.Contains(Path.GetFullPath(sandboxDir).TrimEnd('/'), result);
        }
        finally
        {
            Directory.Delete(sandboxDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WorkingDirectoryInsideSandbox_ExecutesNormally()
    {
        var sandboxDir = Path.Combine(Path.GetTempPath(), "fuseraft_shellplugin_sandbox_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandboxDir);
        try
        {
            using var plugin = new ShellPlugin(sandboxRoot: sandboxDir);

            var result = await plugin.RunAsync("echo inside-sandbox", workingDirectory: sandboxDir);

            Assert.Contains("inside-sandbox", result);
        }
        finally
        {
            Directory.Delete(sandboxDir, recursive: true);
        }
    }

    // Secret masking — EnvSecretMasker wired into every shell output path

    private static string EchoVar(string name) =>
        OperatingSystem.IsWindows() ? $"echo %{name}%" : $"echo ${name}";

    [Fact]
    public async Task RunAsync_SecretEnvVarValue_IsMaskedInOutput()
    {
        const string name = "FUSERAFT_TEST_SHELL_API_KEY";
        const string value = "sk-shell-0123456789abcdef";
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            using var plugin = new ShellPlugin();

            var result = await plugin.RunAsync(EchoVar(name));

            Assert.DoesNotContain(value, result);
            Assert.Contains(EnvSecretMasker.Placeholder, result);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public async Task RunAsync_SecretEnvVarValue_IsMaskedInFailureOutputToo()
    {
        const string name = "FUSERAFT_TEST_SHELL_FAIL_TOKEN";
        const string value = "tok-fail-0123456789abcdef";
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            using var plugin = new ShellPlugin();

            var result = await plugin.RunAsync($"{EchoVar(name)} && exit 3");

            Assert.Contains("[EXIT 3]", result);
            Assert.DoesNotContain(value, result);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public async Task RunBackgroundAsync_SecretEnvVarValue_IsMaskedInJobOutputAndStatus()
    {
        const string name = "FUSERAFT_TEST_SHELL_JOB_SECRET";
        const string value = "job-secret-0123456789abcdef";
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            using var plugin = new ShellPlugin();
            var started = await plugin.RunBackgroundAsync(EchoVar(name));
            var jobId = started.Split("Job ID: ")[1].Split('\n')[0].Trim();

            string status = "";
            for (var i = 0; i < 100 && !status.Contains("COMPLETED"); i++)
            {
                status = await plugin.GetJobStatus(jobId);
                if (!status.Contains("COMPLETED")) await Task.Delay(50);
            }

            var output = await plugin.GetJobOutput(jobId);

            Assert.Contains("[COMPLETED]", status);
            Assert.DoesNotContain(value, status);
            Assert.DoesNotContain(value, output);
            Assert.Contains(EnvSecretMasker.Placeholder, output);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public void GetEnv_SecretLookingName_ReturnsPlaceholderNotValue()
    {
        const string name = "FUSERAFT_TEST_GETENV_API_KEY";
        Environment.SetEnvironmentVariable(name, "abc");   // short on purpose: hidden by name, not by length
        try
        {
            using var plugin = new ShellPlugin();

            Assert.Equal(EnvSecretMasker.Placeholder, plugin.GetEnv(name));
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public void GetEnv_OrdinaryName_ReturnsValue()
    {
        const string name = "FUSERAFT_TEST_GETENV_PLAIN";
        Environment.SetEnvironmentVariable(name, "plain-value");
        try
        {
            using var plugin = new ShellPlugin();

            Assert.Equal("plain-value", plugin.GetEnv(name));
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public void GetEnv_UnsetSecretLookingName_ReturnsEmptyNotPlaceholder()
    {
        using var plugin = new ShellPlugin();

        Assert.Equal(string.Empty, plugin.GetEnv("FUSERAFT_TEST_GETENV_NEVER_SET_TOKEN"));
    }

    [Fact]
    public void GetEnv_OrdinaryNameHoldingASecretValue_IsMaskedByValue()
    {
        const string secretName = "FUSERAFT_TEST_GETENV_HOLDER_SECRET";
        const string plainName  = "FUSERAFT_TEST_GETENV_ALIAS";
        const string value      = "aliased-secret-0123456789";
        Environment.SetEnvironmentVariable(secretName, value);
        Environment.SetEnvironmentVariable(plainName, value);
        try
        {
            using var plugin = new ShellPlugin();

            Assert.DoesNotContain(value, plugin.GetEnv(plainName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, null);
            Environment.SetEnvironmentVariable(plainName, null);
        }
    }

    // Dangerous-command rails — hard-denied like sudo. Every command below is harmless if the
    // guard regressed and it ran anyway (mkfs on a device that does not exist; `curl --version`
    // piped to a shell that cannot parse it).

    private const string HarmlessRawDiskCommand = "mkfs.ext4 /dev/fuseraft-test-nonexistent";
    private const string HarmlessFetchToExecCommand = "curl --version | sh";

    [Fact]
    public async Task RunAsync_RawDiskOperation_IsDenied()
    {
        using var plugin = new ShellPlugin();

        var result = await plugin.RunAsync(HarmlessRawDiskCommand);

        Assert.StartsWith("[DENIED]", result);
        Assert.Contains(DangerousCommandDetector.RawDiskOp, result);
    }

    [Fact]
    public async Task RunAsync_FetchToExec_IsDenied()
    {
        using var plugin = new ShellPlugin();

        var result = await plugin.RunAsync(HarmlessFetchToExecCommand);

        Assert.StartsWith("[DENIED]", result);
        Assert.Contains(DangerousCommandDetector.FetchToExec, result);
    }

    [Fact]
    public async Task RunScriptAsync_DangerousCommandInsideScript_IsDenied()
    {
        using var plugin = new ShellPlugin();

        var result = await plugin.RunScriptAsync($"echo starting\n{HarmlessRawDiskCommand}\necho done");

        Assert.StartsWith("[DENIED]", result);
    }

    [Fact]
    public async Task RunBackgroundAsync_DangerousCommand_IsDeniedAndNoJobStarts()
    {
        using var plugin = new ShellPlugin();

        var result = await plugin.RunBackgroundAsync(HarmlessRawDiskCommand);

        Assert.StartsWith("[DENIED]", result);
        Assert.DoesNotContain("Job ID:", result);
    }

    [Fact]
    public async Task RunAsync_DangerousCommand_IsDeniedBeforeAskingTheUser()
    {
        var asked = false;
        using var plugin = new ShellPlugin(approveCommand: _ => { asked = true; return Task.FromResult(true); });

        var result = await plugin.RunAsync(HarmlessRawDiskCommand);

        Assert.StartsWith("[DENIED]", result);
        Assert.False(asked, "a hard-denied command must not reach the approval prompt");
    }

    [Fact]
    public async Task RunAsync_OrdinaryRecursiveDelete_StillRuns()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuseraft_shellplugin_rm_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "f.txt"), "x");
        try
        {
            using var plugin = new ShellPlugin();

            var result = await plugin.RunAsync($"rm -rf \"{dir}\"");

            Assert.DoesNotContain("[DENIED]", result);
            Assert.False(Directory.Exists(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ShellPolicy matching tolerance

    [Theory]
    [InlineData("rm    -rf /tmp/fuseraft-test-nonexistent")]              // whitespace run
    [InlineData("rm\t-rf /tmp/fuseraft-test-nonexistent")]                // tab
    [InlineData("rm \\\n-rf /tmp/fuseraft-test-nonexistent")]             // line continuation
    [InlineData("r\u200Bm -rf /tmp/fuseraft-test-nonexistent")]           // zero-width character
    public async Task RunAsync_DenyPattern_IsNotSidesteppedByWhitespaceOrInvisibleChars(string command)
    {
        var policy = new ShellPolicy { Deny = ["rm -rf"] };
        using var plugin = new ShellPlugin(shellPolicy: policy);

        var result = await plugin.RunAsync(command);

        Assert.StartsWith("[DENIED]", result);
        Assert.Contains("deny pattern", result);
    }

    [Fact]
    public async Task RunAsync_AllowPattern_MatchesAcrossWhitespaceVariations()
    {
        var policy = new ShellPolicy { Allow = ["echo hello"] };
        using var plugin = new ShellPlugin(shellPolicy: policy);

        var result = await plugin.RunAsync("echo    hello");

        Assert.DoesNotContain("[DENIED]", result);
        Assert.Contains("hello", result);
    }

    [Fact]
    public async Task RunAsync_DenyPatternWithIntentionalTrailingSpace_IsNotBroadened()
    {
        var policy = new ShellPolicy { Deny = ["ls "] };
        using var plugin = new ShellPlugin(shellPolicy: policy);

        // Trimmed to a bare "ls" the pattern would match the tail of "tools".
        var result = await plugin.RunAsync("echo tools");

        Assert.DoesNotContain("[DENIED]", result);
    }

    // sudo — every spelling is denied end to end. `sudo -n` never prompts, so each of these is
    // harmless if the guard regressed and the command ran anyway.

    [Theory]
    [InlineData("sudo -n true")]
    [InlineData("ls; sudo -n true")]
    [InlineData("env sudo -n true")]
    [InlineData("command sudo -n true")]
    [InlineData("/usr/bin/sudo -n true")]
    [InlineData("(sudo -n true)")]
    [InlineData("echo $(sudo -n true)")]
    [InlineData("if true; then sudo -n true; fi")]
    [InlineData("'sudo' -n true")]
    [InlineData("\\sudo -n true")]
    public async Task RunAsync_Sudo_InAnySpelling_IsDenied(string command)
    {
        using var plugin = new ShellPlugin();

        var result = await plugin.RunAsync(command, timeoutSeconds: 10);

        Assert.StartsWith("[DENIED] sudo is not permitted.", result);
        Assert.Contains("non-privileged alternatives", result);
    }

    [Fact]
    public async Task RunAsync_Doas_IsDeniedByName()
    {
        using var plugin = new ShellPlugin();

        var result = await plugin.RunAsync("env doas -n true", timeoutSeconds: 10);

        Assert.StartsWith("[DENIED] doas is not permitted.", result);
    }

    [Fact]
    public async Task RunScriptAsync_SudoBehindAWrapperInsideAScript_IsDenied()
    {
        using var plugin = new ShellPlugin();

        var result = await plugin.RunScriptAsync("echo starting\nenv sudo -n true\necho done", timeoutSeconds: 10);

        Assert.StartsWith("[DENIED] sudo is not permitted.", result);
    }

    [Fact]
    public async Task RunBackgroundAsync_WrappedSudo_IsDeniedAndNoJobStarts()
    {
        using var plugin = new ShellPlugin();

        var result = await plugin.RunBackgroundAsync("nohup /usr/bin/sudo -n true");

        Assert.StartsWith("[DENIED] sudo is not permitted.", result);
        Assert.DoesNotContain("Job ID:", result);
    }

    [Fact]
    public async Task RunAsync_WrappedSudo_IsDeniedBeforeAskingTheUser()
    {
        var asked = false;
        using var plugin = new ShellPlugin(approveCommand: _ => { asked = true; return Task.FromResult(true); });

        var result = await plugin.RunAsync("env sudo -n true", timeoutSeconds: 10);

        Assert.StartsWith("[DENIED]", result);
        Assert.False(asked, "a hard-denied command must not reach the approval prompt");
    }

    [Fact]
    public async Task RunAsync_CommandsThatOnlyMentionSudo_StillRun()
    {
        using var plugin = new ShellPlugin();

        var result = await plugin.RunAsync("echo the word sudo appears here");

        Assert.DoesNotContain("[DENIED]", result);
        Assert.Contains("the word sudo appears here", result);
    }

    [Fact]
    public async Task RunAsync_PlainSudo_KeepsItsOriginalDenialWording()
    {
        // The exact text agents (and any saved prompts/skills) have seen since sudo was first blocked.
        using var plugin = new ShellPlugin();

        var result = await plugin.RunAsync("sudo -n true", timeoutSeconds: 10);

        Assert.Equal(
            "[DENIED] sudo is not permitted. " +
            "Prefer non-privileged alternatives: pip install --user, python -m pip install --user, " +
            "pipx, or a virtual environment (python -m venv .venv && .venv/bin/pip install ...). " +
            "If elevated privileges are truly required, tell the user exactly which command to run " +
            "and they will run it themselves.",
            result);
    }
}
