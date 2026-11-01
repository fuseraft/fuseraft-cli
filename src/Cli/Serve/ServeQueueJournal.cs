using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace fuseraft.Cli.Serve;

/// <summary>
/// Keeps the daemon's unfinished tasks, and the results its clients haven't collected, in a file, so a daemon that
/// restarts (a crash, a reboot, an upgrade) runs them again and still delivers them. A task that was running resumes
/// from its saved session. Task text is saved as given, so the file is readable only by the daemon's user.
/// </summary>
public sealed class ServeQueueJournal
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _path;
    private readonly ServeTaskQueue _queue;
    private readonly ServeAttachClients _clients;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();

    public ServeQueueJournal(string path, ServeTaskQueue queue, ServeAttachClients clients, ILogger logger)
    {
        _path    = path;
        _queue   = queue;
        _clients = clients;
        _logger  = logger;
    }

    /// <summary>
    /// Queues what the file holds and starts keeping it up to date. Call before the daemon accepts tasks.
    /// Returns how many tasks it restored.
    /// </summary>
    public int Restore()
    {
        var saved = Load();
        foreach (var group in saved.Results.GroupBy(r => (r.ClientId, r.TokenName)))
            _clients.Restore(new AttachIdentity(group.Key.ClientId, group.Key.TokenName))
                .Restore(group.Select(r => (r.SessionId, r.Result)));

        // The task that was running ran ahead of every queued one, so it goes first again.
        var tasks = saved.Tasks.OrderByDescending(t => t.Running).ThenBy(t => t.CreatedAt).ToList();
        foreach (var t in tasks)
        {
            var origin = t.ClientId is null ? null : _clients.Restore(new AttachIdentity(t.ClientId, t.TokenName));
            var record = _queue.Restore(t.SessionId, t.Task, t.ObjectiveId, origin, t.CreatedAt, t.Running,
                t.Running ? t.Interruptions + 1 : t.Interruptions);
            origin?.Track(record);
        }

        _queue.Changed   += Save;
        _clients.Changed += Save;
        Save();
        return tasks.Count;
    }

    private void Save()
    {
        lock (_lock)
        {
            var file = new JournalFile
            {
                Tasks = [.. _queue.Unfinished.Select(r => new SavedTask
                {
                    SessionId   = r.SessionId,
                    Task        = r.Task,
                    ObjectiveId = r.ObjectiveId,
                    ClientId    = (r.OriginSink as ServeAttachClient)?.Identity.ClientId,
                    TokenName   = (r.OriginSink as ServeAttachClient)?.Identity.TokenName,
                    Running       = r.Status == ServeTaskStatus.Running,
                    Interruptions = r.Interruptions,
                    CreatedAt     = r.CreatedAt,
                })],
                Results = [.. _clients.WithUndelivered.SelectMany(c => c.Undelivered.Select(u => new SavedResult
                {
                    ClientId  = c.Identity.ClientId!,
                    TokenName = c.Identity.TokenName,
                    SessionId = u.SessionId,
                    Result    = u.Result,
                }))],
            };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(file, Json));
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The tasks still run; only a restart before the next good save would lose them.
                _logger.LogWarning(ex, "Couldn't save the task queue to {Path}", _path);
            }
        }
    }

    private JournalFile Load()
    {
        if (!File.Exists(_path)) return new JournalFile();
        try
        {
            return JsonSerializer.Deserialize<JournalFile>(File.ReadAllText(_path), Json) ?? new JournalFile();
        }
        catch (JsonException ex)
        {
            var aside = _path + ".corrupt";
            File.Move(_path, aside, overwrite: true);
            _logger.LogError(ex, "The task queue in {Path} isn't valid JSON; starting with an empty queue (kept it as {Aside})", _path, aside);
            return new JournalFile();
        }
    }

    private sealed class JournalFile
    {
        public List<SavedTask>   Tasks   { get; set; } = [];
        public List<SavedResult> Results { get; set; } = [];
    }

    private sealed class SavedTask
    {
        public string  SessionId   { get; set; } = "";
        public string  Task        { get; set; } = "";
        public string? ObjectiveId { get; set; }
        public string? ClientId    { get; set; }
        public string? TokenName   { get; set; }
        public bool    Running     { get; set; }
        public int     Interruptions { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed class SavedResult
    {
        public string      ClientId  { get; set; } = "";
        public string?     TokenName { get; set; }
        public string      SessionId { get; set; } = "";
        public JsonElement Result    { get; set; }
    }
}
