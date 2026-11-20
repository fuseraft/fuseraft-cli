namespace fuseraft.Infrastructure.Context;

/// <summary>
/// The session the current model request belongs to, flowing to <see cref="SessionAffinityHandler"/> through C#'s
/// async execution context. The REPL sets it at the start of each turn, so a turn's subagents carry it too.
/// </summary>
internal static class RequestSession
{
    internal static readonly AsyncLocal<string?> Id = new();
}
