---
title: "Gates"
description: "Gates are deterministic checks that block a handoff until real evidence exists on disk. This page covers what each check verifies, the file formats they expect, and what happens when an agent gets stuck."
---

A **gate** blocks a handoff until real evidence exists: a brief on disk, a test report backed by commands that actually ran, every planned file written. Gates read files and the run's recorded actions; they never call a model. The same inputs always give the same answer.

You declare gates on edges with `requires:`. The full list of checks, with their arguments, is in [Configuration → Gates](configuration.md#gates). This page goes deeper on what each one verifies and the formats they expect.

```yaml
flow:
  start: Developer
  steps:
    Developer:
      - to: Tester
        requires:
          - filesWritten: brief.files_to_change
          - commandSucceeded: brief.verify_command
```

When a gate fails, the agent gets a correction message naming exactly what's missing, and runs again. It never sees the next agent's turn until every gate on the edge passes.

---

## Where evidence comes from

Every run records each file write, shell command (with exit code) and git commit in the change log and the evidence graph (see [What every run records](configuration.md#what-every-run-records)). Gates read those records rather than the agent's prose, so "I ran the tests" only counts if a test command actually ran and exited 0.

A file the agent changed without naming it, say through `sed` in `shell_run`, is still recorded if the agent has read or written that file before.

---

## Artifacts and shapes

Naming an artifact in `requires` checks that its file exists and, when the artifact declares a `shape`, that the file parses as JSON and matches it.

```yaml
artifacts:
  review:
    path: .fuseraft/artifacts/review.json
    shape:
      verdict: approved|revision_needed
      feedback: string
```

A mismatch lists each problem, for example:

```
'.fuseraft/artifacts/review.json' doesn't match its declared shape.
  - $.verdict: expected one of approved, revision_needed, found 'ok'.
  - $.feedback: missing.
```

Extra fields are allowed, so a shape states the minimum the next agent relies on.

---

## `filesWritten`

`filesWritten: brief.files_to_change` passes when every path listed in that field of the artifact was written this run, in this turn or an earlier one. Entries can be strings or objects with a `path` field:

```json
"files_to_change": [
  "src/api/users.py",
  { "path": "tests/test_users.py", "reason": "Add pagination tests" }
]
```

Paths match case-insensitively, ignoring a leading `./`. Every listed file must be written, including ones that already exist: listing a file is a commitment to change it. If a file turns out not to need changes, the agent should remove it from the brief and hand off again.

`checklistComplete: brief.execution_checklist` applies the same rule to file-writing steps in a checklist, catching a path that appears only in the checklist and never in `files_to_change`.

---

## `commandSucceeded`

`commandSucceeded: brief.verify_command` reads the command from the artifact and passes when a shell command matching it ran and exited 0 this run. Reading the command from the brief keeps the gate language-agnostic: the Planner writes `dotnet test`, `cargo test` or `pytest`, and the gate checks that one.

`commandSucceeded: "go build|go test"` uses a literal pattern instead: any `|`-separated part may match. `fallback` gives a pattern to use when the field is empty.

A command run in the background with `shell_run_background` doesn't count; the check needs the exit code.

---

## `testReport`

`testReport: testResults` checks the test report artifact:

1. The file exists and has at least one result.
2. With `noFailures` (default `true`): no result has `status: FAIL`.
3. With `hasAssertions` (default `true`): every `PASS` result names a `command` that actually ran and succeeded this run. A `PASS` with an empty command, or a command that never ran, is rejected as fabricated.

### Test report format

```json
{
  "results": [
    {
      "criterion": "The add command stores a task and returns a non-zero ID",
      "status": "PASS",
      "command": "cargo test test_add_command -- --nocapture",
      "exit_code": 0
    }
  ]
}
```

| Field | Description |
|-------|-------------|
| `results` | One entry per acceptance criterion. |
| `results[].criterion` | The criterion text from the brief. |
| `results[].status` | `PASS` or `FAIL`. |
| `results[].command` | The exact shell command that verified this criterion. Required for `PASS`. If one command verified several criteria, repeat it verbatim on each row rather than inventing a narrower one. |
| `results[].exit_code` | The exit code. Informational. |
| `results[].output` | Relevant output for a `FAIL`, so the next agent can diagnose without re-running. |

Declare this as the artifact's `shape` so the Tester is told the format and a malformed report is caught early.

---

## `relatedTestsPass`

`relatedTestsPass` runs the tests itself instead of reading history:

1. It reads the files changed this run from the change log.
2. It runs `advanced.testSelector.findRelatedCommand` once per changed file (`{file}` substituted); each output line is a test target.
3. It appends the targets to `fullSuiteCommand` and runs it, or runs the full suite when discovery found nothing.

It passes when that command exits 0. Use it on brownfield projects to enforce coverage of what changed without running everything on every turn. See [Test selector](configuration.md#test-selector).

---

## `acceptanceCriteriaPassed`

`acceptanceCriteriaPassed: brief` makes acceptance criteria machine-checkable. For each criterion in the brief with an `expected_output_contains` field, a successful command this run must have produced output containing that text:

```json
"acceptance_criteria": [
  {
    "criterion": "include 'lib/math'; x.sqrt(16) prints 4.0",
    "test_command": "kiwi test_include.ki",
    "expected_output_contains": "4.0"
  },
  "Response includes total_count and has_next fields"
]
```

Plain-string criteria, and objects without `expected_output_contains`, are left to the reviewer's judgement. `test_command` is shown in the correction message; the gate doesn't run it.

---

## `reviewJudgement`

`reviewJudgement` requires the agent's reply in this turn to hold a structured verdict, typically on a Reviewer's edge to `end`:

````
```json
{
  "review": [
    { "criterion": "<criterion text>", "verdict": "PASS", "evidence": "<what you ran or read>" },
    { "criterion": "<another>",        "verdict": "FAIL", "evidence": "<what you found>" }
  ]
}
```
````

It passes when:

1. the block exists, and every entry has a criterion, a `PASS` or `FAIL` verdict, and evidence;
2. no verdict is `FAIL`, because a failing review should go back for revision, not approve;
3. when the team has a `brief` artifact (or `reviewJudgement: <brief>` names one), there is an entry for every acceptance criterion;
4. if any verdict is `PASS`, a shell command succeeded in this turn, because reading code alone isn't verification.

The agent must write the block as text in the same reply as its handoff call.

---

## `anyFileWritten`, `noRepeatedShellFailures`, `sessionContextWritten`, `architecture`

| Check | Passes when |
|-------|-------------|
| `anyFileWritten` | The agent wrote at least one file (`write_file`, `patch_file`, or a commit) this turn. With `fallback: "npm install\|go mod tidy"`, a successful matching shell command also counts, for fixes that are only a command. Code in a reply is not a file. |
| `noRepeatedShellFailures: <pattern>` | Matching commands haven't failed several turns in a row. When they have, the agent is told to stop retrying and hand off for replanning. |
| `sessionContextWritten` | The agent called `session_context_write` this turn, so the next agent can pick up from its summary. |
| `architecture` | The changes respect the project's declared architecture rules. |

---

## Brief format

The checks that read a brief expect this shape. Declare it as the `brief` artifact's `shape` (with the fields your team uses) so the Planner is told the format:

```json
{
  "goal": "Add a pagination endpoint to the user list API",
  "files_to_change": [
    { "path": "src/api/users.py",    "reason": "Add pagination parameters" },
    { "path": "tests/test_users.py", "reason": "Tests for paginated responses" }
  ],
  "acceptance_criteria": [
    "GET /users?page=2&limit=10 returns the correct slice of results",
    "Invalid page or limit values return 400 with a descriptive error"
  ],
  "constraints": ["Do not add new dependencies"],
  "verify_command": "pytest tests/test_users.py"
}
```

---

## When an agent gets stuck

Each failure is classified as **missing evidence**, **invalid transition**, **conflicting evidence** (fabricated or inconsistent results) or **no progress** (handing off again without calling any tool). Each kind has its own correction message and its own threshold; when consecutive failures of one kind reach it, the run stops with a stuck error rather than looping. Defaults: 3, or 2 for conflicting evidence. Tune them with [`advanced.failureHandling`](configuration.md#failure-handling).

Two optional backstops close the remaining gaps: `maxConsecutiveContractFailures` caps failures on one edge across all kinds, and `maxConsecutiveTurnsWithoutSignal` stops an agent that keeps finishing turns without handing off at all.

An edge's `onStuck` agent is called in after two consecutive failures on that edge, with the failure and the change log, and told to fix the problem and hand back.

---

## Gates and instructions

Gates are the guarantee; instructions are how you avoid needing them. fuseraft already tells every agent which routes it has and what each one checks (see [Generated prompt text](configuration.md#generated-prompt-text)), so the agent knows the bar before it tries. Gates catch the turns where a model ignores that anyway.
