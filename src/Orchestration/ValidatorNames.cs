namespace fuseraft.Orchestration;

/// <summary>
/// Names of the history-based validators that gate checks run through <c>ContractEngine</c>.
/// Use these everywhere instead of inline literals to prevent typo-induced silent failures.
/// </summary>
public static class ValidatorNames
{
    public const string RequireWriteFile               = "RequireWriteFile";
    public const string RequireReviewJudgement         = "RequireReviewJudgement";
    public const string RequireAcceptanceCriteriaPassed = "RequireAcceptanceCriteriaPassed";
    public const string BlockOnConsecutiveFail         = "BlockOnConsecutiveFail";
    public const string ArchitectureValidator          = "ArchitectureValidator";
    public const string RequireSessionContextWrite     = "RequireSessionContextWrite";

    // Synthetic name emitted into ValidatorStuckException / event logs
    public const string SignalRequiredPrefix           = "signal-required:";
}
