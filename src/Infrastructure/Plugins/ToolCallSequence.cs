namespace fuseraft.Infrastructure.Plugins;

/// <summary>Counts the tool calls agents make, so a tool can tell whether any other call ran since its own.</summary>
public sealed class ToolCallSequence
{
    /// <summary>The process's count, kept by every agent's tool invoker.</summary>
    public static ToolCallSequence Shared { get; } = new();

    private long _count;

    internal long Current => Interlocked.Read(ref _count);

    internal void Advance() => Interlocked.Increment(ref _count);
}
