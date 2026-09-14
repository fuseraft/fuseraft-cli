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

    internal static bool LooksBinary(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[(int)Math.Min(SniffBytes, stream.Length)];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
        }
        catch { return false; }
    }
}
