using System.IO.Compression;
using System.Text;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Spots a reply that has stopped saying anything new, so its stream can be stopped rather than left to run. A live model
/// answered, then wrote itself a question and looped on variations of "I did not use tools. Stop. Done." for 133,000
/// characters. Exact repeats are a weak signal (the wording keeps shifting), but such text compresses far better than
/// prose: under 18 % within 8,000 characters, where docs prose stays above 35 %. Code compresses nearly as well as a loop,
/// so fenced code is left out of the measure.
/// </summary>
internal static class RunawayOutput
{
    internal const int MinChars      = 8_000;
    internal const int WindowChars   = 6_000;
    internal const double MaxRatio   = 0.17;
    internal const int CeilingChars  = 100_000;

    /// <summary>Where the reply's runaway part starts, or null while it reads like a reply.</summary>
    internal static int? LoopStart(string text)
    {
        if (text.Length >= CeilingChars)
            return CeilingChars;

        var (prose, origin) = OutsideFences(text);
        if (prose.Length < MinChars || Ratio(prose, prose.Length - WindowChars) >= MaxRatio)
            return null;

        // The first window that compresses like a loop also holds whatever came just before the loop, an answer
        // included. So the cut goes where the text first says again something it already said: one copy of everything
        // stays, and only the repeats go. Without an exact repeat, the window's start is the cut.
        var window = 0;
        while (window + WindowChars < prose.Length && Ratio(prose, window) >= MaxRatio)
            window += 1_000;
        var firstRepeat = int.MaxValue;
        for (var at = 0; at + RepeatChars <= Math.Min(prose.Length, window + WindowChars) && at < firstRepeat; at += 16)
        {
            var again = prose.IndexOf(prose.Substring(at, RepeatChars), at + RepeatChars, StringComparison.Ordinal);
            if (again >= 0 && again < firstRepeat)
                firstRepeat = again;
        }
        return origin[firstRepeat == int.MaxValue ? window : firstRepeat];
    }

    // Long enough that a repeat is the reply saying the same thing again, not a shared phrase or a path.
    private const int RepeatChars = 64;

    private static double Ratio(string text, int start)
    {
        var bytes = Encoding.UTF8.GetBytes(text.Substring(start, WindowChars));
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(bytes);
        return (double)output.Length / bytes.Length;
    }

    // The text outside ``` fences, and for each of its characters the index it had in the reply.
    private static (string Prose, int[] Origin) OutsideFences(string text)
    {
        var prose  = new StringBuilder(text.Length);
        var origin = new List<int>(text.Length);
        var inFence = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (string.CompareOrdinal(text, i, "```", 0, 3) == 0)
            {
                inFence = !inFence;
                i += 2;
                continue;
            }
            if (inFence) continue;
            prose.Append(text[i]);
            origin.Add(i);
        }
        return (prose.ToString(), [.. origin]);
    }
}
