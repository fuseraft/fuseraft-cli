using System.ComponentModel;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Provides agents with a shared, writable context summary for the current session.
///
/// <para>
/// Agents write a plain-text summary before handing off to a successor, and read it
/// immediately on re-entry to catch up on what was accomplished without re-reading
/// every source file from scratch. This is the primary defence against "state drift"
/// in long agentic sessions: the Developer writes what it implemented and which files
/// it touched; the Tester reads that summary to know where to focus; the Reviewer reads
/// it to understand what changed; on REVISION REQUIRED the Developer reads it again
/// rather than re-reading the full brief plus every source file.
/// </para>
///
/// <para>
/// The summary is plain text stored at
/// <c>.fuseraft/state/sessions/{session_id}/context_summary.md</c>. Agents may
/// use any format they find useful — bullet lists, structured notes, or prose.
/// Each call to <c>session_context_write</c> replaces the previous summary so the
/// file always reflects the current state of the session.
/// </para>
/// </summary>
public sealed class SessionContextPlugin
{
    private readonly string _summaryPath;

    public SessionContextPlugin(string summaryPath)
    {
        _summaryPath = summaryPath;
    }

    [Description("Read the session context summary written by the previous agent. Call this at the start of every turn to catch up without re-reading source files.")]
    public async Task<string> ReadAsync()
    {
        if (!File.Exists(_summaryPath))
            return PluginResult.Info(
                "No session context summary yet — this is the first turn or the previous agent did not write one. " +
                "Write a summary before handing off so the next agent has context.");

        var content = await File.ReadAllTextAsync(_summaryPath);
        if (string.IsNullOrWhiteSpace(content))
            return PluginResult.Info("Session context summary is empty.");

        return $"[Session context ({Path.GetFileName(_summaryPath)})]\n\n{content.Trim()}";
    }

    [Description("Write or update the session context summary. Call this before every handoff so the next agent knows what was done, what files were changed, and any known issues.")]
    public async Task<string> WriteAsync(
        [Description("Summary text — bullet points work well. Include: what was accomplished, files changed, open issues or constraints the next agent should know about.")] string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return PluginResult.Error("summary must not be empty.");

        var dir = Path.GetDirectoryName(_summaryPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(_summaryPath, summary.Trim());
        return PluginResult.Ok($"Session context updated → {_summaryPath}");
    }
}
