#!/usr/bin/env python3
"""Parse and summarize a fuseraft snapshot turns.jsonl file."""

import json
import sys
from pathlib import Path
from datetime import datetime

RESET = "\033[0m"
BOLD = "\033[1m"
DIM = "\033[2m"
CYAN = "\033[36m"
GREEN = "\033[32m"
RED = "\033[31m"
YELLOW = "\033[33m"
MAGENTA = "\033[35m"
BLUE = "\033[34m"


def fmt_ts(ts: str) -> str:
    try:
        dt = datetime.fromisoformat(ts.replace("Z", "+00:00"))
        return dt.strftime("%H:%M:%S")
    except Exception:
        return ts


def color_agent(agent: str) -> str:
    palette = {
        "Planner": CYAN,
        "PlannerCritic": MAGENTA,
        "Coder": GREEN,
        "CoderCritic": YELLOW,
        "Orchestrator": BLUE,
    }
    for key, col in palette.items():
        if key.lower() in agent.lower():
            return col + agent + RESET
    return BOLD + agent + RESET


def render_turn(turn: dict, verbose: bool) -> None:
    n = turn.get("turn", "?")
    agent = turn.get("agent", "Unknown")
    ts = fmt_ts(turn.get("ts", ""))
    content = (turn.get("content") or "").strip()
    tool_calls = turn.get("tool_calls", [])
    in_tok = turn.get("input_tokens", 0)
    out_tok = turn.get("output_tokens", 0)

    header = f"{DIM}[{ts} turn={n:>2}]{RESET}  {color_agent(agent)}"
    header += f"  {DIM}in={in_tok:,} out={out_tok:,}{RESET}"
    print(header)

    if content:
        for line in content.splitlines()[:5]:
            print(f"    {line}")
        if len(content.splitlines()) > 5:
            print(f"    {DIM}… ({len(content.splitlines())} lines){RESET}")

    for tc in tool_calls:
        ok = tc.get("succeeded", True)
        icon = GREEN + "✓" + RESET if ok else RED + "✗" + RESET
        name = BOLD + tc.get("name", "") + RESET
        summary = tc.get("args_summary", "")
        if summary and verbose:
            print(f"    {icon} {name}  {DIM}{summary}{RESET}")
        else:
            print(f"    {icon} {name}")

    print()


def summarize(turns: list[dict]) -> None:
    total_in = sum(t.get("input_tokens", 0) for t in turns)
    total_out = sum(t.get("output_tokens", 0) for t in turns)
    agents = {}
    tool_counts: dict[str, int] = {}
    fail_counts: dict[str, int] = {}

    for t in turns:
        a = t.get("agent", "Unknown")
        agents[a] = agents.get(a, 0) + 1
        for tc in t.get("tool_calls", []):
            name = tc.get("name", "?")
            tool_counts[name] = tool_counts.get(name, 0) + 1
            if not tc.get("succeeded", True):
                fail_counts[name] = fail_counts.get(name, 0) + 1

    print(f"{BOLD}=== Summary ==={RESET}")
    print(f"  Turns         : {len(turns)}")
    print(f"  Total tokens  : in={total_in:,}  out={total_out:,}  total={total_in+total_out:,}")
    print(f"\n  {BOLD}Agents:{RESET}")
    for a, cnt in sorted(agents.items(), key=lambda x: -x[1]):
        print(f"    {color_agent(a):40s}  {cnt} turn(s)")
    print(f"\n  {BOLD}Top tools:{RESET}")
    for name, cnt in sorted(tool_counts.items(), key=lambda x: -x[1])[:15]:
        fails = fail_counts.get(name, 0)
        fail_str = f"  {RED}{fails} failed{RESET}" if fails else ""
        print(f"    {BOLD}{name}{RESET:30s}  {cnt:>3}x{fail_str}")


def main() -> None:
    default = Path.home() / ".fuseraft/snapshots/home-scs-github-fuseraft-sandbox/ef0aa7b7/turns.jsonl"
    path = Path(sys.argv[1]) if len(sys.argv) > 1 else default
    verbose = "--verbose" in sys.argv or "-v" in sys.argv
    only_summary = "--summary" in sys.argv or "-s" in sys.argv

    if not path.exists():
        print(f"{RED}File not found:{RESET} {path}", file=sys.stderr)
        sys.exit(1)

    turns = []
    with path.open() as f:
        for line in f:
            line = line.strip()
            if line:
                turns.append(json.loads(line))

    if not only_summary:
        print(f"{BOLD}Snapshot:{RESET} {path}")
        print(f"{BOLD}Session :{RESET} {turns[0].get('session', '?') if turns else '?'}\n")
        for t in turns:
            render_turn(t, verbose)

    summarize(turns)


if __name__ == "__main__":
    main()
