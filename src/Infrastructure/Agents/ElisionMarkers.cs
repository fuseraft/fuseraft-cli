using System.Text.RegularExpressions;

namespace fuseraft.Infrastructure.Agents;

/// <summary>
/// Canonical text for the placeholder notes injected during context compaction/trimming in
/// place of large or stale tool-call argument values, tool results, and superseded file reads.
/// Single source of truth for producers (<see cref="AgentContextCompactionFilters"/>,
/// <c>ContextWindowFilter</c>, <c>AgentMiddlewareBuilder</c>) so a model reusing one of these
/// notes as if it were real content can be detected reliably — see <see cref="PlaceholderTail"/>
/// and <c>FilePatchDiffing.DetectElisionPlaceholder</c>, which blocks <c>write_file</c>/
/// <c>patch_file</c> from committing a placeholder to disk in place of actual data.
/// </summary>
internal static class ElisionMarkers
{
    /// <summary>Replaces a large intermediate function-call argument value (e.g. a prior
    /// <c>write_file</c>'s <c>content</c>) that has aged out of the retained context window.</summary>
    internal static string ArgValueNote(int originalChars) =>
        $"[ELIDED — {originalChars:N0} chars, NOT the real value. Do not reuse this placeholder as " +
        "content; re-read the file or regenerate the value if you need it again.]";

    /// <summary>Replaces an entire superseded tool result during in-turn context trimming.</summary>
    internal const string ResultNote =
        "[RESULT ELIDED — not the real output, do not reuse this as data. Re-run the tool if you need this result again.]";

    /// <summary>Appended after a capped structural preview of a <c>read_file</c> result once a
    /// downstream write/patch to the same path makes the rest of the read stale.</summary>
    internal static string ConsumedReadTail(int elidedChars) =>
        $"\n[...{elidedChars:N0} chars elided — file was written or patched later this session; " +
        "call read_file again if current content is needed]";

    /// <summary>
    /// Matches content that ends with one of the notes above. All three are always appended as
    /// a trailing marker by their producers, never embedded mid-document — so anchoring to the
    /// end of the (possibly whitespace-padded) text catches a model that copied a placeholder
    /// into a write instead of re-reading/regenerating the real value, without false-positiving
    /// on legitimate edits to source files (such as this one) that contain this same literal
    /// text surrounded by other code.
    /// </summary>
    internal static readonly Regex PlaceholderTail = new(
        @"(\[ELIDED — [\d,]+ chars, NOT the real value\. Do not reuse this placeholder as content; re-read the file or regenerate the value if you need it again\.\]" +
        @"|\[RESULT ELIDED — not the real output, do not reuse this as data\. Re-run the tool if you need this result again\.\]" +
        @"|\[\.\.\.[\d,]+ chars elided — file was written or patched later this session; call read_file again if current content is needed\])\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
