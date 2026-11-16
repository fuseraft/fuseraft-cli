using fuseraft.Cli;
using fuseraft.Core.Interfaces;

namespace FuseraftCli.Tests;

/// <summary>
/// Verifies every IHumanApprovalService method resolves immediately with the
/// no-human-available outcome, without touching the console — the contract
/// eval runs and other unattended sessions depend on.
/// </summary>
public sealed class NonInteractiveHumanApprovalServiceTests
{
    private readonly NonInteractiveHumanApprovalService _svc = new();

}
