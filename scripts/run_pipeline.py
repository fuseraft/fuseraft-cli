#!/usr/bin/env python3
"""Run the ETL pipeline orchestration (config/examples/etl-pipeline.yaml) for one
input/output pair and report success/failure via exit code and a parsed summary dict.

Meant to be called from an event handler — a webhook receiver, a queue consumer, a
file-watcher callback — each time a new event arrives, rather than run by hand. See
scripts/run-pipeline.sh for the bash equivalent.

Usage:
    python3 scripts/run_pipeline.py <input-path> <output-path> [--work-dir DIR]

Requires: fuseraft on PATH (or FUSERAFT_BIN set), and the provider API key
configured in config/examples/etl-pipeline.yaml (ANTHROPIC_API_KEY by default).

Exit codes (propagated from `fuseraft run --ci`):
    0  pipeline completed and all acceptance criteria passed
    1  session failed to complete (agent error, budget exceeded, aborted, ...)
    2  session completed but --ci found a FAILing acceptance criterion

Example — wiring this into a webhook handler instead of running as a script:

    from run_pipeline import run_pipeline

    def on_file_uploaded(event):
        result = run_pipeline(event["path"], f"output/{event['id']}.json")
        if result["succeeded"] and result.get("ci", {}).get("passed", True):
            notify_downstream(result)
        else:
            alert_oncall(result)
"""
import argparse
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

FUSERAFT_BIN = os.environ.get("FUSERAFT_BIN", "fuseraft")
CONFIG = Path(os.environ.get(
    "FUSERAFT_PIPELINE_CONFIG",
    Path(__file__).parent.parent / "config" / "examples" / "etl-pipeline.yaml",
))


def run_pipeline(input_path: str, output_path: str, work_dir: str = ".") -> dict:
    """Invokes `fuseraft run --json --ci` for one input/output pair and returns the
    parsed summary dict, with `exit_code` added.

    A failed *session* (bad output, agent error, --ci FAIL) is reported through the
    returned dict, not an exception — that is an expected outcome callers need to
    branch on, not a bug in this wrapper. This only raises if fuseraft itself could
    not be started (e.g. not on PATH).
    """
    task = (
        f"Read the input from {input_path}, normalize it, and write the result "
        f"to {output_path}. Both paths are relative to the working directory."
    )

    with tempfile.NamedTemporaryFile("w", suffix=".md", delete=False) as f:
        f.write(task)
        task_file = f.name

    try:
        proc = subprocess.run(
            [
                FUSERAFT_BIN, "run",
                "--config", str(CONFIG),
                "--task-file", task_file,
                "--work-dir", work_dir,
                "--json", "--ci", "--no-banner",
            ],
            capture_output=True,
            text=True,
        )
    finally:
        Path(task_file).unlink(missing_ok=True)

    # --json means human-readable status (including setup errors before the
    # session starts) always lands on stderr, never mixed into stdout.
    if proc.stderr:
        print(proc.stderr, file=sys.stderr, end="")

    try:
        summary = json.loads(proc.stdout)
    except json.JSONDecodeError:
        # No JSON summary means the run never reached a completed session (a setup
        # error printed to stderr above instead) — exit code is still authoritative.
        summary = {
            "succeeded": False,
            "error_message": "no JSON summary on stdout — see stderr",
        }

    summary["exit_code"] = proc.returncode
    return summary


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("input_path")
    parser.add_argument("output_path")
    parser.add_argument("--work-dir", default=".")
    args = parser.parse_args()

    result = run_pipeline(args.input_path, args.output_path, args.work_dir)
    print(json.dumps(result, indent=2))
    return result["exit_code"]


if __name__ == "__main__":
    sys.exit(main())
