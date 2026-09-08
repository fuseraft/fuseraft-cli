using System.ComponentModel;
using Spectre.Console.Cli;
using fuseraft.Core;

namespace fuseraft.Cli.Commands.Log;

// fuseraft log audit

public sealed class LogAuditSettings : CommandSettings
{
    [CommandOption("-n|--last")]
    [Description("Show only the last N entries.")]
    public int? Last { get; set; }

    [CommandOption("--session")]
    [Description("Filter by session ID (prefix match).")]
    public string? Session { get; set; }

    [CommandOption("--agent")]
    [Description("Filter by agent ID (substring match).")]
    public string? Agent { get; set; }

    [CommandOption("--decision")]
    [Description("Filter by decision: allow or deny.")]
    public string? Decision { get; set; }

    [CommandOption("--path")]
    [Description("Override the log file path. When omitted, resolves by --session or reads all sessions.")]
    public string? Path { get; set; }

    [CommandOption("--verify")]
    [Description("Recompute the hash chain from disk and report whether it is intact, instead of listing entries.")]
    public bool Verify { get; set; }
}

public sealed class LogAuditCommand : AsyncCommand<LogAuditSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, LogAuditSettings settings, CancellationToken cancellationToken)
    {
        var paths = ResolvePaths(settings);

        return settings.Verify
            ? await AuditChainViewer.VerifyAsync(paths, cancellationToken)
            : await AuditChainViewer.RenderAsync(paths, settings.Last, settings.Agent, settings.Decision, cancellationToken);
    }

    private static IReadOnlyList<string> ResolvePaths(LogAuditSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Path))
            return [FuseraftPaths.ExpandPath(settings.Path)];

        var globalSessionsRoot = System.IO.Path.Combine(FuseraftPaths.GlobalRoot, "sessions");

        if (!string.IsNullOrWhiteSpace(settings.Session))
        {
            string? path = null;
            if (Directory.Exists(globalSessionsRoot))
            {
                path = Directory.GetDirectories(globalSessionsRoot)
                    .SelectMany(Directory.GetDirectories)
                    .FirstOrDefault(d => System.IO.Path.GetFileName(d)
                        .StartsWith(settings.Session, StringComparison.OrdinalIgnoreCase));
                if (path is not null)
                    path = System.IO.Path.Combine(path, "audit-chain.jsonl");
            }
            path ??= System.IO.Path.GetFullPath(
                FuseraftPaths.ExpandSessionId(FuseraftPaths.LocalAuditChainLog, settings.Session));
            return [path];
        }

        // No session specified — collect every session's audit chain across every project.
        return Directory.Exists(globalSessionsRoot)
            ? Directory.GetDirectories(globalSessionsRoot)
                .SelectMany(Directory.GetDirectories)
                .Select(d => System.IO.Path.Combine(d, "audit-chain.jsonl"))
                .Where(File.Exists)
                .OrderBy(p => p)
                .ToList()
            : [];
    }
}
