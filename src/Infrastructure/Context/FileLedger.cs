using fuseraft.Core.Models.Config;
using System.Security.Cryptography;

namespace fuseraft.Infrastructure.Context;

/// <summary>One file that changed after the agent last saw it.</summary>
public sealed record FileChange(string Path, bool Deleted);

/// <summary>
/// What the agent has seen of each file through the file tools, so a change it didn't make through them (a shell command,
/// an editor, another process) is reported before it acts on an out-of-date copy, and a full rewrite from that copy is
/// refused until it reads the file again.
/// </summary>
public sealed class FileLedger
{
    /// <summary>The process's ledger: one REPL session or orchestration run per process, cleared when it starts over.</summary>
    public static FileLedger Shared { get; } = new();

    private const int  MaxHashBytes     = 4 * 1024 * 1024;

    private sealed class Entry
    {
        public long     Size;
        public DateTime WriteUtc;
        public string?  Hash;
        public bool     Stale;
        public long     LastUse;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private long _clock;
    private readonly System.Collections.Concurrent.ConcurrentQueue<FileChange> _toolChanges = new();

    /// <summary>
    /// Keep what <see cref="NoteToolChanges"/> is given for <see cref="TakeToolChanges"/>: on for an orchestration, whose
    /// change log counts a file a shell command rewrote as written, and off otherwise, so nothing piles up unread.
    /// </summary>
    public bool CollectToolChanges { get; set; }

    /// <summary>
    /// How many of the files seen most recently are tracked. Each is checked with one lookup before and after every tool
    /// call, which adds up on a network share. 0 turns tracking off: no change notices and no stale-write refusals.
    /// </summary>
    public int TrackedFiles { get; set; } = FilesConfig.DefaultTrackedFiles;

    /// <summary>Changes found while one of the agent's own tools ran, which its change log attributes to it.</summary>
    public void NoteToolChanges(IEnumerable<FileChange> changes)
    {
        if (!CollectToolChanges) return;
        foreach (var change in changes) _toolChanges.Enqueue(change);
    }

    /// <summary>Everything noted since the last call.</summary>
    public IReadOnlyList<FileChange> TakeToolChanges()
    {
        var taken = new List<FileChange>();
        while (_toolChanges.TryDequeue(out var change)) taken.Add(change);
        return taken;
    }

    /// <summary>The agent has just read or written <paramref name="path"/> through the file tools.</summary>
    public void RecordSeen(string path)
    {
        if (TrackedFiles <= 0) return;
        var info = new FileInfo(path);
        if (!info.Exists) { Forget(path); return; }
        var entry = Capture(info);
        lock (_lock)
        {
            entry.LastUse = ++_clock;
            _entries[path] = entry;
            while (_entries.Count > TrackedFiles)
                _entries.Remove(_entries.MinBy(e => e.Value.LastUse).Key);
        }
    }

    public void Forget(string path) { lock (_lock) _entries.Remove(path); }

    /// <summary>Drops everything: the conversation that saw these files is gone.</summary>
    public void Clear()
    {
        lock (_lock) _entries.Clear();
        _toolChanges.Clear();
    }

    /// <summary>
    /// True when the agent's copy of <paramref name="path"/> is out of date: it changed after the agent last read or
    /// wrote it, whether or not that change has been reported yet. False for a file it has never seen.
    /// </summary>
    public bool IsStale(string path)
    {
        Entry? entry;
        lock (_lock)
            if (!_entries.TryGetValue(path, out entry)) return false;
        if (entry.Stale) return true;
        var info = new FileInfo(path);
        return !info.Exists || Differs(entry, info);
    }

    /// <summary>Every seen file that has changed since it was last seen or swept; each is marked stale and reported once.</summary>
    public IReadOnlyList<FileChange> Sweep()
    {
        List<KeyValuePair<string, Entry>> entries;
        lock (_lock) entries = [.. _entries];

        var changes = new List<FileChange>();
        foreach (var (path, entry) in entries)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                lock (_lock) _entries.Remove(path);
                changes.Add(new FileChange(path, Deleted: true));
                continue;
            }
            if (!Differs(entry, info))
            {
                lock (_lock) entry.WriteUtc = info.LastWriteTimeUtc;   // a touch: don't hash it again next time
                continue;
            }

            var now = Capture(info);
            lock (_lock)
            {
                now.Stale   = true;
                now.LastUse = entry.LastUse;
                _entries[path] = now;
            }
            changes.Add(new FileChange(path, Deleted: false));
        }
        return changes;
    }

    // Size and timestamp first; the hash settles a touch that changed the timestamp but not the bytes.
    private static bool Differs(Entry entry, FileInfo info)
    {
        if (entry.Size == info.Length && entry.WriteUtc == info.LastWriteTimeUtc) return false;
        if (entry.Hash is null || info.Length != entry.Size) return true;
        try
        {
            return HashOf(info) != entry.Hash;
        }
        catch (IOException) { return true; }
    }

    private static Entry Capture(FileInfo info)
    {
        var entry = new Entry { Size = info.Length, WriteUtc = info.LastWriteTimeUtc };
        try
        {
            if (info.Length <= MaxHashBytes) entry.Hash = HashOf(info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return entry;
    }

    private static string HashOf(FileInfo info)
    {
        using var stream = info.OpenRead();
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
