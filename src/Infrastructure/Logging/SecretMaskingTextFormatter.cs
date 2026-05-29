using System.Text;
using System.Text.RegularExpressions;
using Serilog.Events;
using Serilog.Formatting;

namespace fuseraft.Infrastructure.Logging;

/// <summary>
/// Serilog <see cref="ITextFormatter"/> wrapper that redacts API key–like values from
/// rendered output before writing to the underlying formatter. Applied to every log sink
/// (console and file) so secrets never appear in any log output regardless of verbosity.
///
/// <para>Patterns masked (replaced with <c>[REDACTED]</c>):</para>
/// <list type="bullet">
///   <item>Anthropic/OpenAI key pattern: <c>sk-ant-…</c> / <c>sk-…</c> (≥ 20 chars)</item>
///   <item>Bearer token values in Authorization-style strings</item>
///   <item>Generic API key query-string values: <c>api_key=…</c> / <c>token=…</c></item>
/// </list>
/// </summary>
public sealed class SecretMaskingTextFormatter(ITextFormatter inner) : ITextFormatter
{
    private static readonly Regex[] Patterns =
    [
        new Regex(@"sk-[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled),
        new Regex(@"(?i)bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.Compiled),
        new Regex(@"(?i)(api[_-]?key|token|secret)=[^&\s""']{8,}", RegexOptions.Compiled),
    ];

    public void Format(LogEvent logEvent, TextWriter output)
    {
        var buffer = new StringWriter(new StringBuilder(256));
        inner.Format(logEvent, buffer);
        output.Write(Mask(buffer.ToString()));
    }

    private static string Mask(string input)
    {
        foreach (var pattern in Patterns)
            input = pattern.Replace(input, "[REDACTED]");
        return input;
    }
}
