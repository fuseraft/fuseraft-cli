namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Detects binary files before a plugin commits to reading them as text. .NET's default UTF-8
/// decoding is lenient — invalid byte sequences become replacement characters rather than
/// throwing — so a compiled binary (.dll/.pdb, image, archive) "reads" successfully as text via
/// <c>File.ReadAllLines</c>/<c>StreamReader</c>, and if it happens to lack a newline byte for a
/// long stretch, a single decoded "line" can span hundreds of KB, blowing out a tool result
/// (and, downstream, the model's context) from a single match. A NUL byte is never legal in
/// genuine text, so sniffing for one in the first few KB catches this cheaply before the file
/// is read in full. Shared by every plugin that walks or greps files the model didn't pick with
/// an exact extension in mind.
/// </summary>
internal static class BinaryFileSniffer
{
    private const int SniffBytes = 8000;

    // UTF-16/UTF-32 text is exactly as "legal" as UTF-8 — Notepad's "Save As," PowerShell ISE,
    // and various Visual Studio templates all default to it — but it puts a 0x00 byte after
    // (or before) every ASCII character, which is indistinguishable from binary content under a
    // bare NUL-byte scan. A byte-order mark is the standard, unambiguous signal that the bytes
    // that follow are one of these wide-char text encodings, not binary — checked first so a
    // real text file is never rejected as binary just because of how it encodes ASCII.
    private static readonly byte[][] TextByteOrderMarks =
    [
        [0xEF, 0xBB, 0xBF],       // UTF-8
        [0xFF, 0xFE, 0x00, 0x00], // UTF-32 LE (checked before UTF-16 LE — it's a superset prefix)
        [0x00, 0x00, 0xFE, 0xFF], // UTF-32 BE
        [0xFF, 0xFE],             // UTF-16 LE
        [0xFE, 0xFF],             // UTF-16 BE
    ];

    internal static bool LooksBinary(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[(int)Math.Min(SniffBytes, stream.Length)];
            var read = stream.Read(buffer, 0, buffer.Length);

            if (TextByteOrderMarks.Any(bom => read >= bom.Length && buffer.AsSpan(0, bom.Length).SequenceEqual(bom)))
                return false;

            return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
        }
        catch { return false; }
    }
}
