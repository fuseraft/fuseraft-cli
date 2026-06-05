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

    print(f"\n{'─' * 70}\n")


if __name__ == "__main__":
    main()
