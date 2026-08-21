namespace fuseraft.Orchestration;

/// <summary>
/// Canonical string constants for the built-in routing and termination validator names.
/// Use these everywhere instead of inline literals to prevent typo-induced silent failures.
/// </summary>
public static class ValidatorNames
{
    // Built-in routing / termination validators
    public const string RequireShellPass               = "RequireShellPass";
    public const string RequireWriteFile               = "RequireWriteFile";
    public const string RequireAllFilesWritten         = "RequireAllFilesWritten";
    public const string RequireBrief                   = "RequireBrief";
    public const string RequireReviewJudgement         = "RequireReviewJudgement";
    public const string RequireAcceptanceCriteriaPassed = "RequireAcceptanceCriteriaPassed";
    public const string RequireRelatedTestsPass        = "RequireRelatedTestsPass";
    public const string BlockOnConsecutiveFail         = "BlockOnConsecutiveFail";
    public const string TestReportValid                = "TestReportValid";
    public const string ArchitectureValidator          = "ArchitectureValidator";
    public const string RequireSessionContextWrite     = "RequireSessionContextWrite";

    // Synthetic validator names emitted into ValidatorStuckException / event logs
    public const string StructuredRouting              = "StructuredRouting";
    public const string SignalRequiredPrefix           = "signal-required:";
}
