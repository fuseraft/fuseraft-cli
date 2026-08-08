# Scripting & Automation

fuseraft orchestrations are not limited to interactive terminal use. `fuseraft run` is a normal CLI command with a task argument, a real exit code, and (with `--json`) a single machine-parseable result on stdout — the same shape as any other tool you'd shell out to from a script. This page covers running fuseraft from bash or Python, wiring it to external events (a webhook, a queue, a cron tick, a file landing in a watched directory), and the exact contract you can rely on when doing so.

---

## The short version

```bash
fuseraft run --config pipeline.yaml --task-file task.md --json --ci --no-banner
```

- `--no-banner` — skip the ASCII banner
- `--json` — stdout becomes exactly one JSON summary line; every human-readable status line goes to stderr instead
- `--ci` — after the session completes, read `.fuseraft/artifacts/test-report.json` and exit `2` if any acceptance criterion is `FAIL`
- Exit code: `0` success, `1` the session failed (or a setup error before it started), `2` the session succeeded but `--ci` found a failing criterion

That's the whole contract most scripts need. The rest of this page fills in the details, then walks through a complete worked example.

---

## Non-interactive flags

These are the `fuseraft run` flags relevant to scripted invocations. Full flag reference: [CLI Reference → `fuseraft run`](cli-reference.md#fuseraft-run).

| Flag | Why it matters for scripts |
|------|------------------------------|
| `-f, --task-file <path>` | Pass a long or multi-line task without shell-quoting gymnastics. Build the task text yourself (e.g. from an event payload) and write it to a temp file. |
| `--json` | Stdout carries only the JSON summary; see [The `--json` contract](#the-json-contract) below. |
| `--ci` | Fails the process (exit `2`) when the orchestration's own acceptance criteria didn't pass — not just when the session crashed. |
| `--no-banner` | Skip the ASCII banner. Redundant with `--json` (which already suppresses it) but harmless to include; useful on its own if you're not using `--json`. |
| `--work-dir <path>` | Pin the session to a specific directory instead of relying on the process's CWD — important when a single long-running handler processes events for multiple projects/directories. |
| `-o, --output <path>` | Save a Markdown transcript alongside the JSON summary, for audit trails. |
| `-r, --resume <sessionId>` | Retry a session that was interrupted (e.g. the handler process was killed mid-run) instead of starting over. |

**Avoid `--hitl`, `--devui`, and an omitted task in scripts.** Each either blocks on terminal input or opens a browser — none make sense in an unattended process. `--json` does not change this; it's your responsibility not to combine them.

---

## The `--json` contract

Enable JSON mode two ways:

- **Per invocation:** pass `--json` on the command line.
- **Per config:** set `Output.Json: true` in the orchestration config, so every run of that config behaves this way without needing the flag. See [Configuration → Output](configuration.md#output). The `--json` flag always takes precedence if both are used.

**Stream contract:** when JSON mode is active, stdout carries *only* the final JSON summary — no banner, no turn panels, no spinner, no per-agent status. Every human-readable line, including startup diagnostics, goes to stderr. This makes stdout safe to pipe straight into `jq` or `json.loads()` without stripping anything first.

**Summary schema and full field reference:** [CLI Reference → `fuseraft run` → `--json` output](cli-reference.md#fuseraft-run). In short: `session_id`, `task`, `config`, `succeeded`, `error_message`, `exit_code`, `turns`, `elapsed_seconds`, `tokens.{input,output}`, `transcript_path`, and `ci.{passed,skipped,failed_criteria}` when `--ci` was used.

### Early failures still produce clean output

A run can fail before a session ever starts — a bad `--work-dir`, a missing `--spec` file, an unresolvable `--resume` ID, or the config file itself failing to load. fuseraft's contract for these:

- **`--json` flag set:** jsonMode is known from the very first line of the command, before anything else runs. Every one of these early failures still emits exactly one JSON summary line to stdout (`succeeded: false`, `error_message` set, other fields zeroed) and all diagnostic text goes to stderr — the same guarantee as a normal completed run.
- **Only `Output.Json: true` in the config (no `--json` flag):** JSON mode can't be confirmed until the config has finished loading — the setting itself lives in the config. If the failure happens *before* that point, fuseraft cannot know whether to emit JSON, so it doesn't: **stdout is left completely empty** (never wrong, never mixed with plain text) and the failure is reported via exit code plus a stderr message only. If the config loads successfully and something fails afterward, JSON mode is fully known and behaves exactly like the `--json` flag case above.

Either way, **stdout never contains anything other than a well-formed JSON summary or nothing at all.** A script's parsing logic should be: try `json.loads(stdout)`; if that fails (empty or non-JSON), treat it as a failure and fall back to the exit code plus whatever was captured on stderr.

```bash
# --json flag: JSON summary even for a setup error, no session ever started
$ fuseraft run -c pipeline.yaml --work-dir /no/such/dir --json --no-banner
{"session_id":null,"task":null,"config":"/abs/path/pipeline.yaml","succeeded":false,"error_message":"Work directory not found: /no/such/dir","exit_code":1,"turns":0,"elapsed_seconds":0,"tokens":{"input":0,"output":0},"transcript_path":null,"ci":null}
$ echo $?
1
```

```bash
# Output.Json: true only, same failure: stdout is empty, not corrupted
$ fuseraft run -c pipeline.yaml --work-dir /no/such/dir --no-banner
$ echo $?
1
```

If you control the invocation (which you almost always do, since you're the one writing the wrapper script), pass `--json` explicitly rather than relying on `Output.Json` alone — it closes this last gap and gives you a JSON line for every outcome, not just successful ones.

---

## Exit codes at a glance

| Command | `0` | `1` | `2` |
|---------|-----|-----|-----|
| `fuseraft run` | Session completed | Session failed, or a setup error before it started | Only with `--ci`: session completed but an acceptance criterion is `FAIL` |
| `fuseraft validate` | Config is valid (warnings may still print) | One or more errors found | — |
| `fuseraft schedule run` | All due jobs ticked without error | A job failed | — |

`fuseraft validate config.yaml --check-connectivity` is worth running as a pre-flight step in CI before the first real `fuseraft run` — it makes a 1-token call to each configured model endpoint and confirms every API key actually works, so a pipeline fails fast on a misconfigured key instead of burning a full session first. See [CLI Reference → `fuseraft validate`](cli-reference.md#fuseraft-validate).

---

## Triggering runs from events

### Cron / systemd timer

For anything on a fixed schedule, `fuseraft schedule` is usually simpler than hand-rolling a cron entry that calls `fuseraft run` directly — it stores the job definition (config path, work dir, output path template) once in `~/.fuseraft/schedule/`, and `fuseraft schedule run` is designed to be ticked every minute by cron or a systemd timer with no daemon required. See [CLI Reference → `fuseraft schedule`](cli-reference.md#fuseraft-schedule) for the full command set.

For an event that should run a *specific* job on demand — not wait for its next scheduled tick — use:

```bash
fuseraft schedule run --name my-job
```

This ignores the job's schedule and `enabled` flag and runs it immediately, while still reusing the config/work-dir/output settings stored in the job definition.

### Webhooks, queues, file-watchers

For anything else — a webhook payload, a message off a queue, a file landing in a watched directory — the pattern is the same regardless of trigger source: your event handler builds a task (usually naming the specific input/output the event refers to) and shells out to `fuseraft run --json --ci`. See the worked example below.

---

## Worked example: an event-driven ETL pipeline

`config/examples/etl-pipeline.yaml` and `scripts/run-pipeline.sh` / `scripts/run_pipeline.py` are a complete, runnable version of this pattern — copy them as a starting point.

### The orchestration config

Two agents, run at most once each — a linear pipeline, not an open-ended chat:

```yaml
Orchestration:
  Name: EtlPipeline

  Output:
    Json: true          # every invocation behaves as if --json was passed

  Selection:
    Type: sequential     # Extractor, then Transformer, in that fixed order

  Termination:
    Type: composite
    Strategies:
      - Type: regex
        Pattern: "PIPELINE_COMPLETE"   # Transformer's completion signal
      - Type: maxiterations
        MaxIterations: 4               # hard stop if something loops

  Validation:
    TestReportPath: .fuseraft/artifacts/test-report.json   # feeds --ci

  Security:
    FileSystemSandboxPath: .
    ChangeEnvelope:
      - "output/**"                     # Transformer may only write here
      - ".fuseraft/artifacts/**"

  Agents:
    - Name: Extractor       # reads + validates input, never writes
    - Name: Transformer      # normalizes, writes output, files the test report
```

Points worth calling out:

- **`Output.Json: true`** means this config *always* reports structured results — nobody has to remember to pass `--json` when invoking it, which matters once several scripts/services call the same config.
- **`Selection.Type: sequential`** with two agents means: Extractor runs turn 1, Transformer runs turn 2, done. There's no keyword routing to configure — sequential just advances through the agent list in order.
- **`Termination`** combines the Transformer's own completion signal (`PIPELINE_COMPLETE`) with a hard `MaxIterations` cap, so a malfunctioning agent can't loop forever in an unattended process.
- **`Security.ChangeEnvelope`** restricts writes to `output/**` and the artifacts directory — since this runs unattended in response to external events, it shouldn't be able to touch anything else in the sandboxed work dir even if an agent misbehaves. See [Security](security.md).
- **`Validation.TestReportPath`** is what makes `--ci` meaningful here: the Transformer is instructed to write a PASS/FAIL acceptance-criteria report before signalling completion, and `--ci` reads it after the session ends.

See the full file for the complete agent instructions. Full config schema: [Configuration](configuration.md).

### The wrapper scripts

Both scripts do the same thing — build a task string naming the input/output paths, invoke `fuseraft run --json --ci`, parse the summary, and exit with fuseraft's own exit code:

```bash
scripts/run-pipeline.sh <input-path> <output-path> [work-dir]
```

```bash
python3 scripts/run_pipeline.py <input-path> <output-path> [--work-dir DIR]
```

The Python version is also importable as a library function, which is the more useful form for a long-running event handler (a webhook server, a queue consumer) that shouldn't fork a fresh interpreter per event:

```python
from run_pipeline import run_pipeline

def on_file_uploaded(event):
    result = run_pipeline(event["path"], f"output/{event['id']}.json")
    if result["succeeded"] and result.get("ci", {}).get("passed", True):
        notify_downstream(result)
    else:
        alert_oncall(result)
```

`run_pipeline()` returns the parsed JSON summary dict with `exit_code` added — a failed *session* (bad output, `--ci` FAIL, agent error) comes back as `result["succeeded"] is False` in the return value, not an exception, since callers need to branch on that as a normal, expected outcome. It only raises if `fuseraft` itself couldn't be started (e.g. not on `PATH`).

Both scripts read `FUSERAFT_BIN` (default: `fuseraft` on `PATH`) and `FUSERAFT_PIPELINE_CONFIG` (default: `config/examples/etl-pipeline.yaml`) from the environment, so you can point them at a different binary or config without editing the script.

### Trying it yourself

```bash
export ANTHROPIC_API_KEY=<your-key>   # or the provider configured in the YAML

echo '[{"id":1,"first_name":"Ada","email":"ADA@EXAMPLE.COM "}]' > input.json

scripts/run-pipeline.sh input.json output/normalized.json .
echo "exit code: $?"
cat output/normalized.json
```

---

## Related

- [CLI Reference → `fuseraft run`](cli-reference.md#fuseraft-run) — full flag list and the `--json` summary field reference
- [CLI Reference → `fuseraft schedule`](cli-reference.md#fuseraft-schedule) — cron-driven sessions
- [CLI Reference → `fuseraft validate`](cli-reference.md#fuseraft-validate) — pre-flight config and API-key checks
- [Configuration → Output](configuration.md#output) — the `Output.Json` config field
- [Examples](examples.md) — more ready-to-use orchestration configs
