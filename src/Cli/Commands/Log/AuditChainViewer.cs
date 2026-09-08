using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace fuseraft.Cli.Commands.Log;

internal static class AuditChainViewer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal static async Task<int> RenderAsync(
        IReadOnlyList<string> paths,
        int? last,
        string? agentFilter,
        string? decisionFilter,
        CancellationToken ct)
    {
        var existing = paths.Where(File.Exists).ToList();
        if (existing.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No audit log found.[/]");
            if (paths.Count == 1)
                AnsiConsole.MarkupLine($"[dim]Expected path: {Markup.Escape(paths[0])}[/]");
            return 0;
        }

        var rows = new List<(string Path, AuditLogEntry Entry)>();
        foreach (var path in existing)
        {
            await foreach (var line in File.ReadLinesAsync(path, ct))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<AuditLogEntry>(line, JsonOpts);
                    if (entry is not null) rows.Add((path, entry));
                }
                catch { /* skip malformed lines */ }
            }
        }

        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]Audit log is empty.[/]");
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(agentFilter))
            rows = rows.Where(r => (r.Entry.AgentId ?? string.Empty)
                    .Contains(agentFilter.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (!string.IsNullOrWhiteSpace(decisionFilter))
            rows = rows.Where(r => string.Equals(r.Entry.Decision, decisionFilter.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No entries match the specified filters.[/]");
            return 0;
        }

        if (last is > 0)
            rows = rows.TakeLast(last.Value).ToList();

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn(new TableColumn("[bold]Seq[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Time[/]"))
            .AddColumn(new TableColumn("[bold]Agent[/]"))
            .AddColumn(new TableColumn("[bold]Action[/]"))
            .AddColumn(new TableColumn("[bold]Decision[/]"))
            .AddColumn(new TableColumn("[bold]Hash[/]"));

        foreach (var (_, e) in rows)
        {
            table.AddRow(
                $"[dim]{e.Seq}[/]",
                $"[dim]{Markup.Escape(e.Timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss"))}[/]",
                $"[dim]{Markup.Escape(Truncate(e.AgentId ?? "-", 28))}[/]",
                Markup.Escape(e.Action ?? "-"),
                ColorizeDecision(e.Decision),
                $"[dim]{Markup.Escape(HashPrefix(e.Hash))}[/]");
        }

        AnsiConsole.Write(table);

        var denyCount = rows.Count(r => string.Equals(r.Entry.Decision, "deny", StringComparison.OrdinalIgnoreCase));
        AnsiConsole.MarkupLine($"[dim]{rows.Count} entr{(rows.Count == 1 ? "y" : "ies")}  ·  {denyCount} deny[/]");
        var logLabel = existing.Count == 1 ? existing[0] : $"{existing.Count} audit log(s)";
        AnsiConsole.MarkupLine($"[dim]log: {Markup.Escape(logLabel)}[/]");

        return 0;
    }

    internal static async Task<int> VerifyAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var existing = paths.Where(File.Exists).ToList();
        if (existing.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No audit log found.[/]");
            if (paths.Count == 1)
                AnsiConsole.MarkupLine($"[dim]Expected path: {Markup.Escape(paths[0])}[/]");
            return 0;
        }

        var anyFailed = false;
        foreach (var path in existing)
        {
            var (ok, checkedCount, failure) = await VerifyFileAsync(path, ct);
            if (ok)
            {
                AnsiConsole.MarkupLine($"[green]✓ intact[/]  {Markup.Escape(path)}  [dim]({checkedCount} entries)[/]");
            }
            else
            {
                anyFailed = true;
                AnsiConsole.MarkupLine($"[red]✗ TAMPERED[/]  {Markup.Escape(path)}");
                AnsiConsole.MarkupLine($"[red]  {Markup.Escape(failure ?? "unknown failure")}[/]");
            }
        }

        return anyFailed ? 1 : 0;
    }

    private static async Task<(bool Ok, int Checked, string? Failure)> VerifyFileAsync(string path, CancellationToken ct)
    {
        var seq = 0;
        var previousHash = string.Empty;

        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            AuditLogEntry? entry;
            try { entry = JsonSerializer.Deserialize<AuditLogEntry>(line, JsonOpts); }
            catch (JsonException ex) { return (false, seq, $"line {seq + 1}: malformed JSON ({ex.Message})"); }

            if (entry is null) return (false, seq, $"line {seq + 1}: malformed JSON");

            if (entry.Seq != seq)
                return (false, seq, $"seq {entry.Seq}: expected seq {seq} — an entry was reordered, inserted, or removed");

            if (entry.PreviousHash != previousHash)
                return (false, seq, $"seq {entry.Seq}: previous_hash does not match the prior entry's hash — chain link broken");

            var recomputed = ComputeHash(entry.Seq, entry.Timestamp, entry.AgentId ?? "", entry.Action ?? "", entry.Decision ?? "", entry.PreviousHash ?? "");
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(recomputed), Encoding.UTF8.GetBytes(entry.Hash ?? "")))
                return (false, seq, $"seq {entry.Seq}: stored hash does not match the recomputed hash — entry contents were modified");

            previousHash = entry.Hash ?? "";
            seq++;
        }

        return (true, seq, null);
    }

    // Mirrors AgentGovernance.Audit.AuditLogger's private ComputeHash exactly (verified by
    // decompilation against v5.0.0): sha256("{seq}|{timestamp:O}|{agentId}|{action}|{decision}|{previousHash}"),
    // lowercase hex. The package does not expose this publicly, so re-verifying a persisted
    // chain from outside the process that wrote it requires reimplementing it here.
    private static string ComputeHash(long seq, DateTimeOffset timestamp, string agentId, string action, string decision, string previousHash)
    {
        var s = $"{seq}|{timestamp:O}|{agentId}|{action}|{decision}|{previousHash}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    }

    private static string ColorizeDecision(string? decision) => decision?.ToLowerInvariant() switch
    {
        "deny"  => "[red]deny[/]",
        "allow" => "[green]allow[/]",
        _       => Markup.Escape(decision ?? "-"),
    };

    private static string HashPrefix(string? hash) =>
        string.IsNullOrEmpty(hash) ? "-" : hash[..Math.Min(12, hash.Length)];

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private sealed class AuditLogEntry
    {
        [JsonPropertyName("seq")]           public long   Seq          { get; init; }
        [JsonPropertyName("timestamp")]     public DateTimeOffset Timestamp { get; init; }
        [JsonPropertyName("agent_id")]      public string? AgentId     { get; init; }
        [JsonPropertyName("action")]        public string? Action      { get; init; }
        [JsonPropertyName("decision")]      public string? Decision    { get; init; }
        [JsonPropertyName("previous_hash")] public string? PreviousHash { get; init; }
        [JsonPropertyName("hash")]          public string? Hash        { get; init; }
    }
}
