using System.Text;
using System.Text.Json;

namespace fuseraft.Cli.Commands.Log;

/// <summary>
/// A REPL session's prompt-cache use, read from its event log: how much of each request the provider served from
/// cache, and for each request it mostly didn't, why — nothing changed (a cold or evicted cache), the session sat
/// idle past the cache's lifetime, or fuseraft rewrote the history (compaction, trimming, dropping stale reads).
/// </summary>
internal sealed class CacheReport
{
    /// <summary>A request counts as a miss when less than this share of its prompt came from cache.</summary>
    internal const double MissShare = 0.5;

    /// <summary>Requests below the usual minimum cacheable prompt aren't counted as misses.</summary>
    internal const long MinCacheableTokens = 1_024;

    /// <summary>The shortest cache lifetime in common use (Anthropic and Bedrock's default).</summary>
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    internal const string FirstRequest = "first request";
    internal const string Idle         = "idle over 5 min";
    internal const string Unchanged    = "nothing changed";
    internal const string Compaction   = "compaction";
    internal const string HistoryTrim  = "history trim";
    internal const string History      = "history rewritten";
    internal const string StaleDrop    = "stale-read drop";
    internal const string InTurnTrim   = "in-turn trim";
    internal const string Overflow     = "overflow retry";
    internal const string Tools        = "tools changed";

    private static readonly string[] Order =
        [FirstRequest, Idle, Unchanged, Compaction, HistoryTrim, History, StaleDrop, InTurnTrim, Overflow, Tools];

    public string? Session { get; private set; }
    public string? Model { get; private set; }
    public int Sessions { get; private set; } = 1;
    public int Turns { get; private set; }
    public int Requests { get; private set; }
    public int SubagentRequests { get; private set; }

    /// <summary>Requests logged by a fuseraft too old to record their cache use.</summary>
    public int Unmeasured { get; private set; }

    public long PromptTokens { get; private set; }
    public long CachedTokens { get; private set; }
    public long WrittenTokens { get; private set; }
    public int Compactions { get; private set; }
    public long CompactedFromTokens { get; private set; }

    /// <summary>Main-thread requests made between one compaction and the next (or the session start).</summary>
    public List<int> RequestsPerCompaction { get; } = [];

    public Dictionary<string, (int Count, long Uncached)> Misses { get; } = [];

    /// <summary>The input limit the session learned from its model's provider, if it logged one.</summary>
    public (int Tokens, string Source)? ModelLimit { get; private set; }

    public int MissCount => Misses.Values.Sum(m => m.Count);

    /// <summary>Reads one session's events, oldest first.</summary>
    public static CacheReport Analyze(IEnumerable<JsonElement> events)
    {
        var report = new CacheReport();
        var lastRequestAt = new Dictionary<string, DateTimeOffset>();
        var models = new Dictionary<string, int>();
        bool compacted = false, trimmed = false;
        var sinceCompaction = 0;

        foreach (var e in events)
        {
            var type = Str(e, "event_type");
            report.Session ??= Str(e, "session");
            var payload = e.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
            switch (type)
            {
                case "session_start":
                    report.Model ??= Str(payload, "model");
                    break;
                case "turn_end":
                    report.Turns++;
                    break;
                case "compaction":
                    report.Compactions++;
                    report.CompactedFromTokens += Long(payload, "before_tokens") ?? 0;
                    report.RequestsPerCompaction.Add(sinceCompaction);
                    sinceCompaction = 0;
                    compacted = true;
                    break;
                case "history_trimmed":
                    trimmed = true;
                    break;
                case "model_limit":
                    if (Long(payload, "input_tokens") is { } limit && Str(payload, "model") is { } limited
                        && (report.Model is null || limited == report.Model))
                        report.ModelLimit = ((int)limit, Str(payload, "source") ?? "the provider");
                    break;
                case "model_response":
                    var run = Str(payload, "run");
                    if (run is null)
                    {
                        report.Requests++;
                        sinceCompaction++;
                        if (Str(payload, "model") is { } model)
                            models[model] = models.GetValueOrDefault(model) + 1;
                    }
                    else
                    {
                        report.SubagentRequests++;
                    }

                    var at = DateTimeOffset.TryParse(Str(e, "ts"), out var ts) ? ts : (DateTimeOffset?)null;
                    var key = run ?? "";
                    var idle = at is { } now && lastRequestAt.TryGetValue(key, out var before) && now - before > CacheLifetime;
                    if (at is { } t) lastRequestAt[key] = t;

                    if (Long(payload, "prompt_tokens") is not { } prompt)
                    {
                        report.Unmeasured++;
                        break;
                    }
                    var cached = Long(payload, "cached_input_tokens") ?? 0;
                    report.PromptTokens  += prompt;
                    report.CachedTokens  += cached;
                    report.WrittenTokens += Long(payload, "cache_write_tokens") ?? 0;

                    if (prompt >= MinCacheableTokens && cached < prompt * MissShare)
                    {
                        var reason = Reason(Str(payload, "prefix_change_cause"), Long(payload, "previous_message_count") ?? 0,
                            idle, compacted, trimmed);
                        var (count, uncached) = report.Misses.GetValueOrDefault(reason);
                        report.Misses[reason] = (count + 1, uncached + prompt - cached);
                    }
                    if (run is null)
                        compacted = trimmed = false;
                    break;
            }
        }

        if (models.Count > 0)
            report.Model = models.MaxBy(m => m.Value).Key;
        return report;
    }

    private static string Reason(string? cause, long previousCount, bool idle, bool compacted, bool trimmed) => cause switch
    {
        "tools"         => Tools,
        "stale_drop"    => StaleDrop,
        "in_turn_trim"  => InTurnTrim,
        "overflow_trim" => Overflow,
        "history"       => compacted ? Compaction : trimmed ? HistoryTrim : History,
        _ when previousCount == 0 => FirstRequest,
        _ when idle               => Idle,
        _                         => Unchanged,
    };

    /// <summary>Adds up several sessions' reports.</summary>
    public static CacheReport Combine(IReadOnlyList<CacheReport> reports)
    {
        var total = new CacheReport { Sessions = reports.Count };
        foreach (var r in reports)
        {
            total.Turns               += r.Turns;
            total.Requests            += r.Requests;
            total.SubagentRequests    += r.SubagentRequests;
            total.Unmeasured          += r.Unmeasured;
            total.PromptTokens        += r.PromptTokens;
            total.CachedTokens        += r.CachedTokens;
            total.WrittenTokens       += r.WrittenTokens;
            total.Compactions         += r.Compactions;
            total.CompactedFromTokens += r.CompactedFromTokens;
            total.RequestsPerCompaction.AddRange(r.RequestsPerCompaction);
            foreach (var (reason, (count, uncached)) in r.Misses)
            {
                var (c, u) = total.Misses.GetValueOrDefault(reason);
                total.Misses[reason] = (c + count, u + uncached);
            }
        }
        var models = reports.Select(r => r.Model).Where(m => m is not null).Distinct().ToList();
        total.Model = models.Count == 1 ? models[0] : models.Count > 1 ? $"{models.Count} models" : null;
        return total;
    }

    /// <summary>The report as plain text, short enough to read off a screen.</summary>
    /// <param name="budget">The REPL's context budget for the model, in tokens, and where it came from.</param>
    /// <param name="compactAt">The share of the budget at which the REPL compacts.</param>
    public string Render((int Tokens, string Source)? budget = null, double? compactAt = null)
    {
        var sb = new StringBuilder();
        var title = Sessions == 1 ? $"Session {Session ?? "?"}" : $"{Sessions} sessions";
        sb.AppendLine($"{title} · {Model ?? "unknown model"} · {Requests + SubagentRequests:N0} requests · {Turns:N0} turns");

        if (PromptTokens == 0)
        {
            sb.AppendLine(Unmeasured > 0
                ? "  No cache data: these requests were logged by a fuseraft that didn't record it."
                : "  No requests logged.");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine($"  Prompt tokens   {Tokens(PromptTokens)}   cached {Share(CachedTokens, PromptTokens)}   written {Tokens(WrittenTokens)}");
        if (SubagentRequests > 0)
            sb.AppendLine($"  Subagents       {SubagentRequests:N0} of those requests");
        if (Unmeasured > 0)
            sb.AppendLine($"  Not measured    {Unmeasured:N0} requests from an older fuseraft");

        var measured = Requests + SubagentRequests - Unmeasured;
        sb.AppendLine($"  Cache misses    {MissCount:N0} of {measured:N0} requests under {MissShare:P0} cached");
        foreach (var reason in Order.Where(Misses.ContainsKey))
        {
            var (count, uncached) = Misses[reason];
            sb.AppendLine($"    {reason,-18} {count,5:N0}   {Tokens(uncached),7} uncached");
        }

        sb.AppendLine(Compactions == 0
            ? "  Compactions     none"
            : $"  Compactions     {Compactions:N0} · one every {RequestsPerCompaction.Average():N0} requests · " +
              $"at ~{Tokens(CompactedFromTokens / Compactions)} tokens");

        if (budget is { } b)
        {
            var at = compactAt is { } c ? $" · compacts at {c:P0} ({Tokens((long)(b.Tokens * c))})" : "";
            sb.AppendLine($"  Budget          {Tokens(b.Tokens)} ({b.Source}){at}");
        }
        return sb.ToString().TrimEnd();
    }

    internal static string Tokens(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 1_000     => $"{n / 1_000.0:0.#}K",
        _            => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string Share(long part, long whole) => whole == 0 ? "-" : $"{(double)part / whole:P0}";

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64() : null;
}
