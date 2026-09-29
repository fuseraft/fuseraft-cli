namespace fuseraft.Core;

/// <summary>
/// Lets a stretch of work refuse every HITL approval prompt instead of showing it. Parallel subagents
/// (<c>subagent_explore_many</c>) run this way so that several loops never put overlapping y/N prompts
/// on the terminal or the VS Code bridge — and so that none of them can widen the session's sandbox
/// through a sandbox-escape grant, which is permanent and shared by every tool.
///
/// <para>
/// The refusal is scoped to the calling async flow (an <see cref="AsyncLocal{T}"/>): work that
/// <see cref="RefuseInThisFlow"/> is called from, and anything it awaits, is refused; the caller that
/// started it, and every other flow, keep prompting as before. Every approval callback handed to the
/// plugins is wrapped with <see cref="Guard(Func{string, Task{bool}})"/> and its overloads, so this is
/// the single place a refusal is decided.
/// </para>
/// </summary>
public static class ApprovalScope
{
    private static readonly AsyncLocal<bool> Refusing = new();

    /// <summary>For the rest of the calling async flow, refuse approval prompts without showing them.</summary>
    public static void RefuseInThisFlow() => Refusing.Value = true;

    /// <summary>True when the current async flow refuses approval prompts.</summary>
    public static bool IsRefusing => Refusing.Value;

    public static Func<string, Task<bool>> Guard(Func<string, Task<bool>> approve) =>
        a => Refusing.Value ? Task.FromResult(false) : approve(a);

    public static Func<string, string, Task<bool>> Guard(Func<string, string, Task<bool>> approve) =>
        (a, b) => Refusing.Value ? Task.FromResult(false) : approve(a, b);

    public static Func<string, string, string, Task<bool>> Guard(Func<string, string, string, Task<bool>> approve) =>
        (a, b, c) => Refusing.Value ? Task.FromResult(false) : approve(a, b, c);

    public static Func<string, string, string, string, Task<bool>> Guard(Func<string, string, string, string, Task<bool>> approve) =>
        (a, b, c, d) => Refusing.Value ? Task.FromResult(false) : approve(a, b, c, d);
}
