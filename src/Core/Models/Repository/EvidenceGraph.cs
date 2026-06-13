using System.Text.Json.Serialization;
using fuseraft.Core;

namespace fuseraft.Core.Models.Repository;

/// <summary>
/// On-disk evidence graph. Typed nodes record every observable action (file write,
/// command execution, test result) with richer metadata than the flat change log.
/// Edges capture causal relationships between nodes.
///
/// <para>
/// Serialized to <c>.fuseraft/evidence.json</c> by default. Maintained alongside
/// <c>changes.json</c> for backward compatibility; validators can query either
/// representation.
/// </para>
/// </summary>
public record EvidenceGraph
{
    /// <summary>Session ID that is currently active.</summary>
    public string? ActiveSessionId { get; init; }

    /// <summary>All evidence nodes recorded so far.</summary>
    public List<EvidenceNode> Nodes { get; init; } = [];

    /// <summary>Directed edges between nodes expressing causal relationships.</summary>
    public List<EvidenceEdge> Edges { get; init; } = [];
}

/// <summary>
/// Discriminated evidence node. <see cref="NodeType"/> identifies the kind of action;
/// the properties relevant to that action are populated.
/// </summary>
public record EvidenceNode
{
    /// <summary>Stable identifier for this node (GUID).</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Node type discriminator.
    /// <list type="bullet">
    ///   <item><c>FileWrite</c> — a file was written or patched.</item>
    ///   <item><c>FileDelete</c> — a file was deleted.</item>
    ///   <item><c>CommandRun</c> — a shell command was executed.</item>
    ///   <item><c>GitCommit</c> — a git commit was made.</item>
    ///   <item><c>TestResult</c> — a test result was recorded in the test report.</item>
    ///   <item><c>SymbolDefinition</c> — a symbol was analyzed during recon (name, kind, file).</item>
    ///   <item><c>SymbolReference</c> — a cross-file reference was mapped by the Archaeologist (source file, symbol name, target file).</item>
    ///   <item><c>Violation</c> — an architecture layer violation; <see cref="Path"/> is the offending file, <see cref="SymbolName"/> is the illegal namespace, <see cref="Evidence"/> is "SourceLayer → TargetLayer".</item>
    /// </list>
    /// </summary>
    public string NodeType { get; init; } = string.Empty;

    /// <summary>When this node was recorded (UTC).</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Agent that produced this node.</summary>
    public string Agent { get; init; } = string.Empty;

    /// <summary>Turn index within the session.</summary>
    public int Turn { get; init; }

    /// <summary>Session ID this node belongs to.</summary>
    public string? SessionId { get; init; }

    // FileWrite / FileDelete fields

    /// <summary>File path (absolute or relative). Set for FileWrite and FileDelete nodes.</summary>
    public string? Path { get; init; }

    /// <summary>
    /// SHA-256 hex hash of the file contents immediately after the write.
    /// Null when hashing was not performed (e.g., binary files or write failures).
    /// </summary>
    public string? ContentHash { get; init; }

    // CommandRun fields

    /// <summary>Full command string. Set for CommandRun nodes.</summary>
    public string? Command { get; init; }

    /// <summary>Process exit code. 0 = success. Set for CommandRun nodes.</summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// SHA-256 hex hash of the combined stdout+stderr output.
    /// Allows contracts to detect whether the same command produced the same result.
    /// </summary>
    public string? OutputHash { get; init; }

    /// <summary>
    /// Combined stdout+stderr, capped at 4 096 characters.
    /// Null for commands that produced no output.
    /// </summary>
    public string? Output { get; init; }

    // GitCommit fields

    /// <summary>Commit message. Set for GitCommit nodes.</summary>
    public string? CommitMessage { get; init; }

    // TestResult fields

    /// <summary>Test criterion name. Set for TestResult nodes.</summary>
    public string? Criterion { get; init; }

    /// <summary>Test status: <c>"PASS"</c> or <c>"FAIL"</c>. Set for TestResult nodes.</summary>
    public string? Status { get; init; }

    /// <summary>
    /// Command or evidence string cited in the test report. Used for cross-referencing
    /// against CommandRun nodes to detect fabricated results.
    /// </summary>
    public string? Evidence { get; init; }

    // SymbolDefinition / SymbolReference fields

    /// <summary>Symbol name. Set for SymbolDefinition and SymbolReference nodes.</summary>
    public string? SymbolName { get; init; }

    /// <summary>
    /// Symbol kind. Set for SymbolDefinition nodes.
    /// Expected values: <c>"function"</c>, <c>"class"</c>, <c>"interface"</c>, <c>"type"</c>, <c>"variable"</c>.
    /// </summary>
    public string? SymbolKind { get; init; }

    /// <summary>
    /// File that defines the referenced symbol. Set for SymbolReference nodes.
    /// <see cref="Path"/> carries the file where the reference occurs.
    /// </summary>
    public string? TargetFile { get; init; }

    /// <summary>
    /// ID of the <see cref="ClaimRecord"/> in the provenance registry that verifies the
    /// observable outcome represented by this node. Null until a validator or the provenance
    /// registry explicitly associates a claim with this node.
    /// </summary>
    public string? ProvenanceRef { get; init; }
}

/// <summary>
/// A directed edge in the evidence graph expressing a causal relationship between nodes.
/// </summary>
public record EvidenceEdge
{
    /// <summary><see cref="EvidenceNode.Id"/> of the source node.</summary>
    public string From { get; init; } = string.Empty;

    /// <summary><see cref="EvidenceNode.Id"/> of the target node.</summary>
    public string To { get; init; } = string.Empty;

    /// <summary>
    /// Semantic relationship.
    /// <list type="bullet">
    ///   <item><c>produced_by</c> — source artifact was produced by target action.</item>
    ///   <item><c>verified_by</c> — source test result was verified by target command.</item>
    ///   <item><c>depends_on</c> — source action depends on the target artifact.</item>
    /// </list>
    /// </summary>
    public string Relation { get; init; } = string.Empty;
}

/// <summary>
/// Configuration for the structured evidence graph file.
/// </summary>
public record EvidenceStoreConfig
{
    /// <summary>
    /// File path where the evidence graph JSON is written.
    /// Defaults to <c>.fuseraft/state/evidence.json</c>.
    /// </summary>
    public string Path { get; init; } = FuseraftPaths.LocalEvidence;
}
