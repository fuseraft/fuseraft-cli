namespace fuseraft.Core.Models.Config;

/// <summary>
/// Controls the saga (compensating rollback) pattern for long-running workflows.
/// When enabled, the <see cref="fuseraft.Orchestration.Saga.SagaOrchestrator"/> wraps
/// workflow execution and triggers compensating actions in reverse order on failure,
/// preventing partial state from being left behind.
/// </summary>
public record SagaConfig
{
    /// <summary>
    /// When <c>true</c>, workflow execution is wrapped in a saga that runs compensating
    /// actions on failure. Defaults to <c>false</c>.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Maximum number of compensating steps to run during unwind.
    /// Acts as a safety limit to prevent infinite compensation loops if a compensator
    /// is itself broken. Defaults to <c>10</c>.
    /// </summary>
    public int MaxCompensationSteps { get; init; } = 10;
}
