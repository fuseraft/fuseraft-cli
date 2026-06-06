#!/usr/bin/env python3
"""Analyze fuseraft REPL sessions and crash dumps for runtime issues."""

import json
import re
import sys
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path

SESSIONS_DIR = Path.home() / ".fuseraft" / "repl-sessions"
CRASHDUMP_DIR = Path.home() / ".fuseraft" / "crashdump"
GLOBAL_EVENT_LOG = Path.home() / ".fuseraft" / "repl_events.jsonl"
GLOBAL_TOKEN_SESSIONS_DIR = Path.home() / ".fuseraft" / "logs" / "sessions"

# ── helpers ───────────────────────────────────────────────────────────────────

def parse_dt(s: str) -> datetime | None:
    if not s:
        return None
    s = s.rstrip("Z")
    # strip sub-second precision beyond microseconds
    if "." in s:
        base, frac = s.split(".", 1)
        frac = frac[:6]
        s = f"{base}.{frac}"
    try:
        return datetime.fromisoformat(s).replace(tzinfo=timezone.utc)
    except ValueError:
        return None


def fmt_duration(seconds: float) -> str:
    if seconds < 60:
        return f"{seconds:.0f}s"
    m, s = divmod(int(seconds), 60)
    if m < 60:
        return f"{m}m{s:02d}s"
    h, m = divmod(m, 60)
    return f"{h}h{m:02d}m"


def truncate(text: str, n: int = 120) -> str:
    return text if len(text) <= n else text[:n] + "…"


def flatten_exception(exc: dict, depth: int = 0) -> list[dict]:
    """Flatten nested exception chain into a flat list."""
    result = [{"depth": depth, "type": exc.get("type", ""), "message": exc.get("message", "")}]
    inner = exc.get("inner")
    if inner:
        result.extend(flatten_exception(inner, depth + 1))
    return result


TOOL_FAIL_PATTERNS = [
    (re.compile(r"oldText not found", re.I), "patch_file: oldText not found"),
    (re.compile(r"file not found", re.I), "read/write: file not found"),
    (re.compile(r"startLine exceeds file length", re.I), "read_file: startLine out of range"),
    (re.compile(r"exit code [1-9]", re.I), "shell_run: non-zero exit"),
    (re.compile(r"import error", re.I), "shell_run: import error"),
    (re.compile(r"ModuleNotFoundError", re.I), "shell_run: ModuleNotFoundError"),
    (re.compile(r"list_files is blocked", re.I), "list_files: blocked on .fuseraft/"),
    (re.compile(r"ValidatorStuckException", re.I), "orchestration: ValidatorStuckException"),
    (re.compile(r"iteration cap", re.I), "orchestration: iteration cap hit"),
]

SPURIOUS_WRITE_INJECT = re.compile(
    r"You described changes above but did not call any write tool", re.I
)

CRASH_SIGNATURES = {
    # network / provider
    "network_timeout":   re.compile(r"exceeded the configured timeout", re.I),
    "aggregate_retry":   re.compile(r"Retry failed after \d+ tries", re.I),
    "socket_cancel":     re.compile(r"SocketException.*Operation canceled", re.I),
    "http_5xx":          re.compile(r"Status:\s*5\d\d", re.I),
    "http_4xx":          re.compile(r"Status:\s*4\d\d", re.I),
    # orchestration / config
    "unknown_plugin":    re.compile(r"references unknown plugin", re.I),
    "compaction_error":  re.compile(r"Cannot compact a message list", re.I),
    "validator_stuck":   re.compile(r"ValidatorStuckException", re.I),
    "iteration_cap":     re.compile(r"iteration cap", re.I),
    "non_interactive":   re.compile(r"Failed to read input in non-interactive mode", re.I),
    # rendering / UI
    "style_error":       re.compile(r"Could not find color or style", re.I),
    # filesystem
    "path_not_found":    re.compile(r"DirectoryNotFoundException|Could not find a part of the path", re.I),
    "file_not_found":    re.compile(r"FileNotFoundException|Could not find file", re.I),
    # native / platform
    "native_lib_missing": re.compile(r"DllNotFoundException|Unable to load shared library", re.I),
    "sqlite_init":        re.compile(r"SqliteConnection|e_sqlite3", re.I),
}

# ── session loader ─────────────────────────────────────────────────────────────

def load_sessions(n: int | None = None) -> list[dict]:
    files = sorted(SESSIONS_DIR.glob("repl-*.json"), key=lambda p: p.stat().st_mtime, reverse=True)
    if n:
        files = files[:n]
    sessions = []
    for f in files:
        try:
            data = json.loads(f.read_text())
            data["_file"] = f.name
            sessions.append(data)
        except Exception as e:
            print(f"  [warn] could not read {f.name}: {e}", file=sys.stderr)
    return sessions


def analyze_session(s: dict) -> dict:
    sid = s.get("SessionId", "?")
    model = s.get("ModelId", "?")
    cwd = s.get("Cwd", "?")
    started = parse_dt(s.get("StartedAt"))
    updated = parse_dt(s.get("LastUpdatedAt"))
    duration = (updated - started).total_seconds() if started and updated else None
    history = s.get("History", [])

    turn_count = 0
    tool_calls: list[str] = []
    issues: list[str] = []
    spurious_inject_count = 0
    user_turns = 0
    assistant_turns = 0

    for msg in history:
        role = msg.get("Role", "")
        contents = msg.get("Contents", [])

        if role == "user":
            user_turns += 1
        elif role == "assistant":
            assistant_turns += 1
            turn_count += 1

        for content in contents:
            ctype = content.get("Type", "")
            text = content.get("Text", "")

            if ctype == "tool_use":
                tool_calls.append(content.get("Name", content.get("name", "unknown")))

            if ctype == "text" and text:
                # spurious write inject detection
                if SPURIOUS_WRITE_INJECT.search(text):
                    spurious_inject_count += 1

                # tool failure patterns in assistant/tool_result text
                for pattern, label in TOOL_FAIL_PATTERNS:
                    if pattern.search(text):
                        issues.append(label)

    tool_freq = Counter(tool_calls)

    return {
        "sid": sid,
        "file": s["_file"],
        "model": model,
        "cwd": cwd,
        "started": started,
        "duration_s": duration,
        "turns": turn_count,
        "user_turns": user_turns,
        "assistant_turns": assistant_turns,
        "tool_calls": len(tool_calls),
        "top_tools": tool_freq.most_common(5),
        "issues": issues,
        "issue_counts": Counter(issues),
        "spurious_inject_count": spurious_inject_count,
    }


# ── event log loader ───────────────────────────────────────────────────────────

def load_event_log(path: Path) -> list[dict]:
    events = []
    if not path.exists():
        return events
    for line in path.read_text().splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            events.append(json.loads(line))
        except Exception:
            pass
    return events


def analyze_event_log(events: list[dict]) -> dict:
    sessions: dict[str, dict] = {}
    for e in events:
        sid = e.get("session", "?")
        etype = e.get("event_type", "")
        ts = e.get("ts", "")
        payload = e.get("payload", {})

        if sid not in sessions:
            sessions[sid] = {
                "sid": sid,
                "tool_calls": [],
                "user_inputs": 0,
                "assistant_responses": 0,
                "model": None,
                "started": None,
                "ended": None,
                "turns": 0,
            }

        rec = sessions[sid]
        if etype == "session_start":
            rec["model"] = payload.get("model")
            rec["started"] = parse_dt(ts)
            rec["tool_count"] = payload.get("tool_count")
        elif etype == "session_end":
            rec["ended"] = parse_dt(ts)
            rec["turns"] = payload.get("turns", 0)
        elif etype == "tool_call":
            rec["tool_calls"].append(payload.get("tool_name", "?"))
        elif etype == "user_input":
            rec["user_inputs"] += 1
        elif etype == "assistant_response":
            rec["assistant_responses"] += 1

    for rec in sessions.values():
        if rec["started"] and rec["ended"]:
            rec["duration_s"] = (rec["ended"] - rec["started"]).total_seconds()
        else:
            rec["duration_s"] = None
        rec["tool_freq"] = Counter(rec["tool_calls"])

    return sessions


# ── crashdump loader ───────────────────────────────────────────────────────────

def load_crashdumps(n: int | None = None) -> list[dict]:
    files = sorted(CRASHDUMP_DIR.glob("*.json"), key=lambda p: p.stat().st_mtime, reverse=True)
    if n:
        files = files[:n]
    dumps = []
    for f in files:
        try:
            data = json.loads(f.read_text())
            data["_file"] = f.name
            dumps.append(data)
        except Exception as e:
            print(f"  [warn] could not read {f.name}: {e}", file=sys.stderr)
    return dumps


def classify_crash(dump: dict) -> list[str]:
    exc = dump.get("exception", {})
    full_text = json.dumps(exc)
    tags = []
    for tag, pat in CRASH_SIGNATURES.items():
        if pat.search(full_text):
            tags.append(tag)
    return tags or ["unknown"]


def analyze_crashdump(dump: dict) -> dict:
    exc = dump.get("exception", {})
    chain = flatten_exception(exc)
    root = chain[-1] if chain else {}
    tags = classify_crash(dump)
    ts = parse_dt(dump.get("timestamp", ""))
    return {
        "file": dump["_file"],
        "timestamp": ts,
        "app_version": dump.get("app_version", "?"),
        "exception_type": exc.get("type", "?"),
        "message": truncate(exc.get("message", ""), 200),
        "root_cause_type": root.get("type", "?"),
        "root_cause_message": truncate(root.get("message", ""), 160),
        "tags": tags,
    }


# ── report ────────────────────────────────────────────────────────────────────

def section(title: str) -> None:
    print(f"\n{'─' * 70}")
    print(f"  {title}")
    print(f"{'─' * 70}")


def print_session_report(analyses: list[dict]) -> None:
    section(f"REPL SESSIONS  (most recent {len(analyses)})")
    all_issues: Counter = Counter()
    total_spurious = 0

    for a in analyses:
        started_str = a["started"].strftime("%Y-%m-%d %H:%M") if a["started"] else "?"
        dur_str = fmt_duration(a["duration_s"]) if a["duration_s"] is not None else "?"
        print(f"\n  [{started_str}]  {a['sid']}  |  {a['model']}")
        print(f"    cwd:      {a['cwd']}")
        print(f"    duration: {dur_str}  |  turns: {a['turns']}  |  tool calls: {a['tool_calls']}")
        if a["top_tools"]:
            tools_str = ", ".join(f"{t}×{c}" for t, c in a["top_tools"])
            print(f"    top tools: {tools_str}")
        if a["spurious_inject_count"]:
            total_spurious += a["spurious_inject_count"]
            print(f"    ⚠ spurious write-tool injections: {a['spurious_inject_count']}")
        if a["issue_counts"]:
            for issue, count in a["issue_counts"].most_common():
                print(f"    ✗ {issue}  ×{count}")
                all_issues[issue] += count

    section("AGGREGATE ISSUE SUMMARY (sessions)")
    if all_issues:
        for issue, count in all_issues.most_common():
            print(f"  {count:4d}×  {issue}")
    else:
        print("  No tool-failure patterns detected in session text.")
    if total_spurious:
        print(f"  {total_spurious:4d}×  spurious write-tool injections (cross-session total)")


def print_event_log_report(sessions_by_id: dict) -> None:
    section(f"EVENT LOG  ({len(sessions_by_id)} sessions)")
    global_tool_freq: Counter = Counter()
    for rec in sessions_by_id.values():
        global_tool_freq.update(rec["tool_freq"])

    for rec in sorted(sessions_by_id.values(), key=lambda r: r["started"] or datetime.min.replace(tzinfo=timezone.utc), reverse=True):
        started_str = rec["started"].strftime("%Y-%m-%d %H:%M") if rec["started"] else "?"
        dur_str = fmt_duration(rec["duration_s"]) if rec["duration_s"] is not None else "?"
        top = ", ".join(f"{t}×{c}" for t, c in Counter(rec["tool_calls"]).most_common(3))
        print(f"  [{started_str}]  {rec['sid'][:12]}  {rec['model'] or '?'}"
              f"  turns={rec['turns']}  dur={dur_str}  top=[{top}]")

    if global_tool_freq:
        print(f"\n  Global top-10 tools across all logged sessions:")
        for tool, count in global_tool_freq.most_common(10):
            print(f"    {count:5d}×  {tool}")


def print_crash_report(analyses: list[dict]) -> None:
    section(f"CRASH DUMPS  ({len(analyses)} total)")
    tag_totals: Counter = Counter()

    for a in sorted(analyses, key=lambda x: x["timestamp"] or datetime.min.replace(tzinfo=timezone.utc), reverse=True):
        ts_str = a["timestamp"].strftime("%Y-%m-%d %H:%M") if a["timestamp"] else "?"
        print(f"\n  [{ts_str}]  {a['file']}  v{a['app_version']}")
        print(f"    exception:  {a['exception_type']}")
        print(f"    message:    {a['message']}")
        if a["root_cause_type"] != a["exception_type"]:
            print(f"    root cause: {a['root_cause_type']}")
            print(f"                {a['root_cause_message']}")
        print(f"    tags:       {', '.join(a['tags'])}")
        tag_totals.update(a["tags"])

    section("CRASH CATEGORY TOTALS")
    for tag, count in tag_totals.most_common():
        print(f"  {count:3d}×  {tag}")


def print_key_findings(session_analyses: list[dict], crash_analyses: list[dict]) -> None:
    section("KEY FINDINGS")

    findings = []

    # Crash patterns
    tag_totals: Counter = Counter()
    for a in crash_analyses:
        tag_totals.update(a["tags"])

    if tag_totals.get("network_timeout", 0) + tag_totals.get("aggregate_retry", 0) > 0:
        n = tag_totals.get("network_timeout", 0) + tag_totals.get("aggregate_retry", 0)
        findings.append(f"Network timeouts caused {n} crash(es): provider calls hitting the 5-min "
                        "ClientPipelineOptions.NetworkTimeout. Consider increasing NetworkTimeout "
                        "or adding streaming with a keep-alive ping.")

    if tag_totals.get("http_5xx", 0) > 0:
        findings.append(f"HTTP 5xx errors ({tag_totals['http_5xx']}×): upstream provider returned "
                        "5xx (seen: 520). These are transient provider-side failures.")

    if tag_totals.get("compaction_error", 0) > 0:
        findings.append(f"Compaction errors ({tag_totals['compaction_error']}×): "
                        "'Cannot compact a message list with fewer than 2 messages' — "
                        "compaction is being triggered on sessions with only a system prompt.")

    # Session tool failures
    all_issues: Counter = Counter()
    for a in session_analyses:
        all_issues.update(a["issue_counts"])

    if all_issues.get("patch_file: oldText not found", 0) > 0:
        n = all_issues["patch_file: oldText not found"]
        findings.append(f"patch_file mismatches ({n}×): agents attempt edits before re-reading "
                        "current file content, causing oldText to be stale. Consider adding "
                        "a pre-edit read gate or a file-hash check before patching.")

    total_spurious = sum(a["spurious_inject_count"] for a in session_analyses)
    if total_spurious > 0:
        findings.append(f"Spurious write-tool injection ({total_spurious}×): the runtime is "
                        "injecting 'You described changes above but did not call any write tool' "
                        "into conversations where no change was described. The injection heuristic "
                        "is over-triggering.")

    if all_issues.get("read/write: file not found", 0) > 0:
        n = all_issues["read/write: file not found"]
        findings.append(f"File-not-found errors ({n}×): agents reference paths that don't exist "
                        "or were moved. Often follows a failed write in a previous turn.")

    if not findings:
        findings.append("No significant runtime issues detected in the analyzed sessions.")

    for i, f in enumerate(findings, 1):
        lines = [f"  {i}. {f[:100]}"]
        rest = f[100:]
        while rest:
            lines.append(f"     {rest[:97]}")
            rest = rest[97:]
        print("\n".join(lines))


# ── brewer token-usage analysis ───────────────────────────────────────────────

def _load_session_dir(sid_dir: Path, slug: str | None, sessions: list) -> None:
    snap_file = sid_dir / "ctx_snapshots.jsonl"
    evt_file  = sid_dir / "events.jsonl"
    if not snap_file.exists():
        return
    try:
        snaps  = [json.loads(l) for l in snap_file.read_text().splitlines() if l.strip()]
        events = [json.loads(l) for l in evt_file.read_text().splitlines() if l.strip()] if evt_file.exists() else []
        sessions.append({"sid": sid_dir.name, "project": slug, "snaps": snaps, "events": events})
    except Exception as e:
        print(f"  [warn] {sid_dir.name}: {e}", file=sys.stderr)


def load_token_sessions(base: Path, project: str | None = None) -> list[dict]:
    """Load ctx_snapshots.jsonl + events.jsonl for all sessions under base.

    Handles both the new project-scoped layout (base/project_slug/session_id/)
    and the legacy flat layout (base/session_id/) for backward compatibility.
    """
    sessions = []
    if not base.exists():
        return sessions
    for entry in sorted(base.iterdir()):
        if not entry.is_dir():
            continue
        # Detect layout by checking whether ctx_snapshots.jsonl is directly inside.
        if (entry / "ctx_snapshots.jsonl").exists():
            # Legacy flat layout: entry IS the session directory.
            _load_session_dir(entry, slug=None, sessions=sessions)
        else:
            # New layout: entry is a project-slug directory.
            slug = entry.name
            if project and slug != project:
                continue
            for sid_dir in sorted(entry.iterdir()):
                if sid_dir.is_dir():
                    _load_session_dir(sid_dir, slug=slug, sessions=sessions)
    return sessions


def _agent_group(snaps: list[dict]) -> dict[str, list[dict]]:
    groups: dict[str, list[dict]] = defaultdict(list)
    for s in snaps:
        agent = s.get("agent") or "system"
        groups[agent].append(s)
    return groups


def analyze_token_session(sess: dict) -> dict:
    sid    = sess["sid"]
    snaps  = sess["snaps"]
    events = sess["events"]

    # ── per-agent snapshot stats ──────────────────────────────────────────────
    by_agent = _agent_group(snaps)
    agent_stats: dict[str, dict] = {}
    for agent, ss in by_agent.items():
        if agent == "system":
            continue
        tokens = [s.get("turn_input_tokens", 0) for s in ss]
        agent_stats[agent] = {
            "turns":      len(ss),
            "max_input":  max(tokens, default=0),
            "min_input":  min(tokens, default=0),
            "total_input": sum(tokens),
            "tokens":     tokens,
        }

    # ── context_assembly events: estimate vs actual ───────────────────────────
    # build map (agent, turn) -> assembly payload
    assemblies: dict[tuple, dict] = {}
    for e in events:
        if e.get("event_type") == "context_assembly":
            assemblies[(e.get("agent"), e.get("turn"))] = e.get("payload", {})

    # match snapshots to assembly estimates
    efficiency_rows: list[dict] = []
    for s in snaps:
        agent = s.get("agent") or "system"
        turn  = s.get("turn")
        actual = s.get("turn_input_tokens", 0)
        if not actual:
            continue
        asm = assemblies.get((agent, turn), {})
        ctx_chars    = asm.get("context_chars", 0)
        schema_est   = asm.get("tool_schema_est_tokens", 0)
        breakdown    = asm.get("context_chars_breakdown", {})
        history_chars = breakdown.get("history", 0)
        estimated    = ctx_chars // 4 + schema_est
        unaccounted  = actual - estimated if estimated else actual
        ratio        = actual / estimated if estimated > 0 else None
        efficiency_rows.append({
            "agent":       agent,
            "turn":        turn,
            "actual":      actual,
            "ctx_chars":   ctx_chars,
            "history_chars": history_chars,
            "schema_est":  schema_est,
            "estimated":   estimated,
            "unaccounted": unaccounted,
            "ratio":       ratio,
        })

    # ── compaction effectiveness ───────────────────────────────────────────────
    compaction_events = [e for e in events if e.get("event_type") == "compaction"]
    cutover_events    = [e for e in events if e.get("event_type") == "context_budget_cutover"]

    # group cutovers by agent
    cutover_by_agent: dict[str, list[int]] = defaultdict(list)
    for e in cutover_events:
        p = e.get("payload", {})
        tokens = p.get("input_tokens", p.get("cumulative_input_tokens", 0))
        agent  = e.get("agent") or "?"
        reason = p.get("reason", "")
        cutover_by_agent[agent].append(tokens)

    # detect compaction-ineffective: tokens grow after compaction
    compaction_failures = []
    dev_snaps = [s for s in snaps if s.get("agent") == "Developer"]
    for i in range(1, len(dev_snaps)):
        prev_tokens = dev_snaps[i-1].get("turn_input_tokens", 0)
        curr_tokens = dev_snaps[i].get("turn_input_tokens", 0)
        if curr_tokens > prev_tokens * 1.2 and curr_tokens > 100_000:
            compaction_failures.append({
                "prev_turn": dev_snaps[i-1].get("turn"),
                "prev_tokens": prev_tokens,
                "curr_turn": dev_snaps[i].get("turn"),
                "curr_tokens": curr_tokens,
                "growth_pct": int((curr_tokens / prev_tokens - 1) * 100),
            })

    # ── cross-agent history leakage ───────────────────────────────────────────
    # Compare Developer's first-turn actual tokens vs context_assembly estimate
    dev_first = next((r for r in efficiency_rows if r["agent"] == "Developer"), None)
    history_leak_tokens = dev_first["unaccounted"] if dev_first else 0

    # ── tool call frequency ───────────────────────────────────────────────────
    dev_tool_freq: Counter = Counter()
    for e in events:
        if e.get("event_type") == "tool_call" and e.get("agent") == "Developer":
            tool = e.get("payload", {}).get("tool", "?")
            dev_tool_freq[tool] += 1

    # ── session summary ───────────────────────────────────────────────────────
    summary_events = [e for e in events if e.get("event_type") == "session_summary"]
    summary = summary_events[-1].get("payload", {}) if summary_events else {}

    return {
        "sid":                 sid,
        "project":             sess.get("project"),
        "agent_stats":         agent_stats,
        "efficiency_rows":     efficiency_rows,
        "compaction_count":    len(compaction_events),
        "cutover_count":       len(cutover_events),
        "cutover_by_agent":    dict(cutover_by_agent),
        "compaction_failures": compaction_failures,
        "history_leak_tokens": history_leak_tokens,
        "dev_tool_freq":       dev_tool_freq,
        "summary":             summary,
    }


def print_token_report(analyses: list[dict]) -> None:
    section(f"TOKEN-USAGE ANALYSIS  ({len(analyses)} sessions)")

    overall_leaks:    list[int]  = []
    all_cutover_agents: Counter  = Counter()
    all_comp_failures: list[dict] = []

    for a in analyses:
        sid   = a["sid"]
        stats = a["agent_stats"]
        summ  = a["summary"]

        max_turn_tok  = summ.get("max_turn_input_tokens") or max(
            (v["max_input"] for v in stats.values()), default=0)
        total_tok     = summ.get("total_input_tokens")  or sum(
            v["total_input"] for v in stats.values())
        avg_turn_tok  = summ.get("avg_turn_input_tokens") or (
            total_tok // sum(v["turns"] for v in stats.values()) if stats else 0)

        project_label = f"  [{a.get('project') or '?'}]" if a.get("project") else ""
        print(f"\n  ── {sid}{project_label} ──")
        print(f"    total_input={total_tok:>10,}  max_turn={max_turn_tok:>8,}  avg_turn={avg_turn_tok:>7,}")
        print(f"    compactions={a['compaction_count']}  cutovers={a['cutover_count']}")

        # per-agent summary
        for agent, st in sorted(stats.items()):
            toks_str = "  ".join(f"{t:,}" for t in st["tokens"])
            over = " ***" if st["max_input"] > 200_000 else (" **" if st["max_input"] > 100_000 else ("  *" if st["max_input"] > 60_000 else ""))
            print(f"    {agent:<15} turns={st['turns']}  max={st['max_input']:>8,}{over}  seq=[{toks_str}]")

        # cross-agent history leakage
        if a["history_leak_tokens"] > 20_000:
            overall_leaks.append(a["history_leak_tokens"])
            print(f"    !! history_leak: ~{a['history_leak_tokens']:,} tokens unaccounted in Developer turn-1")

        # compaction failures (tokens grew after compaction)
        for cf in a["compaction_failures"]:
            all_comp_failures.append(cf)
            print(f"    !! compaction_ineffective: Developer turn {cf['prev_turn']} "
                  f"({cf['prev_tokens']:,}) → turn {cf['curr_turn']} "
                  f"({cf['curr_tokens']:,}, +{cf['growth_pct']}%)")

        # cutovers per agent
        for agent, tok_list in sorted(a["cutover_by_agent"].items()):
            all_cutover_agents[agent] += len(tok_list)
            worst = max(tok_list)
            print(f"    !! cutover: {agent} ×{len(tok_list)}, worst={worst:,}")

        # top developer tools
        if a["dev_tool_freq"]:
            top = a["dev_tool_freq"].most_common(5)
            top_str = "  ".join(f"{t}×{c}" for t, c in top)
            print(f"    dev_tools: {top_str}")

        # efficiency: rows where ratio > 5
        high_ratio = [r for r in a["efficiency_rows"] if r.get("ratio") and r["ratio"] > 5]
        for r in high_ratio:
            print(f"    !! efficiency {r['agent']} turn={r['turn']}: "
                  f"actual={r['actual']:,}  est={r['estimated']:,}  ratio={r['ratio']:.1f}x  "
                  f"unaccounted={r['unaccounted']:,}")

    # aggregate
    section("TOKEN-USAGE AGGREGATE")
    print(f"  Sessions analyzed:       {len(analyses)}")
    if overall_leaks:
        print(f"  History-leak incidents:  {len(overall_leaks)}  "
              f"(avg {sum(overall_leaks)//len(overall_leaks):,} unaccounted tokens each)")
    if all_comp_failures:
        print(f"  Compaction failures:     {len(all_comp_failures)}  "
              f"(tokens grew ≥20% after compaction)")
    if all_cutover_agents:
        print(f"  Cutover hits by agent:")
        for agent, count in all_cutover_agents.most_common():
            print(f"    {count:3d}×  {agent}")


# ── main ───────────────────────────────────────────────────────────────────────

def main() -> None:
    import argparse

    parser = argparse.ArgumentParser(description="Analyze fuseraft sessions for runtime issues.")
    parser.add_argument("-n", "--sessions", type=int, default=10,
                        help="Number of most recent sessions to analyze (default: 10)")
    parser.add_argument("--crashes", type=int, default=None,
                        help="Limit crash dumps analyzed (default: all)")
    parser.add_argument("--no-events", action="store_true",
                        help="Skip event log analysis")
    parser.add_argument("--dir", type=Path, default=None,
                        help="Sessions directory to scan "
                             "(default: ~/.fuseraft/logs/sessions)")
    parser.add_argument("--project", type=str, default=None,
                        help="Filter by project slug, e.g. home-scs-github-fuseraft-brewer")
    parser.add_argument("--no-token-analysis", action="store_true",
                        help="Skip token-usage analysis")
    args = parser.parse_args()

    print(f"fuseraft session analyzer  —  {datetime.now().strftime('%Y-%m-%d %H:%M')}")
    print(f"Sessions dir:  {SESSIONS_DIR}")
    print(f"Crashdump dir: {CRASHDUMP_DIR}")

    # Sessions
    sessions = load_sessions(args.sessions)
    session_analyses = [analyze_session(s) for s in sessions]
    print_session_report(session_analyses)

    # Event log
    if not args.no_events and GLOBAL_EVENT_LOG.exists():
        events = load_event_log(GLOBAL_EVENT_LOG)
        event_sessions = analyze_event_log(events)
        print_event_log_report(event_sessions)

    # Crash dumps
    crashes = load_crashdumps(args.crashes)
    crash_analyses = [analyze_crashdump(d) for d in crashes]
    print_crash_report(crash_analyses)

    # Key findings
    print_key_findings(session_analyses, crash_analyses)

    # Token-usage analysis
    if not args.no_token_analysis:
        token_sessions_dir = args.dir or GLOBAL_TOKEN_SESSIONS_DIR
        token_sessions = load_token_sessions(token_sessions_dir, project=args.project)
        if token_sessions:
            token_analyses = [analyze_token_session(s) for s in token_sessions]
            print_token_report(token_analyses)
        else:
            print(f"\n  (no session logs found under {token_sessions_dir})")

    print(f"\n{'─' * 70}\n")


if __name__ == "__main__":
    main()
