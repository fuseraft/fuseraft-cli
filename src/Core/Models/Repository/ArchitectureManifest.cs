namespace fuseraft.Core.Models.Repository;

/// <summary>
/// Architecture manifest loaded from <c>.fuseraft/architecture.yaml</c>.
/// Defines project layers and their allowed dependency relationships.
/// </summary>
public sealed class ArchitectureManifest
{
    /// <summary>
    /// Source language used to select the file glob and import-statement parser.
    /// Supported values: <c>csharp</c> (default), <c>python</c>, <c>java</c>,
    /// <c>typescript</c>, <c>javascript</c>, <c>go</c>, <c>rust</c>, <c>ruby</c>.
    /// Unknown values fall back to <c>csharp</c>.
    /// </summary>
    public string Language { get; set; } = "csharp";

    public List<ArchitectureLayer> Layers { get; set; } = [];
}

/// <summary>
/// A single named layer in the architecture manifest.
/// </summary>
public sealed class ArchitectureLayer
{
    /// <summary>Display name (e.g. "Core", "Infrastructure").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Source paths that belong to this layer, relative to project root (e.g. "src/Core/").</summary>
    public List<string> Paths { get; set; } = [];

    /// <summary>
    /// Namespace prefixes owned by this layer.
    /// When empty, defaults to the root namespace + "." + Name (e.g. "fuseraft.Core").
    /// </summary>
    public List<string> Namespaces { get; set; } = [];

    /// <summary>Names of other layers this layer is allowed to reference.</summary>
    public List<string> MayDependOn { get; set; } = [];
}

/// <summary>
/// A detected architecture violation: a source file in one layer importing
/// a namespace that belongs to a layer it is not permitted to reference.
/// </summary>
public sealed record ArchitectureViolation
{
    /// <summary>Layer that contains the violating source file.</summary>
    public string SourceLayer { get; init; } = string.Empty;

    /// <summary>Layer that owns the illegally referenced namespace.</summary>
    public string TargetLayer { get; init; } = string.Empty;

    /// <summary>Relative path of the violating source file.</summary>
    public string File { get; init; } = string.Empty;

    /// <summary>1-based line number of the offending <c>using</c> directive.</summary>
    public int Line { get; init; }

    /// <summary>The namespace being imported illegally.</summary>
    public string Namespace { get; init; } = string.Empty;
}
