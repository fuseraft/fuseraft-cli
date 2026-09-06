using System.Text.Json;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Records the pre-mutation state of files touched by <c>write_file</c>/<c>patch_file</c>/
/// <c>delete_file</c> so the REPL's <c>/undo</c> command can revert the most recent turn's
/// file changes on disk — separate from and complementary to conversation-level <c>/rewind</c>,
/// which only manipulates chat history and never touches the filesystem.
///
/// <para>
/// Constructed disabled (<see cref="Enable"/> not yet called) so it can be created eagerly by
/// <see cref="FileSystemPlugin"/> before a session ID is known, then activated once the
/// session-scoped snapshot directory is resolved. Every method is a no-op while disabled,
/// matching the "null path = off" convention used by <c>SessionReadCache</c> and
/// <c>ToolResultArtifactStore</c>.
/// </para>
///
/// <para>
/// One snapshot is captured per distinct path per turn — the first mutation of a path within a
/// turn captures its pre-turn state; later mutations to the same path in the same turn do not
/// re-snapshot, so <c>/undo</c> always restores to "before this turn started," not to some
/// intermediate state mid-turn. <see cref="BeginTurn"/> must be called once per agent turn
/// (wired through <c>FileSystemPlugin.BeginTurn()</c>) to advance the turn counter and clear
/// the per-turn dedup set.
/// </para>
/// </summary>
internal sealed class UndoSnapshotStore
{
    private string? _dir;
    private int _turn;
    private readonly HashSet<string> _recordedThisTurn = new(StringComparer.OrdinalIgnoreCase);

    private string ManifestPath => Path.Combine(_dir!, "manifest.jsonl");
    private string BlobsDir     => Path.Combine(_dir!, "blobs");

    /// <summary>Activates snapshotting into <paramref name="snapshotDir"/>. No-op if called more than once.</summary>
    internal void Enable(string snapshotDir) => _dir ??= snapshotDir;

    /// <summary>Advances the turn counter and clears the per-turn dedup set. Call once per agent turn.</summary>
    internal void BeginTurn()
    {
        _turn++;
        _recordedThisTurn.Clear();
    }

    /// <summary>
    /// Captures <paramref name="resolvedPath"/>'s current on-disk state before it is mutated,
    /// unless this path was already recorded earlier in the current turn. Pass
    /// <paramref name="knownContent"/> when the caller already has the pre-mutation text in
    /// memory (e.g. <c>patch_file</c>) to avoid a redundant read.
    /// </summary>
    internal async Task RecordBeforeMutationAsync(string resolvedPath, string? knownContent = null)
    {
        if (_dir is null) return;
        if (!_recordedThisTurn.Add(resolvedPath)) return;

        var existed = knownContent is not null || File.Exists(resolvedPath);
        string? blobFile = null;

        if (existed)
        {
            try
            {
                var bytes = knownContent is not null
                    ? System.Text.Encoding.UTF8.GetBytes(knownContent)
                    : await File.ReadAllBytesAsync(resolvedPath);
                Directory.CreateDirectory(BlobsDir);
                blobFile = $"{_turn}_{Guid.NewGuid():N}.blob";
                await File.WriteAllBytesAsync(Path.Combine(BlobsDir, blobFile), bytes);
            }
            catch
            {
                // Best-effort: if the snapshot write fails, skip recording rather than fail
                // the tool call that triggered it. /undo simply won't have this path available.
                return;
            }
        }

        try
        {
            Directory.CreateDirectory(_dir);
            var line = JsonSerializer.Serialize(new UndoManifestEntry(_turn, resolvedPath, existed, blobFile));
            await File.AppendAllTextAsync(ManifestPath, line + Environment.NewLine);
        }
        catch { /* best-effort, same rationale as above */ }
    }

    /// <summary>
    /// Restores every path touched in the most recent still-recorded turn and removes those
    /// entries from the manifest. Returns <c>null</c> when there is nothing to undo. Calling
    /// this repeatedly walks backward turn by turn; there is no redo.
    /// </summary>
    internal async Task<UndoResult?> UndoLastTurnAsync()
    {
        if (_dir is null) return null;

        var entries = await ReadManifestAsync();
        if (entries.Count == 0) return null;

        var maxTurn   = entries.Max(e => e.Turn);
        var toRestore = entries.Where(e => e.Turn == maxTurn).ToList();
        var remaining = entries.Where(e => e.Turn != maxTurn).ToList();

        var actions = new List<UndoAction>();
        foreach (var entry in toRestore)
        {
            if (entry.Existed && entry.BlobFile is not null)
            {
                var blobPath = Path.Combine(BlobsDir, entry.BlobFile);
                if (File.Exists(blobPath))
                {
                    var bytes = await File.ReadAllBytesAsync(blobPath);
                    var dir = Path.GetDirectoryName(entry.Path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    await File.WriteAllBytesAsync(entry.Path, bytes);
                    actions.Add(new UndoAction(entry.Path, "reverted"));
                }
                else
                {
                    actions.Add(new UndoAction(entry.Path, "snapshot missing — could not restore"));
                }
            }
            else
            {
                if (File.Exists(entry.Path)) File.Delete(entry.Path);
                actions.Add(new UndoAction(entry.Path, "deleted (did not exist before this turn)"));
            }
        }

        await WriteManifestAsync(remaining);
        return new UndoResult(maxTurn, actions);
    }

    private async Task<List<UndoManifestEntry>> ReadManifestAsync()
    {
        if (!File.Exists(ManifestPath)) return [];
        var result = new List<UndoManifestEntry>();
        foreach (var line in await File.ReadAllLinesAsync(ManifestPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<UndoManifestEntry>(line);
                if (entry is not null) result.Add(entry);
            }
            catch { /* skip a corrupted line rather than fail the whole read */ }
        }
        return result;
    }

    private async Task WriteManifestAsync(List<UndoManifestEntry> entries)
    {
        var lines = entries.Select(e => JsonSerializer.Serialize(e));
        await File.WriteAllLinesAsync(ManifestPath, lines);
    }
}

internal sealed record UndoManifestEntry(int Turn, string Path, bool Existed, string? BlobFile);

internal sealed record UndoAction(string Path, string Description);

internal sealed record UndoResult(int TurnRestored, IReadOnlyList<UndoAction> Actions);
