using System.Text;
using DiffPlex;

namespace fuseraft.Cli.Tui;

internal sealed record DiffResult(string Text, int Added, int Removed);

/// <summary>Unified line diff with three lines of context.</summary>
internal static class UnifiedDiff
{
    const int Context = 3;

    public static DiffResult Build(string before, string after)
    {
        var diff = Differ.Instance.CreateLineDiffs(TrimFinalNewline(before), TrimFinalNewline(after), ignoreWhitespace: false);
        var a = diff.PiecesOld;
        var b = diff.PiecesNew;
        var sb = new StringBuilder();
        int added = 0, removed = 0, i = 0;

        while (i < diff.DiffBlocks.Count)
        {
            // Merge blocks whose context would overlap into one hunk.
            var j = i;
            while (j + 1 < diff.DiffBlocks.Count
                   && diff.DiffBlocks[j + 1].DeleteStartA - (diff.DiffBlocks[j].DeleteStartA + diff.DiffBlocks[j].DeleteCountA) <= 2 * Context)
                j++;

            var first = diff.DiffBlocks[i];
            var last = diff.DiffBlocks[j];
            var startA = Math.Max(0, first.DeleteStartA - Context);
            var startB = Math.Max(0, first.InsertStartB - Context);
            var endA = Math.Min(a.Count, last.DeleteStartA + last.DeleteCountA + Context);
            var endB = Math.Min(b.Count, last.InsertStartB + last.InsertCountB + Context);
            sb.Append($"@@ -{Start(startA, endA)},{endA - startA} +{Start(startB, endB)},{endB - startB} @@\n");

            var posA = startA;
            for (var k = i; k <= j; k++)
            {
                var block = diff.DiffBlocks[k];
                for (; posA < block.DeleteStartA; posA++)
                    sb.Append(' ').Append(a[posA]).Append('\n');
                for (var d = 0; d < block.DeleteCountA; d++, removed++)
                    sb.Append('-').Append(a[block.DeleteStartA + d]).Append('\n');
                for (var n = 0; n < block.InsertCountB; n++, added++)
                    sb.Append('+').Append(b[block.InsertStartB + n]).Append('\n');
                posA = block.DeleteStartA + block.DeleteCountA;
            }
            for (; posA < endA; posA++)
                sb.Append(' ').Append(a[posA]).Append('\n');
            i = j + 1;
        }
        return new DiffResult(sb.ToString(), added, removed);
    }

    // Unified diff numbers an empty range by the line before it.
    static int Start(int start, int end) => end == start ? start : start + 1;

    static string TrimFinalNewline(string s) =>
        s.EndsWith("\r\n", StringComparison.Ordinal) ? s[..^2] : s.EndsWith('\n') ? s[..^1] : s;
}
