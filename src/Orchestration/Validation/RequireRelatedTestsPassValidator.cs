using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Orchestration.Validation;

/// <summary>
/// Blocks a handoff route unless targeted tests covering the current turn's changed files
/// pass. Implements the incremental test-selection loop described in the brownfield guide:
/// <list type="number">
///   <item>Resolve changed files from the current session's change log.</item>
///   <item>Run <see cref="TestSelectorConfig.FindRelatedCommand"/> (with <c>{file}</c> substituted)
///         for each changed file to discover related test targets.</item>
///   <item>Run the discovered targets — or fall back to <see cref="TestSelectorConfig.FullSuiteCommand"/>
///         when discovery returns nothing.</item>
///   <item>Return <see cref="RoutingValidationResult.Pass()"/> only when the test command exits 0.</item>
/// </list>
/// </summary>
public sealed class RequireRelatedTestsPassValidator(
    TestSelectorConfig testSelector,
    string? changeLogPath = null,
    string? sandboxRoot = null) : IRoutingValidator
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<RoutingValidationResult> ValidateAsync(
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        var changedFiles = await ResolveChangedFilesAsync(cancellationToken);

        var testTargets = await DiscoverRelatedTestsAsync(changedFiles, cancellationToken);

        string? testCommand;
        if (testTargets.Count > 0 && !string.IsNullOrWhiteSpace(testSelector.FullSuiteCommand))
        {
            // Append discovered targets as space-separated arguments to the full-suite runner.
            // This works for pytest (accepts file paths) and go test (accepts package paths).
            testCommand = testSelector.FullSuiteCommand.TrimEnd()
                + " " + string.Join(" ", testTargets.Select(QuoteArg));
        }
        else if (!string.IsNullOrWhiteSpace(testSelector.FullSuiteCommand))
        {
            // No related tests found — run the full suite as fallback.
            testCommand = testSelector.FullSuiteCommand;
        }
        else
        {
            return RoutingValidationResult.Fail(
                "Handoff blocked: RequireRelatedTestsPass has no test command to run.\n\n" +
                "Set TestSelector.FullSuiteCommand in the orchestration config.");
        }

        var result = await RunShellAsync(testCommand, cancellationToken);

        if (!result.Succeeded)
        {
            return RoutingValidationResult.Fail(
                $"Handoff blocked: targeted tests failed (exit {result.ExitCode}).\n\n" +
                $"Command: {testCommand}\n\n" +
                TrimOutput(result.Stdout, result.Stderr));
        }

        return RoutingValidationResult.Pass();
    }

    // Reads changed files from the most recent change-log entry for the active session.
    private async Task<IReadOnlyList<string>> ResolveChangedFilesAsync(CancellationToken ct)
    {
        if (changeLogPath is null || !File.Exists(changeLogPath))
            return [];

        try
        {
            var json = await File.ReadAllTextAsync(changeLogPath, ct);
            var log  = JsonSerializer.Deserialize<ChangeLog>(json, JsonOpts);
            if (log is null) return [];

            var sessionId   = log.ActiveSessionId;
            var recentEntry = log.Entries
                .Where(e => sessionId is null || e.SessionId == sessionId)
                .OrderByDescending(e => e.TurnIndex)
                .FirstOrDefault();

            return recentEntry?.FilesWritten ?? [];
        }
        catch
        {
            return [];
        }
    }

    // Runs FindRelatedCommand for each changed file and collects unique non-empty output lines.
    private async Task<IReadOnlyList<string>> DiscoverRelatedTestsAsync(
        IReadOnlyList<string> changedFiles,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(testSelector.FindRelatedCommand) || changedFiles.Count == 0)
            return [];

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in changedFiles)
        {
            var cmd    = testSelector.FindRelatedCommand.Replace("{file}", file, StringComparison.Ordinal);
            var result = await RunShellAsync(cmd, ct);

            if (!result.Succeeded) continue;

            foreach (var line in result.Stdout.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                    targets.Add(trimmed);
            }
        }

        return [..targets];
    }

    private async Task<ProcessResult> RunShellAsync(string command, CancellationToken ct)
    {
        var (shell, flag) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ("cmd.exe", "/c")
            : ("bash", "-c");

        return await ProcessHelper.RunAsync(
            shell,
            $"{flag} {command}",
            workingDirectory: sandboxRoot,
            timeoutSeconds:   120,
            cancellationToken: ct);
    }

    private static string QuoteArg(string arg)
    {
        // Wrap in double quotes if the arg contains spaces and is not already quoted.
        if (arg.Contains(' ') && !arg.StartsWith('"') && !arg.StartsWith('\''))
            return $"\"{arg}\"";
        return arg;
    }

    private static string TrimOutput(string stdout, string stderr)
    {
        const int cap = 2_000;
        var combined = string.Join("\n", new[] { stdout, stderr }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.TrimEnd()));

        return combined.Length > cap
            ? combined[..cap] + "\n[output truncated]"
            : combined;
    }
}
