#!/usr/bin/env bash
# Runs the ETL pipeline orchestration (config/examples/etl-pipeline.yaml) for one
# input/output pair and reports success/failure via exit code.
#
# Meant to be invoked by an external event — a cron tick, a file-watcher, a webhook
# receiver piping in a payload, systemd — rather than run by hand. See
# scripts/run_pipeline.py for the Python equivalent (e.g. for a webhook handler
# that wants the parsed summary as a dict instead of shelling out).
#
# Usage:
#   scripts/run-pipeline.sh <input-path> <output-path> [work-dir]
#
# Requires: fuseraft on PATH (or FUSERAFT_BIN set), jq, and the provider API key
# configured in config/examples/etl-pipeline.yaml (ANTHROPIC_API_KEY by default).
#
# Exit codes (propagated from `fuseraft run --ci`):
#   0  pipeline completed and all acceptance criteria passed
#   1  session failed to complete (agent error, budget exceeded, aborted, ...)
#   2  session completed but --ci found a FAILing acceptance criterion
set -uo pipefail

FUSERAFT_BIN="${FUSERAFT_BIN:-fuseraft}"
CONFIG="${FUSERAFT_PIPELINE_CONFIG:-$(dirname "$0")/../config/examples/etl-pipeline.yaml}"

INPUT_PATH="${1:?usage: $0 <input-path> <output-path> [work-dir]}"
OUTPUT_PATH="${2:?usage: $0 <input-path> <output-path> [work-dir]}"
WORK_DIR="${3:-$(pwd)}"

TASK_FILE="$(mktemp)"
trap 'rm -f "$TASK_FILE"' EXIT

cat > "$TASK_FILE" <<EOF
Read the input from ${INPUT_PATH}, normalize it, and write the result to
${OUTPUT_PATH}. Both paths are relative to the working directory.
EOF

# --json means stdout is exactly one JSON summary line; every human-readable status
# line (including any setup error before the session starts) goes to stderr instead.
SUMMARY_JSON="$("$FUSERAFT_BIN" run \
  --config "$CONFIG" \
  --task-file "$TASK_FILE" \
  --work-dir "$WORK_DIR" \
  --json --ci --no-banner)"
EXIT_CODE=$?

if command -v jq >/dev/null 2>&1 && [[ -n "$SUMMARY_JSON" ]]; then
  echo "$SUMMARY_JSON" | jq . >&2

  SUCCEEDED="$(echo "$SUMMARY_JSON" | jq -r '.succeeded')"
  CI_PASSED="$(echo "$SUMMARY_JSON" | jq -r '.ci.passed // empty')"

  if [[ "$SUCCEEDED" != "true" ]]; then
    echo "Pipeline session failed: $(echo "$SUMMARY_JSON" | jq -r '.error_message // "unknown error"')" >&2
  elif [[ "$CI_PASSED" == "false" ]]; then
    echo "Pipeline completed but failed acceptance criteria: $(echo "$SUMMARY_JSON" | jq -c '.ci.failed_criteria')" >&2
  fi
fi

exit "$EXIT_CODE"
