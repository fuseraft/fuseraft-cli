using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Undoes the HTML entity encoding some models put on tool arguments (<c>&amp;amp;&amp;amp;</c>
/// for <c>&amp;&amp;</c>, <c>&amp;lt;</c> for <c>&lt;</c>) without corrupting text where the
/// entity is real. A Boomi export named <c>Contracts  &amp;gt;  Projects.xml</c> is a real file,
/// and a one-line XML file stores <c>a &amp;lt; b</c> literally, so blanket decoding sent shell
/// commands to a file that doesn't exist and made searches for escaped XML miss every time.
/// </summary>
internal static partial class HtmlEntityArtifacts
{
    // A closed "…" (with backslash escapes) or '…' span. An unclosed quote matches nothing,
    // so the rest of the command is still decoded — an apostrophe in "it's" can't switch
    // decoding off for everything after it.
    [GeneratedRegex(@"""(?:\\.|[^""\\])*""|'[^']*'")]
    private static partial Regex QuotedSpan();

    /// <summary>
    /// Decodes entities in a shell command except inside quoted strings. The encoding artifact
    /// lands on shell syntax (<c>&amp;&amp;</c>, <c>|</c>, <c>&gt;</c>), which sits outside quotes;
    /// an entity inside quotes is part of an argument — usually a filename, which needs quotes
    /// precisely because it has spaces — and is kept as written. Entity-encoded quotes
    /// (<c>&amp;quot;</c>) are not real quotes yet, so they are decoded like any other artifact.
    /// </summary>
    internal static string DecodeOutsideQuotes(string command)
    {
        if (!command.Contains('&')) return command;

        var sb   = new StringBuilder(command.Length);
        var last = 0;
        foreach (Match quoted in QuotedSpan().Matches(command))
        {
            sb.Append(WebUtility.HtmlDecode(command[last..quoted.Index]));
            sb.Append(quoted.Value);
            last = quoted.Index + quoted.Length;
        }
        sb.Append(WebUtility.HtmlDecode(command[last..]));
        return sb.ToString();
    }

    /// <summary>
    /// A search pattern that matches the query both as written and with entities decoded, so
    /// <c>&amp;lt;TargetFramework</c> still finds <c>&lt;TargetFramework</c> while <c>a &amp;lt; b</c>
    /// also finds that text stored escaped inside XML. Returns the query unchanged when it has
    /// no entities to decode.
    /// </summary>
    internal static string MatchEitherForm(string query)
    {
        var decoded = WebUtility.HtmlDecode(query);
        return decoded == query ? query : $"(?:{query})|(?:{decoded})";
    }
}
