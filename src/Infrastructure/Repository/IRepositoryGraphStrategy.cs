using fuseraft.Core.Models;

namespace fuseraft.Infrastructure.Repository;

/// <summary>
/// Language-specific logic for extracting <see cref="RepositoryGraph"/> nodes/edges from a
/// single source file.
///
/// <para>
/// <see cref="RepositoryGraphBuilder"/> owns everything language-agnostic — file discovery,
/// locking, store I/O, ADR node upserts. A strategy owns only the structural parsing for one
/// language family (declarations, scoping rules, <c>SymbolId</c> conventions). Adding support
/// for a new language means adding a new strategy and registering it; the builder itself
/// should not need to change.
/// </para>
/// </summary>
public interface IRepositoryGraphStrategy
{
    /// <summary>
    /// Glob patterns (as passed to <see cref="Directory.GetFiles(string, string, SearchOption)"/>)
    /// identifying this strategy's source files, e.g. <c>["*.cs"]</c>. Used by
    /// <see cref="RepositoryGraphBuilder.BuildAllAsync"/> for the initial full scan.
    /// </summary>
    IReadOnlyList<string> FileGlobs { get; }

    /// <summary>
    /// True if this strategy owns <paramref name="absoluteFilePath"/> (typically an extension
    /// check). Used by <see cref="RepositoryGraphBuilder.RebuildFileAsync"/> to route a single
    /// changed file to the right strategy.
    /// </summary>
    bool CanHandle(string absoluteFilePath);

    /// <summary>
    /// Scans <paramref name="absolutePath"/> and adds its nodes/edges to <paramref name="graph"/>,
    /// including the <c>file:{relativePath}</c> node itself.
    /// </summary>
    void ScanFile(string absolutePath, string relativePath, RepositoryGraph graph);
}
