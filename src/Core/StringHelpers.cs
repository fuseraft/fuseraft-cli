namespace fuseraft.Core;

internal static class StringHelpers
{
    internal static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    internal static string NewSessionId() => Guid.NewGuid().ToString("N")[..8];
}
