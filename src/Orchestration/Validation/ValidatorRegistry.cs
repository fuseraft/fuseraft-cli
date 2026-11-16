using fuseraft.Core;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Validation;

/// <summary>Builds the routing validators that gate checks run through <c>ContractEngine</c>.</summary>
internal static class ValidatorRegistry
{
    /// <summary>Builds one validator by name, or null when the name is unknown or its inputs are missing.</summary>
    public static IRoutingValidator? Build(
        string name,
        ValidationConfig? validation,
        string? sandboxRoot,
        string? requiredCommandPattern = null,
        string? shellFallbackPattern = null)
    {
        var briefPath     = validation?.BriefPath;
        var changeLogPath = validation?.ChangeLogPath;

        if (name.Equals(ValidatorNames.RequireWriteFile, StringComparison.OrdinalIgnoreCase))
            return new AnyFileWrittenValidator(shellFallbackPattern: shellFallbackPattern, changeLogPath: changeLogPath);
        if (name.Equals(ValidatorNames.BlockOnConsecutiveFail, StringComparison.OrdinalIgnoreCase))
            return new ConsecutiveShellFailValidator(commandPattern: requiredCommandPattern, changeLogPath: changeLogPath);
        if (name.Equals(ValidatorNames.RequireReviewJudgement, StringComparison.OrdinalIgnoreCase))
            return new RequireReviewJudgementValidator(briefPath);
        if (name.Equals(ValidatorNames.RequireAcceptanceCriteriaPassed, StringComparison.OrdinalIgnoreCase) && briefPath is not null)
            return new RequireAcceptanceCriteriaPassedValidator(briefPath, changeLogPath);
        if (name.Equals(ValidatorNames.ArchitectureValidator, StringComparison.OrdinalIgnoreCase))
            return new ArchitectureValidator(projectRoot: sandboxRoot);
        if (name.Equals(ValidatorNames.RequireSessionContextWrite, StringComparison.OrdinalIgnoreCase))
            return new RequireSessionContextWriteValidator();
        return null;
    }
}
