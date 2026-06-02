namespace fuseraft.Core.Models;

/// <summary>
/// Discriminates every kind of node in the repository semantic graph.
/// </summary>
public enum NodeType
{
    Namespace,
    File,
    Project,
    Package,
    Type,
    Interface,
    Method,
    Property,
    Field,
    Adr,
    /// <summary>An architecture layer violation detected by <c>ArchitectureValidator</c>.</summary>
    Violation,
}
