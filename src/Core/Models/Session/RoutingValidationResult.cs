namespace fuseraft.Core.Models.Session;

/// <summary>
/// Result returned by an <see cref="fuseraft.Core.Interfaces.IRoutingValidator"/>.
/// When <see cref="IsValid"/> is false the keyword route is blocked: the
/// <see cref="ErrorMessage"/> is injected as a user message and the source agent
/// is re-invoked so it can correct the problem.
/// </summary>
public record RoutingValidationResult
{
    /// <summary>True when the invariants required by the route are satisfied.</summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Human-readable description of what invariant was violated.
    /// Only populated when <see cref="IsValid"/> is false.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Returns a passing result.</summary>
    public static RoutingValidationResult Pass() => new() { IsValid = true };

    /// <summary>Returns a failing result with the given error message.</summary>
    public static RoutingValidationResult Fail(string message) =>
        new() { IsValid = false, ErrorMessage = message };
}
