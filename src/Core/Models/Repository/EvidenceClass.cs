namespace fuseraft.Core.Models.Repository;

/// <summary>
/// Classifies the type of evidence backing a <see cref="ClaimRecord"/>.
/// Used by <see cref="fuseraft.Infrastructure.ConfidenceComputer"/> to compute confidence tier.
/// </summary>
public enum EvidenceClass
{
    GitHistory,
    EvidenceGraph,
    TestResult,
    ExitCode,
    Validator,
    ADR,
    RepositoryMemory,
    AgentAssertion,
}
