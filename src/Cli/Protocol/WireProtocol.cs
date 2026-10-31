using System.Text.Json;

namespace fuseraft.Cli.Protocol;

/// <summary>
/// The versioned wire contract that out-of-process clients (the VS Code extension, fuseraft-server,
/// CI scripts) depend on — documented in <c>docs/protocol.md</c>.
/// </summary>
public static class WireProtocol
{
    /// <summary>Bump only for a breaking change: a removed or renamed field or event, or a changed meaning.</summary>
    public const int Version = 1;

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Console.Out rather than AnsiConsole so Spectre never wraps or styles a JSON line.
    internal static TextWriter Writer { get; set; } = Console.Out;

    internal static string SerializeList<T>(IEnumerable<T> items) =>
        JsonSerializer.Serialize(new { protocolVersion = Version, items = items.ToArray() }, JsonOpts);

    internal static string SerializeError(string message) =>
        JsonSerializer.Serialize(new { protocolVersion = Version, error = message }, JsonOpts);

    internal static int WriteList<T>(IEnumerable<T> items)
    {
        Writer.WriteLine(SerializeList(items));
        return 0;
    }

    internal static int WriteError(string message)
    {
        Writer.WriteLine(SerializeError(message));
        return 1;
    }
}
