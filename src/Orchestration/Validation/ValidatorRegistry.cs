using fuseraft.Core;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Validation;

/// <summary>
/// Shared validator name→instance construction used by <c>GraphOrchestrator</c> and
/// <c>WorkflowOrchestrator</c> — the two orchestrators that resolve routing validators from
/// per-edge <c>Validators</c> name lists. Extracted because the two orchestrators'
/// <c>BuildValidatorsFromNames</c> methods were independently hand-written copies of the same
/// logic (confirmed byte-identical modulo one comment).
///
/// <para>
/// <c>StrategyFactory.BuildValidators</c> (used by Keyword/StateMachine selection strategies)
/// is deliberately <b>not</b> unified with this — it solves a different problem (builds one
/// dictionary up front for a whole session, needs <c>requireCurrentTurn</c>/
/// <c>provenanceRegistry</c> that this per-edge path doesn't use, and has no per-edge
/// <c>RequiredCommandPattern</c>/<c>ShellFallbackPattern</c> override). Forcing all three call
/// sites into one function would either drop parameters two of the three callers need, or
/// bloat the shared signature with parameters only one caller uses.
/// </para>
/// </summary>
internal static class ValidatorRegistry
{
    public static IReadOnlyList<IRoutingValidator> BuildValidatorsFromNames(
        OrchestrationConfig config,
        IReadOnlyList<string> names,
        string? requiredCommandPattern = null,
        string? shellFallbackPattern = null)
    {
        var result = new List<IRoutingValidator>();

        // Resolve sandbox root the same way OrchestratorBuilder does.
        var sandboxRoot = config.Security?.FileSystemSandboxPath is { Length: > 0 } sbx
            ? FuseraftPaths.ExpandPath(sbx)
            : null;

        var briefPath = config.Validation?.BriefPath;

        foreach (var name in names)
        {
            IRoutingValidator? v = null;

            if (name.Equals(ValidatorNames.RequireShellPass, StringComparison.OrdinalIgnoreCase))
                v = new RequireShellPassValidator(requiredCommandPattern, config.Validation?.ChangeLogPath);
            else if (name.Equals(ValidatorNames.RequireWriteFile, StringComparison.OrdinalIgnoreCase))
                v = new HandoffToTesterValidator(
                        shellFallbackPattern: shellFallbackPattern,
                        changeLogPath:        config.Validation?.ChangeLogPath);
            else if (name.Equals(ValidatorNames.BlockOnConsecutiveFail, StringComparison.OrdinalIgnoreCase))
                v = new ConsecutiveShellFailValidator(
                        commandPattern: requiredCommandPattern,
                        changeLogPath:  config.Validation?.ChangeLogPath);
            else if (name.Equals(ValidatorNames.RequireAllFilesWritten, StringComparison.OrdinalIgnoreCase) && briefPath is not null)
                v = new RequireAllFilesWrittenValidator(briefPath, config.Validation!.ChangeLogPath);
            else if (name.Equals(ValidatorNames.RequireBrief, StringComparison.OrdinalIgnoreCase) && briefPath is not null)
                v = new RequireBriefValidator(briefPath);
            else if (name.Equals(ValidatorNames.TestReportValid, StringComparison.OrdinalIgnoreCase) && config.Validation is not null)
                v = new HandoffToReviewerValidator(config.Validation);
            else if (name.Equals(ValidatorNames.RequireReviewJudgement, StringComparison.OrdinalIgnoreCase))
                v = new RequireReviewJudgementValidator(briefPath);
            else if (name.Equals(ValidatorNames.RequireAcceptanceCriteriaPassed, StringComparison.OrdinalIgnoreCase) && briefPath is not null)
                v = new RequireAcceptanceCriteriaPassedValidator(briefPath, config.Validation!.ChangeLogPath);
            else if (name.Equals(ValidatorNames.RequireRelatedTestsPass, StringComparison.OrdinalIgnoreCase) && config.TestSelector is not null)
                v = new RequireRelatedTestsPassValidator(
                        config.TestSelector,
                        config.Validation?.ChangeLogPath,
                        sandboxRoot);
            else if (name.Equals(ValidatorNames.ArchitectureValidator, StringComparison.OrdinalIgnoreCase))
                v = new ArchitectureValidator(projectRoot: sandboxRoot);

            if (v is not null)
                result.Add(v);
        }

        return result;
    }
}
