# Validators

Validators are deterministic pre-flight checks that run before a keyword route fires or before a termination strategy ends the session. They prevent agents from advancing the pipeline without real evidence — blocking claims like "I wrote the file" that were never actually executed, or "all tests pass" when no test report exists.

When a validator fails, the route or termination is blocked, an error message is injected into the conversation as a user turn, and the agent is re-invoked. The agent must address the error and retry before the handoff or completion can proceed.

---

## Validators on routing routes

Reference a validator by name on individual keyword routes:

```yaml
Selection:
  Type: keyword
  Routes:
    - Keyword: "HANDOFF TO DEVELOPER"
      Agent: Developer
      Validator: RequireBrief
      SourceAgents:
        - Planner
    - Keyword: "HANDOFF TO TESTER"
      Agent: Tester
      Validator: RequireWriteFile
      SourceAgents:
        - Developer
    - Keyword: "HANDOFF TO TESTER"
      Agent: Tester
      Validator: RequireShellPass
      RequiredCommandPattern: "go build|go test"
      SourceAgents:
        - Developer
    - Keyword: "HANDOFF TO REVIEWER"
      Agent: Reviewer
      Validator: TestReportValid
      SourceAgents:
        - Tester
```

## Validators on routes

Validators attached to a `Selection.Routes` entry run before the handoff fires. Use `Validators` (AND semantics — all must pass) or the single `Validator` field:

```yaml
Routes:
  - Keyword: "HANDOFF TO TESTER"
    Agent: Tester
    Validators:
      - RequireWriteFile
      - RequireShellPass
    SourceAgents:
      - Developer
  - Keyword: "HANDOFF TO REVIEWER"
    Agent: Reviewer
    Validator: TestReportValid
    SourceAgents:
      - Tester
  - Keyword: APPROVED
    Agent: Reviewer
    Validators:
      - RequireShellPass
      - RequireReviewJudgement
    SourceAgents:
      - Reviewer
```

## Validators on termination strategies

Validators may also be placed on `Termination.Strategies` entries. For keyword-routed configs, route validation via `Selection.Routes` is the authoritative gate — termination validators on the same pattern are not re-evaluated separately when the route already passed its validator.

```yaml
Termination:
  Type: composite
  MaxIterations: 40
  Strategies:
    - Type: regex
      Pattern: '(?m)^\s*APPROVED\s*$'
      AgentNames:
        - Reviewer
    - Type: maxiterations
      MaxIterations: 40
```

The `maxiterations` child always fires unconditionally — it is not affected by validators and acts as the hard cap.

The `Validation` section provides file paths and patterns used by the validators:

```yaml
Validation:
  BriefPath: ~/.fuseraft/sessions/{project_slug}/{session_id}/brief.json
  TestReportPath: .fuseraft/artifacts/test-report.json
  ChangeLogPath: ~/.fuseraft/state/{project_slug}/changes.json
  TestAssertionPatterns:
    - tester::assert
    - "if .+ throw"
    - \bassert\b
    - \bexpect\b
```

The `Validation` section is required when any route uses `TestReportValid`. It is optional for `RequireWriteFile` and `RequireShellPass`. `ChangeLogPath` enables check 8 — cross-referencing test-report commands against the [change log](configuration.md#change-tracking) produced by `ChangeTracking`; omit it to skip that check.

---

## RequireBrief

**Used on:** `HANDOFF TO DEVELOPER` (blocks the Planner from handing off without a written brief)

**What it checks:** Reads `brief.json` from `Validation.BriefPath` (default `~/.fuseraft/sessions/{project_slug}/{session_id}/brief.json`) and verifies it exists on disk with valid, complete content.

**Passes if:** `brief.json` exists, is valid JSON, and contains non-empty `goal`, `files_to_change`, `acceptance_criteria`, and `implementation` fields.

**Fails with a specific error for each missing piece:**

| Failure | Error |
|---------|-------|
| File missing | Instructs the Planner to explore the codebase and write the brief |
| Invalid JSON | Reports the parse error and asks for a fix |
| Empty `goal` | Asks for a one-sentence objective |
| Empty `files_to_change` | Asks for an explicit file list with reasons |
| Empty `acceptance_criteria` | Asks for testable criteria the Tester will verify |
| Empty `implementation` | Asks for ordered write/patch actions covering every file in `files_to_change` |

> **Note:** `RequireBrief` requires the `Validation` section to be present (it reads `Validation.BriefPath`). If no `Validation` section is configured the validator is silently unavailable.

---

## RequireShellPass

**Used on:** Any route where you require the agent to have run and verified a shell command this turn (e.g. a build or smoke-test before handoff)

**What it checks:** Walks backward through the conversation history looking for completed `shell_run` tool calls (`Role=Tool` messages with a `FunctionResultContent` whose function name contains `shell_run`). Stops at the most recent user-role message.

**Passes if:** At least one `shell_run` call completed successfully this turn — i.e. the result does not start with `[EXIT`, `[ERROR]`, `[TIMEOUT]`, or `[DENIED]`.

**Fails if:** No successful `shell_run` result is found — the agent either ran no command at all, or every command it ran failed.

### RequiredCommandPattern

When the route also sets `RequiredCommandPattern`, a passing shell run is only accepted if the original command matches at least one of the pipe-separated alternatives (case-insensitive):

```yaml
- Keyword: "HANDOFF TO TESTER"
  Agent: Tester
  Validator: RequireShellPass
  RequiredCommandPattern: "go build|go test"
```

With this config, a run of `go mod tidy` exits 0 but is rejected because it matches neither `go build` nor `go test`. The agent must run the actual build or test command before the handoff fires.

The pattern is extracted from the `FunctionCallContent` that preceded the result, so the check is mechanical — it reads the command the model actually called, not what the model wrote in prose.

**Error injected on failure:**

```
Handoff blocked: no matching (go build|go test) shell_run passed this turn.

The validator checks THIS TURN ONLY — prior-turn runs do not carry forward.

  1. Call shell_run to run the required command (exit 0).
  2. Emit the handoff keyword in the same response.
```

---

## RequireWriteFile

**Used on:** Any route where the agent must have written at least one file this turn (e.g. `HANDOFF TO TESTER`, `RECON COMPLETE`, `HANDOFF TO REVIEWER`)

**What it checks:** Walks backward through the conversation history looking for completed `write_file` or `patch_file` tool calls (`Role=Tool` messages with a `FunctionResultContent` whose function name contains either string). Stops at the most recent user-role message.

**Passes if:** At least one `write_file` or `patch_file` call completed in the current agent turn.

**Fails if:** Neither tool was called — meaning the agent described a file write in text but never actually called the tool. Text, code blocks, and responses are not saved to disk.

### ShellFallbackPattern

Some fixes require only a shell command (e.g. a dependency update) and produce no file-write call. Set `ShellFallbackPattern` on the route to allow a successful matching `shell_run` to satisfy the validator instead:

```yaml
- Keyword: "HANDOFF TO TESTER"
  Agent: Tester
  Validator: RequireWriteFile
  ShellFallbackPattern: "npm install|pip install|go mod tidy|go get"
```

The pattern is a pipe-separated list of substrings (case-insensitive). The validator passes if the turn contains a successful `shell_run` whose command matches any alternative. A failed shell command (exit code non-zero, `[ERROR]`, `[TIMEOUT]`, `[DENIED]`) is never accepted regardless of the pattern.

When `ShellFallbackPattern` is omitted the validator behaves as before — only `write_file`, `patch_file`, or `git_commit` satisfy it.

**Error injected on failure:**

```
Handoff blocked: no evidence of real work this turn
(no write_file, no patch_file, no git_commit, no shell fallback matched).

You must write at least one file before handing off. Use write_file for new files
or patch_file for surgical edits to existing files. Code blocks in your response
are NOT saved to disk — you must call the tool.
```

---

## RequireAllFilesWritten

**Used on:** `HANDOFF TO TESTER` (or any route where you need to verify the entire brief's file list has been written, not just one file)

**What it checks:** Reads `brief.json` from `Validation.BriefPath` and verifies that every path listed in `files_to_change` has been written via `write_file` — either in the current agent turn or in a previous turn recorded in `changes.json`.

This closes the loophole where a Developer writes a single helper file to satisfy `RequireWriteFile` while leaving the important implementation files unwritten.

**`files_to_change` format:** Both plain strings and objects are supported:

```json
"files_to_change": [
  "src/api/users.py",
  { "path": "tests/test_users.py", "reason": "Add pagination tests" }
]
```

The validator extracts the path from both forms automatically.

**Two sources are checked:**

1. **Current turn** — scans `Role=Tool` messages backward to the most recent user message, collecting the `path` argument of each successful `write_file` call (matched via `FunctionCallContent.Id` / `FunctionResultContent.CallId`).
2. **Previous turns** — when `Validation.ChangeLogPath` is configured, reads `changes.json` and accumulates `FilesWritten` entries from the current session (filtered by `ActiveSessionId` to exclude prior sessions).

**Passes if:** Every path in `brief.json`'s `files_to_change` is covered by at least one source.

**Fails if:** One or more required files have not been written, with each missing path listed in the error and two remediation options:

```
Handoff blocked: these files from brief.json were not written this session:

  ✗ src/api/users.py
  ✗ tests/test_users.py

To resolve, either:
  A. Modify each missing file with write_file or patch_file.
  B. If a file does not actually need changes, update brief.json to remove it
     from files_to_change, then retry the handoff.
```

> **Important:** Every file listed in `files_to_change` must be written this session — including files that already exist on disk. Listing a file in `files_to_change` is a commitment to modify it. If the file turns out not to need changes, remove it from the brief rather than leaving it unwritten.

**Path matching** is case-insensitive and normalises leading `./` so `./src/main.go` and `src/main.go` are treated as the same file. Absolute/relative mismatches (e.g. `/project/src/main.go` vs `src/main.go`) are handled via suffix matching.

**When `brief.json` is missing**, the handoff is blocked with an error asking the Planner to write it. When `files_to_change` is absent or empty the validator passes immediately (nothing to verify).

**Example route config:**

```yaml
- Keyword: "HANDOFF TO TESTER"
  Agent: Tester
  Validator: RequireAllFilesWritten
  SourceAgents:
    - Developer
```

**Relationship to RequireWriteFile:** Use `RequireWriteFile` when you only need evidence that _something_ was written this turn (cheap check). Use `RequireAllFilesWritten` when you need to verify completeness against the brief (stronger check, requires `Validation` section with `BriefPath`).

> **Note:** `RequireAllFilesWritten` requires the `Validation` section to be present (it reads `Validation.BriefPath`). `Validation.ChangeLogPath` is optional — omit it to check only the current turn.

---

## RequireRelatedTestsPass

**Used on:** `HANDOFF TO TESTER` or `HANDOFF TO REVIEWER` (blocks handoff until targeted tests for the session's changed files pass)

**What it checks:** Reads the change log to find every file written in the current session, runs `TestSelector.FindRelatedCommand` for each changed file to discover related test targets, then executes only those tests. Falls back to `TestSelector.FullSuiteCommand` when discovery returns nothing.

**Passes if:** The test command (targeted or full-suite fallback) exits 0.

**Fails if:** The discovery command fails, the test runner exits non-zero, or `TestSelector` is not configured.

**Requirements:**
- `TestSelector.FindRelatedCommand` must be set (template with `{file}` substitution).
- `TestSelector.FullSuiteCommand` must be set (used as fallback and as the test runner that receives discovered targets as arguments).
- `ChangeTracking` must be configured so changed files can be resolved.

**Example route config:**

```yaml
Orchestration:
  ChangeTracking:
    Path: ~/.fuseraft/state/{project_slug}/changes.json

  TestSelector:
    FindRelatedCommand: "pytest --collect-only -q {file} 2>/dev/null | grep '::' | head -40"
    FullSuiteCommand: "pytest"

  Contracts:
    - Name: TestedChanges
      Requires:
        - RelatedTestsPass

  Selection:
    Type: keyword
    Routes:
      - Keyword: "HANDOFF TO REVIEWER"
        Agent: Reviewer
        Contracts:
          - TestedChanges
        SourceAgents:
          - Tester
```

**How target discovery works:**

1. Changed files are read from `changes.json` for the active session (`ActiveSessionId`).
2. `FindRelatedCommand` is run once per changed file (`{file}` substituted). Each non-empty output line is treated as a test target.
3. Discovered targets are appended as space-separated arguments to `FullSuiteCommand`.
4. If discovery produces no targets, `FullSuiteCommand` is run without arguments (full suite fallback).

**Error injected on failure:**

```
Handoff blocked: targeted tests failed (exit 1).

Command: pytest tests/test_api.py tests/test_auth.py

<test output truncated to 2000 chars>
```

**Relationship to `RequireShellPass`:** `RequireShellPass` checks whether the agent ran any passing shell command this turn. `RequireRelatedTestsPass` is a contract predicate that the orchestrator runs independently — it does not inspect conversation history, it runs the tests itself. Use `RequireRelatedTestsPass` when you want the framework to enforce test coverage of specific changed files. Use `RequireShellPass` when you want to verify the agent ran a command in its own turn.

---

## TestReportValid

**Used on:** `HANDOFF TO REVIEWER`

**What it checks (in order):**

1. `test-report.json` exists at `Validation.TestReportPath`
2. The file is valid JSON and has at least one entry in `results`
3. No result has `status: FAIL`
4. No PASS result has an empty `command` field (fabrication guard)
4b. No PASS result has a plugin tool-call string in the `command` field (e.g. `FileSystem-read_file path=...`) — a test result must be backed by a real shell command, not a file read
5. The `fake_test_files` array is empty (or absent)
6. The number of results is at least as large as the number of acceptance criteria in `brief.json` (if `brief.json` exists)
7. Every test file listed in `brief.json`'s `files_to_change` that contains "test" in its path contains at least one pattern from `TestAssertionPatterns` (static fake-test detection)
8. PASS result commands are cross-referenced against `changes.json` (if `Validation.ChangeLogPath` is set) — each non-trivial command in the report must match a command that was actually executed and recorded by the change tracker

**Passes if:** All applicable checks pass.

**Fails with a specific error for each check.**

### Test report schema

The Tester must write a file at `Validation.TestReportPath` (default `.fuseraft/artifacts/test-report.json`) matching this schema before writing `HANDOFF TO REVIEWER`:

```json
{
  "results": [
    {
      "criterion": "The add command stores a task and returns a non-zero ID",
      "status": "PASS",
      "command": "cargo test test_add_command -- --nocapture",
      "exit_code": 0
    }
  ],
  "fake_test_files": []
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `results` | yes | Array with one entry per acceptance criterion. |
| `results[].criterion` | yes | The exact acceptance criterion text from the brief. |
| `results[].status` | yes | `PASS` or `FAIL`. |
| `results[].command` | yes (for PASS) | The exact shell command that was executed to verify this criterion. An empty command on a PASS result is rejected as fabricated. |
| `results[].exit_code` | no | The actual exit code. Informational — not validated. |
| `results[].output` | yes (for FAIL) | Relevant stderr/stdout from the failing command. Required on FAIL results so the Developer can diagnose without re-running. Omit on PASS results — the Reviewer re-runs commands themselves and pre-recorded output adds noise to the history. |
| `fake_test_files` | yes | List of test files that contain no real assertions. Must be empty for handoff to proceed. |

### Assertion pattern detection

The `TestAssertionPatterns` list contains regular expressions. A test file is considered real if its contents match at least one pattern. The defaults cover the most common assertion styles:

| Pattern | Matches |
|---------|---------|
| `tester::assert` | Kiwi-lang / fuseraft-lang assertions |
| `if .+ throw` | Manual if/throw assertion pattern |
| `\bassert\b` | Native assert (most languages) |
| `\bexpect\b` | Jest, Chai, Vitest, RSpec, etc. |

Add your own patterns if your test framework uses a different convention:

```yaml
Validation:
  TestAssertionPatterns:
    - \bassert\b
    - \bexpect\b
    - assertEqual
    - assertTrue
    - should\.be
    - "#\\[test\\]"
```

### Command cross-reference (check 8)

When `Validation.ChangeLogPath` is set and `changes.json` exists, check 8 cross-references the commands listed in `test-report.json` PASS results against the commands that were actually executed and recorded by the [change tracker](configuration.md#change-tracking).

Two tiers:

1. **Any commands ran at all** — if `changes.json` records zero successful commands but the report has PASS results, all passes are rejected as unverifiable.
2. **Per-result token match** — for each PASS result whose `command` field is 8+ characters, at least one executed command in `changes.json` must share a significant token with it (substring or token-overlap match). Commands shorter than 8 characters are skipped to avoid false matches on tokens like `run`.

**Error when no commands ran:**

```
HANDOFF TO REVIEWER blocked: N PASS result(s) but no shell commands recorded. Run tests with shell_run before handing off.
```

**Error when a command has no match:**

```
HANDOFF TO REVIEWER blocked: 1 PASS result(s) not found in the change log.

Each PASS must be backed by a real shell_run this session:
  1. shell_run the command.
  2. Update 'command' in test-report.json.
  3. write_file the report.

Unverified:
  ✗ cargo test test_add_command -- --nocapture

Commands recorded in changes.json for this session:
  - go test ./...
```

---

## RequireReviewJudgement

**Used on:** The `APPROVED` route in `Selection.Routes` (blocks session completion until the Reviewer has produced a structured per-criterion judgement)

**What it checks:** Scans the Reviewer's last assistant message in the current turn for a `{"review":[...]}` JSON block and enforces four rules:

1. The block exists and has at least one entry with non-empty `criterion`, `verdict`, and `evidence`.
2. No entry has `verdict: FAIL` — a FAIL means the change is incomplete and must be routed back.
3. When `Validation.BriefPath` is set, the review must have at least as many entries as the brief's `acceptance_criteria` array — every criterion must be explicitly addressed.
4. When any entry has `verdict: PASS`, at least one successful `shell_run` must have been called this turn — code inspection alone is not sufficient to claim PASS.

**Passes if:** All four rules are satisfied.

**Fails with a specific error for each rule.**

### Expected format

The Reviewer should emit the block inside a ` ```json ``` ` fence before writing the decision keyword:

```json
{
  "review": [
    { "criterion": "<exact criterion text from brief.json>", "verdict": "PASS", "evidence": "<one sentence: what you ran or read to verify this>" },
    { "criterion": "<another criterion>",                    "verdict": "FAIL", "evidence": "<what you found>" }
  ]
}
```

`verdict` must be `PASS` or `FAIL`. Every acceptance criterion from the brief must appear as an entry when `Validation.BriefPath` is configured.

**Errors injected on failure:**

*No review block:*
```
APPROVED blocked: response has no structured review block.

Add a ```json block before APPROVED:
{ "review": [{ "criterion": "...", "verdict": "PASS", "evidence": "..." }] }

Cover every acceptance criterion. Use PASS or FAIL. Then write APPROVED on its own line.
```

*FAIL verdict present:*
```
APPROVED blocked: review contains FAIL verdict(s).

Fix the failing criteria before writing APPROVED, or route
REVISION REQUIRED so the developer can address them.

Failing:
  ✗ <criterion text>
```

*Coverage gap (when BriefPath is set):*
```
APPROVED blocked: the review covers 2 criterion/criteria but brief.json lists 4.

Add one review entry per acceptance criterion — every criterion must be explicitly
addressed with its own verdict and evidence.
```

*No shell run for PASS verdict:*
```
APPROVED blocked: review contains PASS verdicts but no shell command was
successfully run this turn.

Code inspection alone is not sufficient — the Reviewer must execute something
to confirm behavioral correctness:

  1. Run a verification command with shell_run:
       • Build:  shell_run("dotnet build") / etc.
       • Tests:  shell_run("dotnet test") / etc.
       • Run-it: shell_run("kiwi test.ki") or equivalent.
  2. Update 'evidence' fields to describe what you ran and what you observed.
  3. Re-emit the ```json review block followed by APPROVED on its own line.
```

**Typical config (combined with `RequireShellPass` and `BriefPath`):**

```yaml
Validation:
  BriefPath: ~/.fuseraft/sessions/{project_slug}/{session_id}/brief.json

Selection:
  Type: keyword
  Routes:
    - Keyword: APPROVED
      Agent: Reviewer
      Validators:
        - RequireShellPass
        - RequireReviewJudgement
      SourceAgents:
        - Reviewer
```

`RequireShellPass` and the shell-run check in `RequireReviewJudgement` are complementary: `RequireShellPass` blocks the route if *no* shell command ran at all; `RequireReviewJudgement`'s inner check additionally requires a shell run specifically when the review claims PASS verdicts. Setting `Validation.BriefPath` activates coverage enforcement.

> **Note:** `APPROVED` must be declared in `Selection.Routes` for the routing engine to recognize it. Adding it only to `Termination.Strategies` is insufficient — route ownership is read exclusively from `Selection.Routes`.

> **Note:** When `Validation.BriefPath` is omitted, the coverage check is skipped. The other three checks always run.

---

## RequireAcceptanceCriteriaPassed

**Used on:** The `developer → reviewer` handoff edge, and optionally the `reviewer → approved` edge for defence in depth.

**What it checks:** Reads `brief.json` from `Validation.BriefPath` and finds every acceptance criterion that has an `expected_output_contains` field. For each such criterion, it scans the session's shell history in `changes.json` (filtered to the current `ActiveSessionId`) and verifies that at least one successful command produced output containing that sentinel string. Criteria without `expected_output_contains` are skipped — they remain subject to the Reviewer's judgement.

**Passes if:** Every machine-testable criterion has been satisfied by a real command run that produced the expected output.

**Fails if:** Any testable criterion has no matching successful command output this session.

### Testable criterion format

Write acceptance criteria as objects with `expected_output_contains` to make them machine-verifiable:

```json
"acceptance_criteria": [
  {
    "criterion": "x = include 'lib/math'; x.sqrt(16) prints 4.0",
    "test_command": "kiwi test_include.ki",
    "expected_output_contains": "4.0"
  },
  {
    "criterion": "build.sh succeeds",
    "test_command": "./build.sh",
    "expected_output_contains": "BUILD SUCCEEDED"
  }
]
```

| Field | Required | Description |
|-------|----------|-------------|
| `criterion` | yes | Human-readable description of the expected behavior. |
| `expected_output_contains` | yes (to be machine-testable) | Sentinel string that must appear in a successful command's output. Case-insensitive. |
| `test_command` | no | The command the Developer should run. Shown in the error message when the criterion is unsatisfied. Not executed by the validator itself. |

Plain-string criteria and object criteria without `expected_output_contains` are ignored by this validator.

**Error injected on failure:**

```
Handoff blocked: the following acceptance criteria have not been verified by a passing shell command this session:

  ✗ x = include 'lib/math'; x.sqrt(16) prints 4.0
    Run: kiwi test_include.ki
    Expected in output: "4.0"

Run the indicated command(s), confirm the expected output appears, then retry the handoff.
```

**Example route config:**

```yaml
Validation:
  BriefPath:     ~/.fuseraft/sessions/{project_slug}/{session_id}/brief.json
  ChangeLogPath: ~/.fuseraft/state/{project_slug}/changes.json

Selection:
  Type: keyword
  Routes:
    - Keyword: "HANDOFF TO REVIEWER"
      Agent: Reviewer
      Validators:
        - RequireAllFilesWritten
        - RequireAcceptanceCriteriaPassed
      SourceAgents:
        - Developer
    - Keyword: APPROVED
      Agent: Reviewer
      Validators:
        - RequireShellPass
        - RequireReviewJudgement
        - RequireAcceptanceCriteriaPassed  # defence in depth
      SourceAgents:
        - Reviewer
```

> **Note:** `RequireAcceptanceCriteriaPassed` requires `Validation.BriefPath` to be set (it reads acceptance criteria from the brief). `Validation.ChangeLogPath` is required to read command outputs from the session history — without it the validator passes immediately (nothing to check against).

**Relationship to `RequireReviewJudgement`:** `RequireReviewJudgement` checks that the Reviewer wrote a structured verdict block. `RequireAcceptanceCriteriaPassed` checks that the Developer (or the Reviewer) actually *ran* commands whose output matched the brief's sentinels. Use both together for maximum coverage: the former enforces a structured narrative review; the latter enforces that the feature was mechanically verified.

---

## Brief schema

The `TestReportValid` validator reads `brief.json` to check criterion coverage. The Planner agent should write this file using `write_file` before handing off to the Developer.

```json
{
  "goal": "Add a pagination endpoint to the user list API",
  "files_to_change": [
    { "path": "src/api/users.py",      "reason": "Add pagination parameters to list endpoint" },
    { "path": "tests/test_users.py",   "reason": "Add tests for paginated response" }
  ],
  "files_for_context": [
    { "path": "src/api/base.py",       "reason": "Understand existing endpoint patterns" }
  ],
  "acceptance_criteria": [
    "GET /users?page=2&limit=10 returns the correct slice of results",
    "Response includes total_count and has_next fields",
    "Invalid page or limit values return 400 with a descriptive error",
    "Test files contain real assertions (not just print statements)"
  ],
  "constraints": [
    "Do not change the existing /users endpoint response shape for page=1",
    "Do not add new dependencies"
  ],
  "implementation": [
    { "action": "patch", "path": "src/api/users.py",    "description": "Add page/limit query params and slice logic" },
    { "action": "write", "path": "tests/test_users.py", "description": "Tests for paginated response and error cases" }
  ]
}
```

---

## Evidence contracts

Evidence contracts are a composable, reusable alternative to individual validators. Where validators are attached directly to a route and contain logic in code, contracts are declared in YAML and reference named predicates. Multiple routes and state machine transitions can share the same contract without duplicating validation logic.

```yaml
Orchestration:
  Contracts:
    - Name: ImplementationComplete
      Requires:
        - CommandSucceeded:
            PatternField: "verify_command"   # reads the verify command from brief.json

    - Name: TestsValid
      Requires:
        - FileExists:
            Path: .fuseraft/artifacts/test-report.json
        - TestReport:
            NoFailures: true
            HasAssertions: true

  Selection:
    Type: keyword
    Routes:
      - Keyword: "HANDOFF TO TESTER"
        Agent: Tester
        Contracts:
          - ImplementationComplete
        SourceAgents:
          - Developer
```

**When to use contracts vs. validators**

| | Validators | Contracts |
|---|---|---|
| Declared in | Code (built-in) | YAML (your config) |
| Reusable across routes | No — attach individually | Yes — reference by name |
| Supported routing types | Keyword, termination | Keyword, state machine |
| Evidence source | Conversation history scan | Evidence graph (or `changes.json`) |
| Custom predicates | No | Yes (FilesWritten, ChecklistComplete, CommandSucceeded, FileExists, TestReport, RelatedTestsPass) |

Contracts and validators compose: a route may declare both `Validators` and `Contracts`. All must pass (AND semantics).

For full predicate reference, see [Evidence contracts](configuration.md#evidence-contracts).

> **Why not include `FilesWritten` in `ImplementationComplete`?** Adding `FilesWritten` alongside `CommandSucceeded` creates two separate satisfaction points: the contract partially passes when files land on disk, giving the agent a checkpoint before the verify command runs. In practice this causes agents to commit early — once on file write, then again after verify — producing duplicate commits with identical messages. `CommandSucceeded` on the `verify_command` is sufficient: if the verify command passes, the files were obviously written correctly.

---

## Relationship between validators and agent instructions

Validators are a mechanism-level guarantee — they run in code regardless of what the agent says. But they work best when agent instructions are aligned. The default config's Tester instructions explicitly prohibit writing `HANDOFF TO REVIEWER` without a valid test report, and the Developer is told to always call `write_file` before handing off. Validators catch cases where the model ignores these instructions.

---

## Stuck detection

`ValidatorStuckException` is the hard stop that ends a session when an agent cannot get past a route. What counts as "stuck" — and which counter tracks it — depends on `Selection.Type`:

- **No keyword** — the response contains no recognized keyword on its own line.
- **Foreign keyword** — the response contains a keyword that belongs to a different agent role (e.g. Developer writing `BUGS FOUND`, which is a Tester-only keyword).
- **Multiple keywords** — the response contains more than one keyword on separate lines (ambiguous).
- **Validator failure** — the response has a valid keyword but a pre-flight validator (e.g. `RequireShellPass`) rejected the handoff.

**`Selection.Type: graph`** (`GraphOrchestrator`): a single counter covers all four modes above. It increments whenever any correction is injected and resets only when the agent produces a clean routed turn — alternating failure modes hit the threshold at the same rate as consecutive identical failures. Threshold: `Selection.Graph.MaxRetries` (default 4).

**`Selection.Type: keyword`** (`KeywordSelectionStrategy`) **and `statemachine`** (`StateMachineSelectionStrategy`): validator/contract failures are classified by type (`MissingEvidence`, `InvalidTransition`, `ConflictingEvidence`, `NoProgress`) and escalate independently per `FailureHandling.<Type>.Threshold` (default 3, except `ConflictingEvidence` which defaults to 2) — see [Failure handling](configuration.md#failure-handling). A turn with **no keyword/signal at all** is *not* covered by that counter: it triggers a periodic warning every 5 consecutive same-agent turns and is otherwise bounded only by `Termination.MaxIterations`, unless you explicitly set `FailureHandling.MaxConsecutiveTurnsWithoutSignal` (statemachine only; default `0` = disabled).
