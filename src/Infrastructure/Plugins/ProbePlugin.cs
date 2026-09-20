using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using fuseraft.Core;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Gives agents a structured way to test hypotheses and validate assumptions:
/// run code snippets in various languages, assert expected outputs with clear
/// PASS/FAIL verdicts, and frame experiments as Given/When/Then hypotheses.
///
/// Complements <see cref="ShellPlugin"/> by adding assertion logic and structured
/// result formatting that helps agents reason about what to try next.
/// </summary>
///
/// <para>
/// Every process it launches runs through <see cref="ShellPlugin.VetAsync"/> and the shell's
/// working-directory sandbox first — the <c>sudo</c> block, dangerous-command and credential-file guards,
/// <c>ShellPolicy</c>, HITL approval — and its output has known secrets masked before any assertion
/// compares it. Probe used to call <c>ProcessHelper</c> directly, which made it an alternative shell
/// with none of those protections: <c>probe_code</c> could <c>cat .env</c>, print an API key from the
/// environment, run <c>sudo</c>, or work in any directory, whatever <c>shell_run</c> refused.
/// </para>
/// </summary>
public sealed class ProbePlugin : IDisposable
{
    private readonly ShellPlugin _shell;
    private readonly bool _ownsShell;

    /// <param name="shell">
    /// The configured <see cref="ShellPlugin"/> whose guards and sandbox apply. When omitted, a plugin
    /// with the built-in guards (<c>sudo</c>, dangerous commands, credential files) is created and owned.
    /// </param>
    public ProbePlugin(ShellPlugin? shell = null)
    {
        _ownsShell = shell is null;
        _shell     = shell ?? new ShellPlugin();
    }

    public void Dispose()
    {
        if (_ownsShell) _shell.Dispose();
    }

    // Vets, sandboxes the working directory, runs, and masks known secrets in the result — in that
    // order, for every process this plugin starts. The denial (a [DENIED] message) is non-null when
    // nothing was run.
    private async Task<(ProcessResult Result, string? Denial)> RunGuardedAsync(
        string vetText, bool shellSyntax, string? directory, Func<string?, Task<ProcessResult>> run)
    {
        var denial = await _shell.VetAsync(vetText, shellSyntax);
        if (denial is not null) return (default, denial);

        var dirDenial = _shell.ValidateDirectory(NormalizeDirectory(directory), out var resolvedDir);
        if (dirDenial is not null) return (default, dirDenial);

        var result = await run(resolvedDir);
        return (result with { Stdout = EnvSecretMasker.Mask(result.Stdout), Stderr = EnvSecretMasker.Mask(result.Stderr) }, null);
    }

    // Probe's tools default `directory` to ".", but with a sandbox that would resolve against the
    // process's working directory — outside the sandbox — and refuse every default call. shell_run's
    // default is null, meaning "the sandbox root"; "." and blank mean the same here.
    private static string? NormalizeDirectory(string? directory) =>
        string.IsNullOrWhiteSpace(directory) || directory == "." ? null : directory;

    private Task<(ProcessResult Result, string? Denial)> RunBashAsync(string command, string directory, int timeoutSeconds) =>
        RunGuardedAsync(command, shellSyntax: true, directory,
            dir => ProcessHelper.RunAsync("bash", ["-c", command], dir, timeoutSeconds));

    // Supported language runners.
    // InlineFlag: the flag used to pass inline code (e.g. -c, -e). Null for temp-file runners.
    // TempFileArg: the leading argument before the temp-file path. Null for inline runners.
    private static readonly IReadOnlyDictionary<string, LanguageRunner> Runners =
        new Dictionary<string, LanguageRunner>(StringComparer.OrdinalIgnoreCase)
        {
            ["bash"]       = new("bash",    false, "",     "-c",       null),
            ["sh"]         = new("bash",    false, "",     "-c",       null),
            ["python"]     = new("python3", false, "",     "-c",       null),
            ["python3"]    = new("python3", false, "",     "-c",       null),
            ["py"]         = new("python3", false, "",     "-c",       null),
            ["node"]       = new("node",    false, "",     "-e",       null),
            ["javascript"] = new("node",    false, "",     "-e",       null),
            ["js"]         = new("node",    false, "",     "-e",       null),
            ["powershell"] = new("pwsh",    false, "",     "-Command", null),
            ["ps"]         = new("pwsh",    false, "",     "-Command", null),
            ["kiwi"]       = new("kiwi",    false, "",     "-e",       null),
            ["go"]         = new("go",      true,  ".go",  null,       "run"),
            ["csharp"]     = new("dotnet",  true,  ".csx", null,       "script"),
            ["cs"]         = new("dotnet",  true,  ".csx", null,       "script"),
        };

    // probe_code

    [Description("Execute a code snippet and return the result.")]
    public async Task<string> ProbeCodeAsync(
        [Description("Language: bash, python, js, go, csharp, powershell, kiwi.")] string language,
        [Description("Code snippet.")] string code,
        [Description("Working directory.")] string directory = ".",
        [Description("Timeout in seconds.")] int timeoutSeconds = 30)
    {
        if (!Runners.TryGetValue(language, out var runner))
        {
            var supported = string.Join(", ", Runners.Keys.Distinct(StringComparer.OrdinalIgnoreCase));
            return PluginResult.Error($"Unsupported language '{language}'. Supported: {supported}");
        }

        // "pwsh" (PowerShell 7+) isn't installed by default on plain Windows Server/desktop
        // images — only Windows PowerShell 5.1 is guaranteed present. Resolve to whichever
        // actually exists rather than failing outright on a hardcoded "pwsh".
        var executable = OperatingSystem.IsWindows() &&
                          (language.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
                           language.Equals("ps", StringComparison.OrdinalIgnoreCase))
            ? ProcessHelper.WindowsPowerShellPath.Value
            : runner.Executable;

        string tempFile = string.Empty;

        try
        {
            // Only bash/sh snippets are POSIX shell text; for Python, Node, Go, ... the shell-specific
            // rules don't apply, but a snippet that opens ~/.ssh/id_rsa or a .env still must not run.
            var shellSyntax = runner.Executable == "bash";

            var (result, denial) = await RunGuardedAsync(code, shellSyntax, directory, async dir =>
            {
                if (runner.UseTempFile)
                {
                    tempFile = FuseraftPaths.NewTempFile("probe", runner.TempExtension);
                    await File.WriteAllTextAsync(tempFile, code);
                    // Pass the temp-file path as a separate argument — no quoting needed.
                    return await ProcessHelper.RunAsync(
                        executable, [runner.TempFileArg!, tempFile], dir, timeoutSeconds);
                }

                // Pass code as a single argv element — avoids fragile manual quote-escaping
                // that breaks when code contains trailing backslashes or nested quotes.
                return await ProcessHelper.RunAsync(
                    executable, [runner.InlineFlag!, code], dir, timeoutSeconds);
            });
            if (denial is not null) return denial;

            return FormatProbeResult(language, code, result);
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempFile) && File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    // assert_output

    [Description("Run a command and assert its output. Returns PASS/FAIL.")]
    public async Task<string> AssertOutputAsync(
        [Description("Shell command.")] string command,
        [Description("Expected value.")] string expected,
        [Description("Match type: contains, equals, regex, exitcode.")] string matchType = "contains",
        [Description("Working directory.")] string directory = ".",
        [Description("Timeout in seconds.")] int timeoutSeconds = 30)
    {
        // Masked before it is compared: a PASS/FAIL verdict on `contains`/`equals`/`regex` would otherwise
        // let a caller guess a masked secret one character at a time.
        var (result, denial) = await RunBashAsync(command, directory, timeoutSeconds);
        if (denial is not null) return denial;
        var actual = result.Stdout.TrimEnd();
        // Some models HTML-encode characters in tool arguments (e.g. &lt; for <).
        expected = System.Net.WebUtility.HtmlDecode(expected);

        var (passed, reason) = matchType.ToLowerInvariant() switch
        {
            "contains"  => actual.Contains(expected, StringComparison.OrdinalIgnoreCase)
                            ? (true,  $"Output contains '{expected}'.")
                            : (false, $"Output does not contain '{expected}'.\n  Actual output:\n{Indent(actual)}"),

            "equals"    => string.Equals(actual.Trim(), expected.Trim(), StringComparison.Ordinal)
                            ? (true,  "Output matches exactly.")
                            : (false, BuildDiff(expected.Trim(), actual.Trim())),

            "regex"     => MatchRegex(expected, actual, out var regexReason)
                            ? (true,  regexReason)
                            : (false, regexReason),

            "exitcode"  => int.TryParse(expected, out var code) && result.ExitCode == code
                            ? (true,  $"Exit code is {code} as expected.")
                            : (false, $"Expected exit code {expected}, got {result.ExitCode}."),

            _ => (false, $"Unknown matchType '{matchType}'. Use: contains, equals, regex, exitcode.")
        };

        var sb = new StringBuilder();
        sb.AppendLine($"COMMAND  : {command}");
        sb.AppendLine($"EXPECTED : {expected}  [{matchType}]");
        sb.AppendLine($"EXIT CODE: {result.ExitCode}");

        if (!string.IsNullOrWhiteSpace(result.Stderr))
            sb.AppendLine($"STDERR   :\n{Indent(result.Stderr.TrimEnd())}");

        sb.AppendLine();
        sb.AppendLine(passed ? "VERDICT: PASS" : "VERDICT: FAIL");
        sb.AppendLine($"REASON : {reason}");

        return sb.ToString().TrimEnd();
    }

    // compare_outputs

    [Description("Run two commands and display their outputs side by side.")]
    public async Task<string> CompareOutputsAsync(
        [Description("Command A.")] string commandA,
        [Description("Command B.")] string commandB,
        [Description("Working directory.")] string directory = ".",
        [Description("Timeout in seconds.")] int timeoutSeconds = 30)
    {
        // Both are vetted before either runs, so a refused B can't leave A's side effects behind.
        var denialA = await _shell.VetAsync(commandA);
        if (denialA is not null) return denialA;
        var denialB = await _shell.VetAsync(commandB);
        if (denialB is not null) return denialB;

        var dirDenial = _shell.ValidateDirectory(NormalizeDirectory(directory), out var resolvedDir);
        if (dirDenial is not null) return dirDenial;

        var taskA = ProcessHelper.RunAsync("bash", ["-c", commandA], resolvedDir, timeoutSeconds);
        var taskB = ProcessHelper.RunAsync("bash", ["-c", commandB], resolvedDir, timeoutSeconds);
        await Task.WhenAll(taskA, taskB);
        var resultA = taskA.Result with { Stdout = EnvSecretMasker.Mask(taskA.Result.Stdout), Stderr = EnvSecretMasker.Mask(taskA.Result.Stderr) };
        var resultB = taskB.Result with { Stdout = EnvSecretMasker.Mask(taskB.Result.Stdout), Stderr = EnvSecretMasker.Mask(taskB.Result.Stderr) };

        var sb = new StringBuilder();
        sb.AppendLine("=== A ===");
        sb.AppendLine($"COMMAND  : {commandA}");
        sb.AppendLine($"EXIT CODE: {resultA.ExitCode}");
        sb.AppendLine($"OUTPUT   :\n{Indent(resultA.Stdout.TrimEnd())}");
        if (!string.IsNullOrWhiteSpace(resultA.Stderr))
            sb.AppendLine($"STDERR   :\n{Indent(resultA.Stderr.TrimEnd())}");

        sb.AppendLine();
        sb.AppendLine("=== B ===");
        sb.AppendLine($"COMMAND  : {commandB}");
        sb.AppendLine($"EXIT CODE: {resultB.ExitCode}");
        sb.AppendLine($"OUTPUT   :\n{Indent(resultB.Stdout.TrimEnd())}");
        if (!string.IsNullOrWhiteSpace(resultB.Stderr))
            sb.AppendLine($"STDERR   :\n{Indent(resultB.Stderr.TrimEnd())}");

        sb.AppendLine();
        var outputsMatch = string.Equals(resultA.Stdout.Trim(), resultB.Stdout.Trim(), StringComparison.Ordinal);
        sb.AppendLine(outputsMatch ? "OUTPUTS MATCH: yes" : "OUTPUTS MATCH: no");

        return sb.ToString().TrimEnd();
    }

    // run_hypothesis

    [Description("Test a hypothesis with a Given/When/Then structure. Returns PASS/FAIL.")]
    public async Task<string> RunHypothesisAsync(
        [Description("The hypothesis to test.")] string hypothesis,
        [Description("Command to probe the hypothesis.")] string command,
        [Description("Expected output if hypothesis is correct.")] string expectedObservation,
        [Description("Optional setup command.")] string setupCommand = "",
        [Description("Working directory.")] string directory = ".",
        [Description("Timeout in seconds.")] int timeoutSeconds = 30)
    {
        var sb = new StringBuilder();
        sb.AppendLine("HYPOTHESIS");
        sb.AppendLine($"  {hypothesis}");
        sb.AppendLine();

        // Optional setup
        if (!string.IsNullOrWhiteSpace(setupCommand))
        {
            sb.AppendLine("SETUP");
            var (setup, setupDenial) = await RunBashAsync(setupCommand, directory, timeoutSeconds);
            if (setupDenial is not null) return setupDenial;
            sb.AppendLine($"  COMMAND  : {setupCommand}");
            sb.AppendLine($"  EXIT CODE: {setup.ExitCode}");
            if (!string.IsNullOrWhiteSpace(setup.Stdout))
                sb.AppendLine($"  OUTPUT   : {setup.Stdout.Trim()}");
            if (!string.IsNullOrWhiteSpace(setup.Stderr))
                sb.AppendLine($"  STDERR   : {setup.Stderr.Trim()}");
            if (!setup.Succeeded)
            {
                sb.AppendLine();
                sb.AppendLine("VERDICT: FAIL");
                sb.AppendLine("REASON : Setup command failed. Probe was not run.");
                return sb.ToString().TrimEnd();
            }
            sb.AppendLine();
        }

        // Probe
        sb.AppendLine("PROBE");
        var (probe, probeDenial) = await RunBashAsync(command, directory, timeoutSeconds);
        if (probeDenial is not null) return probeDenial;
        sb.AppendLine($"  COMMAND  : {command}");
        sb.AppendLine($"  EXIT CODE: {probe.ExitCode}");

        var output = probe.Stdout.TrimEnd();
        if (!string.IsNullOrWhiteSpace(output))
            sb.AppendLine($"  OUTPUT   :\n{Indent(output, "    ")}");
        if (!string.IsNullOrWhiteSpace(probe.Stderr))
            sb.AppendLine($"  STDERR   :\n{Indent(probe.Stderr.TrimEnd(), "    ")}");

        sb.AppendLine();

        // Verdict
        var passed = output.Contains(expectedObservation, StringComparison.OrdinalIgnoreCase);
        sb.AppendLine("EXPECTED OBSERVATION");
        sb.AppendLine($"  {expectedObservation}");
        sb.AppendLine();
        sb.AppendLine(passed ? "VERDICT: PASS" : "VERDICT: FAIL");
        sb.AppendLine(passed
            ? $"REASON : Output contains the expected observation."
            : $"REASON : Output does not contain '{expectedObservation}'. The hypothesis may be incorrect, or the probe needs refinement.");

        return sb.ToString().TrimEnd();
    }

    // Helpers

    private static string FormatProbeResult(string language, string code, ProcessResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"LANGUAGE : {language}");
        sb.AppendLine($"EXIT CODE: {result.ExitCode}");

        if (!string.IsNullOrWhiteSpace(result.Stdout))
        {
            sb.AppendLine("STDOUT:");
            sb.AppendLine(Indent(result.Stdout.TrimEnd()));
        }
        else
        {
            sb.AppendLine("STDOUT   : (empty)");
        }

        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            sb.AppendLine("STDERR:");
            sb.AppendLine(Indent(result.Stderr.TrimEnd()));
        }

        return sb.ToString().TrimEnd();
    }

    private static bool MatchRegex(string pattern, string actual, out string reason)
    {
        try
        {
            var match = Regex.IsMatch(actual, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            reason = match
                ? $"Output matches regex '{pattern}'."
                : $"Output does not match regex '{pattern}'.\n  Actual output:\n{Indent(actual)}";
            return match;
        }
        catch (ArgumentException ex)
        {
            reason = $"Invalid regex pattern: {ex.Message}";
            return false;
        }
    }

    private static string BuildDiff(string expected, string actual)
    {
        var sb = new StringBuilder("Outputs differ.\n");
        sb.AppendLine("  EXPECTED:");
        sb.AppendLine(Indent(expected));
        sb.AppendLine("  ACTUAL:");
        sb.AppendLine(Indent(actual));
        return sb.ToString().TrimEnd();
    }

    private static string Indent(string text, string prefix = "  ") =>
        string.Join("\n", text.Split('\n').Select(l => prefix + l));

    private readonly record struct LanguageRunner(
        string Executable,
        bool UseTempFile,
        string TempExtension,
        string? InlineFlag,    // flag for inline code (e.g. "-c", "-e"); null for temp-file runners
        string? TempFileArg);  // leading arg before the temp-file path (e.g. "run", "script"); null for inline runners
}
