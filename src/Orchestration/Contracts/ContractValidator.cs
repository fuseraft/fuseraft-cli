using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Contracts;

/// <summary>
/// Wraps a named <see cref="ContractConfig"/> evaluation as an <see cref="IRoutingValidator"/>
/// so contracts integrate transparently into the existing keyword-routing validation pipeline.
///
/// <para>
/// Routes may declare contracts alongside legacy validators — all run with AND semantics:
/// every validator and every contract must pass before the route fires.
/// </para>
/// </summary>
public sealed class ContractValidator(ContractEngine engine, string contractName) : IRoutingValidator
{
    public async Task<RoutingValidationResult> ValidateAsync(
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        var (satisfied, error) = await engine.EvaluateAsync(contractName, cancellationToken);

        return satisfied
            ? RoutingValidationResult.Pass()
            : RoutingValidationResult.Fail(error ?? $"Contract '{contractName}' was not satisfied.");
    }
}
