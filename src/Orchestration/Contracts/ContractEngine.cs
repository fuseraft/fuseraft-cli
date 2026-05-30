using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration.Validation;

namespace fuseraft.Orchestration.Contracts;

/// <summary>
/// Evaluates named <see cref="ContractConfig"/> definitions against observable evidence
/// (disk artifacts, the change log, and the evidence graph) without making any LLM calls.
///
/// <para>
/// Contracts are composable, reusable transition gates. Each predicate in a contract's
/// <c>Requires</c> list must hold for the contract to be satisfied. All predicates use
/// AND semantics — a single failing predicate fails the whole contract.
/// </para>
///
/// <para>
/// When an <see cref="EvidenceStore"/> is configured, predicates query the typed evidence
/// graph for richer, more accurate results. When not configured, predicates fall back to
/// reading the flat <c>changes.json</c> log directly.
/// </para>
///
/// <para>
/// Call <see cref="EvaluateAsync"/> from a <see cref="ContractValidator"/> to integrate
/// contracts into the existing routing validation pipeline.
/// </para>
/// </summary>
public sealed class ContractEngine
{
    private readonly IReadOnlyDictionary<string, ContractConfig> _contracts;
    private readonly ValidationConfig? _validationConfig;
    private readonly EvidenceStore? _evidenceStore;
    private readonly TestSelectorConfig? _testSelector;
    private readonly string? _sandboxRoot;
    private readonly string _sessionId;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    public ContractEngine(
        IEnumerable<ContractConfig> contracts,
        ValidationConfig? validationConfig = null,
        EvidenceStore? evidenceStore = null,
        TestSelectorConfig? testSelector = null,
        string? sandboxRoot = null,
        string? sessionId = null)
    {
        _contracts        = contracts.ToDictionary(
            c => c.Name,
            c => c,
            StringComparer.OrdinalIgnoreCase);
        _validationConfig = validationConfig;
        _evidenceStore    = evidenceStore;
        _testSelector     = testSelector;
        _sandboxRoot      = sandboxRoot;
        _sessionId        = sessionId ?? string.Empty;
    }

    private string Expand(string path) => FuseraftPaths.ExpandSessionId(path, _sessionId);

    /// <summary>Names of all contracts known to this engine.</summary>
    public IReadOnlyList<string> ContractNames => [.. _contracts.Keys];

    /// <summary>The evidence store this engine queries. Exposed for snapshot use.</summary>
    internal EvidenceStore? EvidenceStore => _evidenceStore;

    /// <summary>
    /// Evaluates all predicates in the named contract.
    /// Returns <c>(true, null)</c> when all predicates pass; otherwise returns
    /// <c>(false, errorMessage)</c> with a human-readable explanation of the first
    /// failing predicate.
    /// </summary>
    public async Task<(bool Satisfied, string? ErrorMessage)> EvaluateAsync(
        string contractName,
        CancellationToken cancellationToken = default)
    {
        if (!_contracts.TryGetValue(contractName, out var contract))
            return (false,
                $"Contract error: unknown contract '{contractName}'. Verify it is declared under 'Orchestration.Contracts'.");

        foreach (var predicate in contract.Requires)
        {
            var (ok, msg) = await EvaluatePredicateAsync(predicate, contractName, cancellationToken);
            if (!ok)
                return (false, msg);
        }

        return (true, null);
    }

    // Dispatches to the appropriate predicate evaluator based on Type.
    private async Task<(bool Ok, string? Error)> EvaluatePredicateAsync(
        ContractPredicate pred,
        string contractName,
        CancellationToken cancellationToken)
    {
        return pred.Type.ToLowerInvariant() switch
        {
            "fileswritten"       => await EvaluateFilesWrittenAsync(pred, contractName, cancellationToken),
            "commandsucceeded"   => await EvaluateCommandSucceededAsync(pred, contractName, cancellationToken),
            "fileexists"         => EvaluateFileExists(pred, contractName),
            "testreport"         => await EvaluateTestReportAsync(pred, contractName, cancellationToken),
            "relatedtestspass"   => await EvaluateRelatedTestsPassAsync(contractName, cancellationToken),
            _ => (false, $"Contract '{contractName}' error: unknown predicate type '{pred.Type}'. Valid: FilesWritten, CommandSucceeded, FileExists, TestReport, RelatedTestsPass.")
        };
    }

    // FilesWritten

    private async Task<(bool, string?)> EvaluateFilesWrittenAsync(
        ContractPredicate pred,
        string contractName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pred.Source) || string.IsNullOrWhiteSpace(pred.Field))
            return (false,
                $"Contract '{contractName}' config error: FilesWritten requires 'Source' (JSON path) and 'Field' (array field name).");

        var source = Expand(pred.Source);

        if (!File.Exists(source))
            return (false,
                $"Contract '{contractName}' failed: FilesWritten source '{source}' does not exist. Write it before handing off.");

        // Parse the source file and extract the array field.
        List<string> expectedPaths;
        try
        {
            var raw = await File.ReadAllTextAsync(source, ct);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (!root.TryGetProperty(pred.Field, out var fieldEl) &&
                !root.TryGetProperty(pred.Field.ToLowerInvariant(), out fieldEl))
            {
                return (false,
                    $"Contract '{contractName}' failed: '{source}' has no field '{pred.Field}'.");
            }

            expectedPaths = [];
            foreach (var item in fieldEl.EnumerateArray())
            {
                // Support both plain strings and objects with a "path" property.
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) expectedPaths.Add(PathHelpers.NormalizePath(s));
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    if (item.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String)
                    {
                        var s = pathEl.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) expectedPaths.Add(PathHelpers.NormalizePath(s));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return (false,
                $"Contract '{contractName}' error: could not parse '{source}': {ex.Message}");
        }

        if (expectedPaths.Count == 0)
            return (true, null); // nothing to check

        // Load written files from the change log.
        var written = await LoadWrittenFilesAsync(ct);

        var missing = expectedPaths
            .Where(req => !written.Any(w => PathHelpers.PathsMatch(w, req)) && !File.Exists(req))
            .ToList();

        if (missing.Count == 0)
            return (true, null);

        return (false,
            $"Contract '{contractName}' failed — files from '{source}'['{pred.Field}'] not written:\n" +
            string.Join("\n", missing.Select(f => $"  ✗ {f}")) +
            "\n\nWrite them with write_file before handing off.");
    }

    // CommandSucceeded

    private async Task<(bool, string?)> EvaluateCommandSucceededAsync(
        ContractPredicate pred,
        string contractName,
        CancellationToken ct)
    {
        // Resolve the match pattern — either a literal value or read from a JSON field.
        string? pattern = pred.Pattern;

        if (!string.IsNullOrWhiteSpace(pred.PatternField))
        {
            var sourcePath = Expand(pred.PatternSource
                ?? _validationConfig?.BriefPath
                ?? FuseraftPaths.LocalBrief);

            if (!File.Exists(sourcePath))
                return (false,
                    $"Contract '{contractName}' failed: PatternSource '{sourcePath}' does not exist. " +
                    $"The Planner must write '{pred.PatternField}' to this file before the Developer hands off.");

            try
            {
                var raw = await File.ReadAllTextAsync(sourcePath, ct);
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (!root.TryGetProperty(pred.PatternField, out var fieldEl) &&
                    !root.TryGetProperty(pred.PatternField.ToLowerInvariant(), out fieldEl))
                {
                    return (false,
                        $"Contract '{contractName}' failed: '{sourcePath}' has no field '{pred.PatternField}'. " +
                        $"The Planner must include '{pred.PatternField}' in the brief (e.g. the command to check or run the implementation).");
                }

                pattern = fieldEl.ValueKind == JsonValueKind.String ? fieldEl.GetString() : null;
            }
            catch (Exception ex)
            {
                return (false,
                    $"Contract '{contractName}' error: could not read '{sourcePath}': {ex.Message}");
            }
        }

        // When PatternField was specified but resolved to nothing, fall back to the literal
        // Pattern if one was also provided — this lets configs provide both as a safety net.
        if (string.IsNullOrWhiteSpace(pattern) && !string.IsNullOrWhiteSpace(pred.Pattern))
            pattern = pred.Pattern;

        if (string.IsNullOrWhiteSpace(pattern))
            return (false,
                $"Contract '{contractName}' config error: CommandSucceeded requires 'Pattern' or 'PatternField' (pointing to a non-empty string field in the brief).");

        var patterns = pattern.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var commands  = await LoadSucceededCommandsAsync(ct);

        bool found = commands.Any(cmd =>
            patterns.Any(p => cmd.Contains(p, StringComparison.OrdinalIgnoreCase)));

        if (found)
            return (true, null);

        var resolvedFrom = pred.PatternField is not null
            ? $" (read from '{Expand(pred.PatternSource ?? FuseraftPaths.LocalBrief)}' field '{pred.PatternField}')"
            : string.Empty;

        return (false,
            $"Contract '{contractName}' failed — no shell command matching '{pattern}'{resolvedFrom} in session log.\n\n" +
            $"Run `{pattern}` with shell_run and confirm it exits 0 before handing off.");
    }

    // FileExists

    private (bool, string?) EvaluateFileExists(ContractPredicate pred, string contractName)
    {
        if (string.IsNullOrWhiteSpace(pred.Path))
            return (false,
                $"Contract '{contractName}' config error: FileExists requires 'Path'.");

        var path = Expand(pred.Path);

        if (File.Exists(path))
            return (true, null);

        return (false,
            $"Contract '{contractName}' failed — '{path}' does not exist. Create it before handing off.");
    }

    // TestReport

    private async Task<(bool, string?)> EvaluateTestReportAsync(
        ContractPredicate pred,
        string contractName,
        CancellationToken ct)
    {
        var reportPath = _validationConfig?.TestReportPath ?? FuseraftPaths.LocalTestReport;

        if (!File.Exists(reportPath))
        {
            var criteria = await TryReadAcceptanceCriteriaAsync(ct);
            return (false,
                $"Contract '{contractName}' failed — test report '{reportPath}' not found.\n\n" +
                $"Write it with write_file using this schema:\n" +
                BuildTestReportTemplate(reportPath) +
                FormatCriteriaHint(criteria, "You need one result entry per criterion."));
        }

        TestReportDoc? report;
        try
        {
            var raw = await File.ReadAllTextAsync(reportPath, ct);
            report = JsonSerializer.Deserialize<TestReportDoc>(raw, JsonOpts);
        }
        catch (Exception ex)
        {
            return (false,
                $"Contract '{contractName}' error: could not parse test report '{reportPath}': {ex.Message}");
        }

        if (report?.Results is null || report.Results.Count == 0)
        {
            var criteria = await TryReadAcceptanceCriteriaAsync(ct);
            var need = criteria.Count > 0 ? $"{criteria.Count}" : "one";
            return (false,
                $"Contract '{contractName}' failed — test report '{reportPath}' has no results.\n\n" +
                $"The 'results' array is empty or missing. Patch it with {need} entr{(criteria.Count == 1 ? "y" : "ies")} — " +
                $"one per acceptance criterion:\n" +
                BuildTestReportTemplate(reportPath) +
                FormatCriteriaHint(criteria, $"You need {need} result(s)."));
        }

        if (pred.NoFailures == true)
        {
            var failures = report.Results
                .Where(r => string.Equals(r.Status, "FAIL", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (failures.Count > 0)
                return (false,
                    $"Contract '{contractName}' failed — test report contains {failures.Count} FAIL result(s):\n" +
                    string.Join("\n", failures.Select(f => $"  ✗ {f.Criterion ?? "(unnamed)"}: {f.Status}")));
        }

        if (pred.HasAssertions == true && _validationConfig?.ChangeLogPath is { } logPath)
        {
            var succeededCommands = await LoadSucceededCommandsAsync(ct);
            var reportCommands    = report.Results
                .SelectMany(r => new[] { r.Command, r.Evidence })
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c!)
                .ToList();

            bool anyVerified = reportCommands.Any(rc =>
                succeededCommands.Any(sc =>
                    sc.Contains(rc, StringComparison.OrdinalIgnoreCase) ||
                    rc.Contains(sc, StringComparison.OrdinalIgnoreCase)));

            if (!anyVerified && succeededCommands.Count > 0)
                return (false,
                    $"Contract '{contractName}' failed — test report commands not found in session log (possible fabrication). Re-run tests with shell_run and update the report.");
        }

        return (true, null);
    }

    // TestReport message helpers

    // Reads acceptance_criteria from brief.json (best-effort; returns empty on any error).
    private async Task<List<string>> TryReadAcceptanceCriteriaAsync(CancellationToken ct)
    {
        var briefPath = _validationConfig?.BriefPath ?? FuseraftPaths.LocalBrief;
        if (!File.Exists(briefPath)) return [];

        try
        {
            var raw = await File.ReadAllTextAsync(briefPath, ct);
            using var doc = JsonDocument.Parse(raw);

            // Accept both camelCase and PascalCase property names.
            if (!doc.RootElement.TryGetProperty("acceptance_criteria", out var arr) &&
                !doc.RootElement.TryGetProperty("AcceptanceCriteria", out arr))
                return [];

            return arr.EnumerateArray()
                .Where(el => el.ValueKind == JsonValueKind.String)
                .Select(el => el.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
        catch { return []; }
    }

    private static string BuildTestReportTemplate(string reportPath) =>
        $"\n  write_file(\"{reportPath}\",\n" +
        "  {\n" +
        "    \"results\": [\n" +
        "      {\n" +
        "        \"criterion\": \"<exact text from brief.json acceptance_criteria>\",\n" +
        "        \"status\": \"PASS\",\n" +
        "        \"command\": \"<exact shell command you ran to verify it>\",\n" +
        "        \"exit_code\": 0\n" +
        "      }\n" +
        "    ],\n" +
        "    \"fake_test_files\": []\n" +
        "  })";

    private static string FormatCriteriaHint(List<string> criteria, string countNote)
    {
        if (criteria.Count == 0) return string.Empty;
        return $"\n\nbrief.json acceptance_criteria ({criteria.Count} — {countNote}):\n" +
               string.Join("\n", criteria.Select((c, i) => $"  {i + 1}. {c}"));
    }

    // RelatedTestsPass

    private async Task<(bool, string?)> EvaluateRelatedTestsPassAsync(
        string contractName,
        CancellationToken ct)
    {
        if (_testSelector is not { FindRelatedCommand.Length: > 0 })
            return (false,
                $"Contract '{contractName}' config error: RelatedTestsPass requires " +
                "TestSelector.FindRelatedCommand to be set at the orchestration level.");

        if (string.IsNullOrWhiteSpace(_testSelector.FullSuiteCommand))
            return (false,
                $"Contract '{contractName}' config error: RelatedTestsPass requires " +
                "TestSelector.FullSuiteCommand so it has a test runner command to execute.");

        // Resolve changed files for the current session.
        var changedFiles = await LoadChangedFilesAsync(ct);

        // Discover related test targets for each changed file.
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in changedFiles)
        {
            var cmd    = _testSelector.FindRelatedCommand.Replace("{file}", file, StringComparison.Ordinal);
            var result = await RunShellAsync(cmd, ct);
            if (!result.Succeeded) continue;
            foreach (var line in result.Stdout.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length > 0) targets.Add(t);
            }
        }

        string testCommand = targets.Count > 0
            ? _testSelector.FullSuiteCommand.TrimEnd() + " " +
              string.Join(" ", targets.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))
            : _testSelector.FullSuiteCommand;

        var testResult = await RunShellAsync(testCommand, ct);

        if (!testResult.Succeeded)
        {
            var combined = string.Join("\n", new[] { testResult.Stdout, testResult.Stderr }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.TrimEnd()));
            const int cap = 2_000;
            if (combined.Length > cap) combined = combined[..cap] + "\n[output truncated]";

            return (false,
                $"Contract '{contractName}' failed — targeted tests did not pass (exit {testResult.ExitCode}).\n\n" +
                $"Command: {testCommand}\n\n{combined}");
        }

        return (true, null);
    }

    private async Task<IReadOnlyList<string>> LoadChangedFilesAsync(CancellationToken ct)
    {
        var logPath = _validationConfig?.ChangeLogPath;
        if (logPath is null || !System.IO.File.Exists(logPath)) return [];

        try
        {
            var raw = await System.IO.File.ReadAllTextAsync(logPath, ct);
            var log = JsonSerializer.Deserialize<ChangeLogDoc>(raw, JsonOpts) ?? new ChangeLogDoc();
            var sessionId = log.ActiveSessionId;
            var entry = (sessionId is not null
                ? log.Entries.Where(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal))
                : (IEnumerable<ChangeEntryDoc>)log.Entries)
                .OrderByDescending(e => e.TurnIndex)
                .FirstOrDefault();
            return entry?.FilesWritten ?? [];
        }
        catch { return []; }
    }

    private async Task<ProcessResult> RunShellAsync(string command, CancellationToken ct)
    {
        var (shell, flag) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ("cmd.exe", "/c") : ("bash", "-c");
        return await ProcessHelper.RunAsync(
            shell, $"{flag} {command}",
            workingDirectory: _sandboxRoot,
            timeoutSeconds:   120,
            cancellationToken: ct);
    }

    // Evidence-source helpers (prefer graph, fall back to flat log)

    private async Task<HashSet<string>> LoadWrittenFilesAsync(CancellationToken ct)
    {
        // Prefer evidence graph when available — it has typed FileWrite nodes.
        if (_evidenceStore is not null)
        {
            var paths = await _evidenceStore.GetWrittenFilePathsAsync(ct);
            return paths.Select(PathHelpers.NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return await LoadWrittenFilesFromChangeLogAsync(ct);
    }

    private async Task<List<string>> LoadSucceededCommandsAsync(CancellationToken ct)
    {
        // Prefer evidence graph when available — it has typed CommandRun nodes with ExitCode.
        if (_evidenceStore is not null)
            return (await _evidenceStore.GetSucceededCommandsAsync(ct)).ToList();

        return await LoadSucceededCommandsFromChangeLogAsync(ct);
    }

    private async Task<HashSet<string>> LoadWrittenFilesFromChangeLogAsync(CancellationToken ct)
    {
        var logPath = _validationConfig?.ChangeLogPath;
        if (logPath is null || !File.Exists(logPath))
            return [];

        try
        {
            var raw = await File.ReadAllTextAsync(logPath, ct);
            var log = JsonSerializer.Deserialize<ChangeLogDoc>(raw, JsonOpts) ?? new ChangeLogDoc();
            var sessionId = log.ActiveSessionId;
            var entries = sessionId is not null
                ? log.Entries.Where(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal))
                : (IEnumerable<ChangeEntryDoc>)log.Entries;

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
                foreach (var p in entry.FilesWritten)
                    if (!string.IsNullOrWhiteSpace(p))
                        paths.Add(PathHelpers.NormalizePath(p));
            return paths;
        }
        catch { return []; }
    }

    private async Task<List<string>> LoadSucceededCommandsFromChangeLogAsync(CancellationToken ct)
    {
        var logPath = _validationConfig?.ChangeLogPath;
        if (logPath is null || !File.Exists(logPath))
            return [];

        try
        {
            var raw = await File.ReadAllTextAsync(logPath, ct);
            var log = JsonSerializer.Deserialize<ChangeLogDoc>(raw, JsonOpts) ?? new ChangeLogDoc();
            var sessionId = log.ActiveSessionId;
            var entries = sessionId is not null
                ? log.Entries.Where(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal))
                : (IEnumerable<ChangeEntryDoc>)log.Entries;

            return entries
                .SelectMany(e => e.CommandsRun)
                .Where(c => c.Succeeded && !string.IsNullOrWhiteSpace(c.Command))
                .Select(c => c.Command!)
                .ToList();
        }
        catch { return []; }
    }

    // Local DTOs (avoid coupling to the full ChangeLog model)

    private sealed record ChangeLogDoc
    {
        [JsonPropertyName("activeSessionId")]
        public string? ActiveSessionId { get; init; }

        [JsonPropertyName("entries")]
        public List<ChangeEntryDoc> Entries { get; init; } = [];
    }

    private sealed record ChangeEntryDoc
    {
        [JsonPropertyName("sessionId")]
        public string? SessionId { get; init; }

        [JsonPropertyName("turnIndex")]
        public int TurnIndex { get; init; }

        [JsonPropertyName("filesWritten")]
        public List<string> FilesWritten { get; init; } = [];

        [JsonPropertyName("commandsRun")]
        public List<CommandDoc> CommandsRun { get; init; } = [];
    }

    private sealed record CommandDoc
    {
        [JsonPropertyName("command")]
        public string? Command { get; init; }

        [JsonPropertyName("succeeded")]
        public bool Succeeded { get; init; }
    }

    private sealed record TestReportDoc
    {
        [JsonPropertyName("results")]
        public List<TestResultDoc>? Results { get; init; }
    }

    private sealed record TestResultDoc
    {
        [JsonPropertyName("criterion")]
        public string? Criterion { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("command")]
        public string? Command { get; init; }

        [JsonPropertyName("evidence")]
        public string? Evidence { get; init; }
    }
}
