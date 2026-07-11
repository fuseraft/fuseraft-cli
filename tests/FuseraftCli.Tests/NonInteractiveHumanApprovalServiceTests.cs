using fuseraft.Cli;

namespace FuseraftCli.Tests;

/// <summary>
/// Verifies every IHumanApprovalService method resolves immediately with the
/// no-human-available outcome, without touching the console — the contract
/// eval runs and other unattended sessions depend on.
/// </summary>
public sealed class NonInteractiveHumanApprovalServiceTests
{
    private readonly NonInteractiveHumanApprovalService _svc = new();

    [Fact]
    public async Task PromptContinueAsync_ReturnsNull()
        => Assert.Null(await _svc.PromptContinueAsync());

    [Fact]
    public async Task PromptRedirectAsync_ReturnsNull()
        => Assert.Null(await _svc.PromptRedirectAsync("Agent"));

    [Fact]
    public async Task PromptValidatorStuckAsync_ReturnsNull()
        => Assert.Null(await _svc.PromptValidatorStuckAsync("Tester", "TestsValid", 2, "fabricated evidence"));

    [Fact]
    public async Task PromptBlockerResolutionAsync_ReturnsNull()
        => Assert.Null(await _svc.PromptBlockerResolutionAsync("Developer", "missing credentials"));

    [Fact]
    public async Task PromptRouteApprovalAsync_ReturnsTrue()
        => Assert.True(await _svc.PromptRouteApprovalAsync("APPROVED", "Reviewer", "Done"));

    [Fact]
    public async Task PromptShellCommandAsync_ReturnsTrue()
        => Assert.True(await _svc.PromptShellCommandAsync("rm -rf /tmp/scratch"));

    [Fact]
    public async Task PromptPostSessionAsync_ReturnsNull()
        => Assert.Null(await _svc.PromptPostSessionAsync());

    [Fact]
    public async Task PromptPlanReviewAsync_ReturnsNull()
        => Assert.Null(await _svc.PromptPlanReviewAsync("1. Do X\n2. Do Y"));
}
