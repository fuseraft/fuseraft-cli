# Evals

Evals let you run a team of agents against a set of predefined tasks and automatically score the results. Each eval case specifies what the agent should say (or not say), how many turns it may take, and whether the session must succeed — giving you a repeatable regression suite for your agent configs.

## Quick start

Scaffold a suite, then run it:

```bash
fuseraft eval init                  # interactive wizard → .fuseraft/evals/suite.yaml
fuseraft eval run                   # runs suite.yaml against the default team config
```

## Commands

### `fuseraft eval init [output]`

Scaffolds a new eval suite YAML with annotated example cases.

| Flag | Description |
|------|-------------|
| `[output]` | Path to write the suite (default: `.fuseraft/evals/suite.yaml`) |
| `-n, --name <name>` | Suite name embedded in the file |
| `-c, --config <path>` | Default team config path to embed |
| `--no-interactive` | Skip prompts and use supplied options and defaults |

```bash
fuseraft eval init my-evals/suite.yaml --name "Smoke Tests" --config .fuseraft/config/orchestration.yaml
```

### `fuseraft eval run [suite]`

Runs every case in a suite and prints pass/fail per case, then a summary.

| Flag | Description |
|------|-------------|
| `[suite]` | Path to the suite file (default: `.fuseraft/evals/suite.yaml`) |
| `-c, --config <path>` | Override the suite-level team config |
| `-o, --output <path>` | Write per-case results as JSONL to this file |
| `--filter <value>` | Run only cases whose `id` or `tag` contains this substring (case-insensitive) |
| `--timeout <seconds>` | Per-case timeout; `0` = no timeout (default) |
| `--no-banner` | Skip the suite header line |
| `--ci` | Exit with code `1` if any case fails (for CI pipelines) |

```bash
fuseraft eval run                              # run all cases
fuseraft eval run --filter smoke               # run only cases tagged "smoke"
fuseraft eval run --ci --output results.jsonl  # CI mode with JSONL output
```

## Suite file format

Suites are YAML (or JSON) files with a top-level name, a default config path, and a list of cases.

```yaml
name: My Eval Suite
config: .fuseraft/config/orchestration.yaml   # suite-level default; overridable per case

cases:
  - id: smoke-basic
    task: "Say hello and confirm you are ready."
    must_succeed: true
    expect_keywords:
      - hello
    max_turns: 3
    tags:
      - smoke
```

### Top-level fields

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | Human-readable suite name shown in the banner |
| `config` | string | Default team config path used by all cases unless overridden |
| `cases` | list | Ordered list of eval cases |

### Case fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `id` | string | — | Unique identifier used in reports and `--filter` |
| `task` | string | — | Inline task prompt sent to the orchestrator |
| `task_file` | string | — | Path to a file whose contents become the task (mutually exclusive with `task`) |
| `config` | string | suite default | Per-case team config override |
| `must_succeed` | bool | `true` | Fail the case when the session does not complete successfully |
| `expect_keywords` | list\<string\> | `[]` | All strings must appear (case-insensitive) in the final assistant message |
| `expect_regex` | list\<string\> | `[]` | All patterns must match (case-insensitive) against the final assistant message |
| `forbidden_keywords` | list\<string\> | `[]` | None of these strings may appear (case-insensitive) in the final assistant message |
| `max_turns` | int | `0` | Fail if the session exceeds this many agent turns; `0` = unlimited |
| `tags` | list\<string\> | `[]` | Labels used with `--filter` |

## Scoring

Each case is scored after the session finishes. A case **passes** only when all of the following hold:

- If `must_succeed: true`, the session completed without error.
- Every string in `expect_keywords` appears in the final assistant message.
- Every pattern in `expect_regex` matches the final assistant message.
- No string in `forbidden_keywords` appears in the final assistant message.
- If `max_turns` > 0, the session did not exceed that many turns.

Failure reasons are printed per-case and included in the JSONL output.

## Config resolution

The team config used for a case is resolved in this order:

1. `config` field on the case
2. `-c/--config` CLI flag
3. `config` field at the suite level
4. `.fuseraft/config/orchestration.yaml` (hardcoded fallback)

## JSONL output

When `--output <path>` is given, one JSON object per case is written to that file after the suite completes:

```json
{"case_id":"smoke-basic","session_id":"a1b2c3d4","passed":true,"failure_reasons":[],"total_turns":2,"duration_ms":3120,"total_input_tokens":841,"total_output_tokens":53,"error_message":null}
{"case_id":"code-generation","session_id":"e5f6a7b8","passed":false,"failure_reasons":["expected keyword not found: \"def reverse_string\""],"total_turns":5,"duration_ms":9870,"total_input_tokens":2103,"total_output_tokens":198,"error_message":null}
```

| Field | Description |
|-------|-------------|
| `case_id` | The `id` from the suite |
| `session_id` | Short random ID for this run |
| `passed` | `true` if all scoring criteria passed |
| `failure_reasons` | List of human-readable failure descriptions |
| `total_turns` | Number of agent turns used |
| `duration_ms` | Wall-clock time for this case |
| `total_input_tokens` | Sum of input tokens across all turns |
| `total_output_tokens` | Sum of output tokens across all turns |
| `error_message` | Exception message if the orchestrator threw, otherwise `null` |

## CI integration

Pass `--ci` to make `fuseraft eval run` exit with code `1` if any case fails. Combined with `--output`, this gives you a full audit trail:

```yaml
# .github/workflows/eval.yml
- name: Run evals
  run: fuseraft eval run .fuseraft/evals/suite.yaml --ci --output eval-results.jsonl

- name: Upload results
  if: always()
  uses: actions/upload-artifact@v4
  with:
    name: eval-results
    path: eval-results.jsonl
```

## Example suite

The file below is the annotated example generated by `fuseraft eval init`. It covers the four main case patterns:

```yaml
name: Example Eval Suite
config: .fuseraft/config/orchestration.yaml

cases:
  # Smoke test — quick sanity check that the team responds at all.
  - id: smoke-basic
    task: "Say hello and confirm you are ready."
    must_succeed: true
    expect_keywords:
      - hello
    max_turns: 3
    tags:
      - smoke

  # Keyword + regex check — verify code generation output.
  - id: code-generation
    task: "Write a Python function named reverse_string that returns the reverse of its input."
    must_succeed: true
    expect_keywords:
      - def reverse_string
      - return
    expect_regex:
      - "def reverse_string\\("
    max_turns: 5
    tags:
      - coding

  # Forbidden-keyword guard — catch undesirable response patterns.
  - id: no-refusal
    task: "List three benefits of automated testing."
    must_succeed: true
    forbidden_keywords:
      - "I cannot"
      - "I'm unable"
      - "I am unable"
    tags:
      - quality

  # Task from file — useful for long or multi-line prompts.
  - id: file-task
    task_file: .fuseraft/evals/tasks/my-task.txt
    must_succeed: true
    max_turns: 10
    tags:
      - file-task

  # Per-case config override — run against a different team.
  - id: specialist-check
    config: .fuseraft/config/specialist.yaml
    task: "Explain the role of a load balancer in two sentences."
    must_succeed: true
    expect_keywords:
      - load balancer
    tags:
      - routing
```
