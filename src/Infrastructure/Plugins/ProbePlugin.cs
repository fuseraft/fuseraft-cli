using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Gives agents a structured way to test hypotheses and validate assumptions:
/// run code snippets in various languages, assert expected outputs with clear
/// PASS/FAIL verdicts, and frame experiments as Given/When/Then hypotheses.
///
/// Complements <see cref="ShellPlugin"/> by adding assertion logic and structured
/// result formatting that helps agents reason about what to try next.
/// </summary>
public sealed class ProbePlugin
{
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

        string tempFile = string.Empty;

        try
        {
            ProcessResult result;

            if (runner.UseTempFile)
            {
                tempFile = Path.Combine(Path.GetTempPath(), $"fuseraft_probe_{Guid.NewGuid():N}{runner.TempExtension}");
                await File.WriteAllTextAsync(tempFile, code);
                // Pass the temp-file path as a separate argument — no quoting needed.
                result = await ProcessHelper.RunAsync(
                    runner.Executable, [runner.TempFileArg!, tempFile], directory, timeoutSeconds);
            }
            else
            {
                // Pass code as a single argv element — avoids fragile manual quote-escaping
                // that breaks when code contains trailing backslashes or nested quotes.
                result = await ProcessHelper.RunAsync(
                    runner.Executable, [runner.InlineFlag!, code], directory, timeoutSeconds);
            }

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
        var result = await ProcessHelper.RunAsync("bash", ["-c", command], directory, timeoutSeconds);
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
        var taskA = ProcessHelper.RunAsync("bash", ["-c", commandA], directory, timeoutSeconds);
        var taskB = ProcessHelper.RunAsync("bash", ["-c", commandB], directory, timeoutSeconds);
        await Task.WhenAll(taskA, taskB);
        var resultA = taskA.Result;
        var resultB = taskB.Result;

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
            var setup = await ProcessHelper.RunAsync("bash", ["-c", setupCommand], directory, timeoutSeconds);
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
        var probe = await ProcessHelper.RunAsync("bash", ["-c", command], directory, timeoutSeconds);
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
