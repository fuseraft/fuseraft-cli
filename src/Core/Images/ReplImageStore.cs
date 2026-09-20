using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace fuseraft.Core.Images;

/// <summary>
/// Content-addressed sidecar storage for images attached to REPL messages. A session snapshot is rewritten
/// after every turn, so embedding image bytes in it would re-serialize megabytes each time; instead the
/// snapshot records a short id and the bytes live here once, keyed by their SHA-256. Identical images
/// (the same screenshot re-attached, or carried through a fork) share one file.
/// </summary>
public static class ReplImageStore
{
    // <24 hex of SHA-256>.<ext> — nothing else is ever a valid id, which is what keeps a hand-edited or
    // hostile snapshot from turning an image reference into a path traversal.
    private static readonly Regex IdPattern = new(@"^[0-9a-f]{24}\.(png|jpg|gif|webp)$", RegexOptions.Compiled);

    public static string Root => Path.Combine(FuseraftPaths.GlobalReplSessions, "images");

    public static bool IsValidId(string? id) => id is not null && IdPattern.IsMatch(id);

    public static string IdFor(byte[] bytes, string mediaType) =>
        $"{Convert.ToHexString(SHA256.HashData(bytes))[..24].ToLowerInvariant()}.{ExtensionFor(mediaType)}";

    /// <summary>Writes <paramref name="bytes"/> if not already present and returns its id, or <c>null</c> if it cannot be stored.</summary>
    public static string? Save(byte[] bytes, string mediaType)
    {
        try
        {
            var id   = IdFor(bytes, mediaType);
            var path = Path.Combine(Root, id);
            if (File.Exists(path)) return id;

            Directory.CreateDirectory(Root);
            // Temp-then-move so a crash mid-write never leaves a truncated file under a valid id.
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, path, overwrite: true);
            return id;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Bytes for <paramref name="id"/>, or <c>null</c> when the id is malformed, missing, or unreadable.</summary>
    public static byte[]? Load(string? id)
    {
        if (!IsValidId(id)) return null;
        try
        {
            var path = Path.Combine(Root, id!);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ExtensionFor(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/png"  => "png",
        "image/jpeg" => "jpg",
        "image/gif"  => "gif",
        "image/webp" => "webp",
        _            => "png",
    };
}
