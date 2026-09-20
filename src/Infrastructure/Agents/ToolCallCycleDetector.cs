namespace fuseraft.Infrastructure.Agents;

internal enum ToolCallCycleVerdict { None, Soft, Hard }

/// <summary>
/// Spots a model bouncing between two identical tool calls — <c>A B A B A B …</c> — which
/// <see cref="AgentToolLoopGuard"/> / <c>ReplToolLoopGuard</c> / <c>ReplTurn</c>'s consecutive-
/// identical-call counters miss entirely: every step differs from the one before it, so each of
/// those counters resets to 1 on every call. Typical shape: <c>read_file(x)</c> → <c>run_tests</c>
/// → <c>read_file(x)</c> → <c>run_tests</c> with nothing changing in between. OpenHands'
/// <c>StuckDetector</c> covers this as its "alternating pattern" scenario; this is the same idea
/// over call signatures.
///
/// <para>
/// Feed it one signature per tool call (name + a stable argument signature — see
/// <see cref="ToolCallSignature"/>). The alternation length is the run of calls at the tail that
/// strictly alternate between exactly two distinct signatures. Like the identical-call guards it
/// fires <see cref="ToolCallCycleVerdict.Soft"/> exactly once as the run climbs through
/// <see cref="SoftThreshold"/>, and <see cref="ToolCallCycleVerdict.Hard"/> on every call from
/// <see cref="HardThreshold"/> on.
/// </para>
///
/// <para>
/// Any edit that changes a call's arguments (a different <c>patch_file</c> body, a different
/// path) yields a different signature and breaks the run, so a real edit/verify cycle doesn't
/// trip it. Period-3+ cycles and result-awareness are out of scope.
/// </para>
/// </summary>
internal sealed class ToolCallCycleDetector
{
    // Calls, not cycles: 6 = three full A/B round trips before a nudge, 10 = five before a stop.
    internal const int SoftThreshold = 6;
    internal const int HardThreshold = 10;

    // Enough history to measure a run of HardThreshold with a little slack, no more.
    private const int WindowSize = HardThreshold + 2;

    private readonly List<string> _recent = [];

    /// <summary>Length of the alternating run measured by the most recent <see cref="Observe"/>.</summary>
    internal int LastLength { get; private set; }

    internal void Reset()
    {
        _recent.Clear();
        LastLength = 0;
    }

    internal ToolCallCycleVerdict Observe(string signature)
    {
        _recent.Add(signature);
        if (_recent.Count > WindowSize) _recent.RemoveAt(0);

        LastLength = TailAlternationLength();

        if (LastLength >= HardThreshold) return ToolCallCycleVerdict.Hard;
        if (LastLength == SoftThreshold) return ToolCallCycleVerdict.Soft;
        return ToolCallCycleVerdict.None;
    }

    // 0 when the last two calls are identical (or there is only one call): that's the
    // consecutive-identical case the other counters own, not an alternation.
    private int TailAlternationLength()
    {
        var n = _recent.Count;
        if (n < 2 || _recent[n - 1] == _recent[n - 2]) return 0;

        var length = 2;
        while (length < n && _recent[n - 1 - length] == _recent[n - 1 - length + 2])
            length++;
        return length;
    }
}
