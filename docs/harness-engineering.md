# Harness Engineering

Harness engineering is the practice of designing orchestration configs so that agents cannot advance the pipeline without real, mechanical evidence — regardless of what they claim in prose. This page explains the control layers available and how to combine them into a config that is resistant to hallucinated progress.

---

## The core problem

Agents can write confident-sounding output that does not reflect what actually happened. An agent might say "I wrote the implementation" without ever calling `write_file`. It might claim "all tests pass" without running a single command. Without a mechanical enforcement layer, the pipeline advances on claims rather than facts.

fuseraft addresses this with four interlocking control layers:

| Layer | What it does |
|-------|-------------|
| **Validators** | Block routes until a disk artifact or tool-call record proves the claim |
| **Change tracking** | Records every file write, shell command, and git commit to a JSONL log on disk |
| **Routing corrections** | Injects error messages and re-invokes the agent when routing signals are wrong or validators fail |
| **Stagnation detection** | Throws after 3 consecutive bad turns rather than letting an agent loop |

---

## Change tracking

Enable change tracking first — it is the ground-truth record that validators and compaction grounding depend on.

```yaml
ChangeTracking:
  Path: .fuseraft/state/changes.json
```

With this enabled, every `write_file`, `delete_file`, `shell_run`, `shell_run_script`, and `git_commit` call is recorded to `changes.json`. Downstream agents can call `changes_read_latest()` (via the `Changes` plugin) to see what previous agents actually did. Validators that reference `Validation.ChangeLogPath` cross-check their evidence against this log.

Add `Changes` to the Tester and Reviewer agent plugin lists so they can inspect the record:

```yaml
- Name: Tester
  Plugins:
    - FileSystem
    - Shell
    - Changes
```

---

## Execution state

When `ChangeTracking` is configured, fuseraft maintains a second derived artifact alongside `changes.json`: `execution-state.json` in the same state directory. It is written after every agent turn and injected into every agent's context as the `execution_state` source.

**What agents see:**

| Field | Content |
|-------|---------|
| `ActiveFailures` | Compiler/linker errors extracted from the most recent failed build — file, line, error code, and message. Cleared on the next successful build. |
| `FailedAttempts` | Ring buffer (last 10) of attempts that were recorded as failed this session — description, error summary, and timestamp. |
| `SignificantChanges` | Ring buffer (last 50) of file writes, patches, copies, and deletes this session — path, operation, and timestamp. |
| `Build` | Most recent build result — succeeded flag, exit code, command, and errors. |

**Session scoping:**

The execution state file lives at the project level (next to `changes.json`) and persists on disk between runs. On session start, fuseraft compares the on-disk `SessionId` to the current session's ID. If they differ, the file is reset to a clean state before the first agent turn runs — so a new run never inherits `ActiveFailures`, `FailedAttempts`, `SignificantChanges`, or a stale `Build.Succeeded` from a prior run.

Within a session, state accumulates across all turns and survives compaction. A REPLAN loop in the same session picks up where it left off — `FailedAttempts` from earlier Developer turns are still visible to the Planner when deciding how to revise the brief.

**When it matters:**

- The Developer reads `ActiveFailures` to know exactly which compiler errors to fix rather than re-running a build to rediscover them.
- The Planner reads `FailedAttempts` on a REPLAN to avoid proposing an approach the Developer already tried.
- The Verifier cross-checks `FailedAttempts` against the change log to detect silent failures (errors that recur without being recorded).

---

## Validators

Validators are deterministic pre-flight checks that run before a keyword route fires. They inspect disk artifacts, tool-call records in the conversation history, or both. If a check fails, the route is blocked, an error message is injected, and the source agent is re-invoked.

### RequireBrief

Blocks until `brief.json` exists on disk with non-empty `goal`, `files_to_change`, `acceptance_criteria`, and `implementation`.

```yaml
- Keyword: "HANDOFF TO DEVELOPER"
  Agent: Developer
  Validator: RequireBrief
  SourceAgents:
    - Planner
```

The Planner must call `write_file` to produce `.fuseraft/artifacts/brief.json` before this route fires. A claimed brief — one described in prose but never written — will not pass.

### RequireWriteFile

Blocks unless the current agent called `write_file` at least once in the current turn.

```yaml
- Keyword: "HANDOFF TO TESTER"
  Agent: Tester
  Validator: RequireWriteFile
  SourceAgents:
    - Developer
```

Use `ShellFallbackPattern` when a fix requires only a shell command (no file write):

```yaml
- Keyword: "HANDOFF TO TESTER"
  Agent: Tester
  Validator: RequireWriteFile
  ShellFallbackPattern: "npm install|pip install|go get"
  SourceAgents:
    - Developer
```

### RequireAllFilesWritten

Stronger than `RequireWriteFile` — checks that every path in `brief.json`'s `files_to_change` was written either this turn or in a prior turn recorded in `changes.json`. Closes the loophole where an agent writes one trivial file and hands off while leaving implementation files untouched.

```yaml
- Keyword: "HANDOFF TO TESTER"
  Agent: Tester
  Validator: RequireAllFilesWritten
  SourceAgents:
    - Developer
```

### RequireShellPass

Blocks unless a shell command exited 0 in the current turn. Use `RequiredCommandPattern` to require a specific command.

```yaml
- Keyword: "HANDOFF TO TESTER"
  Agent: Tester
  Validator: RequireShellPass
  RequiredCommandPattern: "go build|cargo build|npm run build"
  SourceAgents:
    - Developer
```

Without `RequiredCommandPattern`, any successful shell run satisfies the check. With it, `go mod tidy` exits 0 but is rejected because it does not match the pattern — the agent must actually run the build.

### TestReportValid

Blocks unless a valid `.fuseraft/artifacts/test-report.json` exists and passes eight structural checks, including: no FAIL results, real assertion patterns in test files, no empty `command` fields on PASS results, and (when a change log is configured) PASS result commands cross-referenced against commands that were actually executed.

```yaml
- Keyword: "HANDOFF TO REVIEWER"
  Agent: Reviewer
  Validator: TestReportValid
  SourceAgents:
    - Tester
```

The Tester must write the test report using `write_file` before this route fires. See [Validators](validators.md#testreportvalid) for the full schema and check details.

### RequireReviewJudgement

Blocks unless the Reviewer's last message contains a `{"review": [...]}` JSON block with `criterion`, `verdict`, and `evidence` entries — one per acceptance criterion.

```yaml
- Keyword: APPROVED
  Agent: Reviewer
  Validators:
    - RequireShellPass
    - RequireReviewJudgement
  SourceAgents:
    - Reviewer
```

Used on the terminal route so the session cannot end with a vague "looks good" message. Combines with `RequireShellPass` to require both a real shell run and a structured per-criterion verdict.

---

## Stacking validators

Use the `Validators` list (AND semantics — all must pass) instead of a single `Validator` when a route requires multiple checks:

```yaml
Routes:
  - Keyword: "HANDOFF TO TESTER"
    Agent: Tester
    Validators:
      - RequireWriteFile
      - RequireShellPass
    RequiredCommandPattern: "go build|go test"
    SourceAgents:
      - Developer
```

The route fires only when both validators pass. If either fails, the source agent is re-invoked with the specific failure message.

---

## Source-agent restrictions

`SourceAgents` enforces role boundaries mechanically. A keyword not authored by an agent in the list is ignored — the routing engine treats it as if the keyword was not present.

```yaml
- Keyword: "BUGS FOUND"
  Agent: Developer
  SourceAgents:
    - Tester   # Only the Tester can send Developer back to fix bugs
- Keyword: "REVISION REQUIRED"
  Agent: Developer
  SourceAgents:
    - Reviewer  # Only the Reviewer can trigger revision
```

Without `SourceAgents`, any agent could emit `BUGS FOUND` and route another agent backwards. With it, the Developer cannot self-report as the Tester.

---

## The Validation section

Provide file paths used by validators that read disk artifacts:

```yaml
Validation:
  BriefPath: .fuseraft/artifacts/brief.json
  TestReportPath: .fuseraft/artifacts/test-report.json
  ChangeLogPath: .fuseraft/state/changes.json
  TestAssertionPatterns:
    - \bassert\b
    - \bexpect\b
    - assertEqual
    - "#\\[test\\]"
```

`ChangeLogPath` enables check 8 in `TestReportValid` — PASS result commands are cross-referenced against `changes.json`. Omit it to skip that check. `TestAssertionPatterns` are .NET regexes; add patterns for your test framework if the defaults do not cover it.

---

## Routing corrections

When an agent produces no valid routing keyword, an unknown keyword, multiple keywords in the same response, or a keyword that belongs to a different role, fuseraft injects a correction message and re-invokes the agent. The agent does not advance the pipeline — it must produce a valid turn to proceed.

**The counter covers all failure modes together.** A turn with no keyword, then a turn with a wrong-role keyword, then a turn with a validator failure increments the counter to 3 — it does not reset between different failure types.

When the counter reaches 3 a `ValidatorStuckException` is raised and the session stops. This prevents an agent from looping indefinitely between different failure modes.

---

## Human-in-the-loop gates

Add `RequireHumanApproval: true` to any route to pause and prompt the operator before that route fires, regardless of whether `--hitl` is set:

```yaml
- Keyword: "HANDOFF TO DEVELOPER"
  Agent: Developer
  Validator: RequireBrief
  RequireHumanApproval: true
  SourceAgents:
    - Planner
```

The operator sees the Planner's output and the brief, then types `y` to allow the handoff or `n` to re-invoke the Planner with a message. This is useful for high-trust checkpoints (e.g. reviewing the brief before a Developer writes code, or approving the APPROVED route before the session ends).

---

## Compaction grounding

When history compaction is enabled, the compaction model summarizes old turns before they are dropped. Without grounding, fabricated claims in those turns bake into the summary and carry forward as apparent facts.

To ground summaries in the change log, configure both `Compaction` and `ChangeTracking`:

```yaml
ChangeTracking:
  Path: .fuseraft/state/changes.json

Compaction:
  TriggerTurnCount: 30
  KeepRecentTurns: 8
  Model:
    ModelId: gpt-4o-mini
```

When `Validation.ChangeLogPath` is set and `changes.json` exists, the compaction prompt reads the log and instructs the summarizer to correct any agent claims that contradict it. A claim like "I wrote `src/main.rs`" is rejected by the summary if `changes.json` records no such write in that session.

Set `TriggerTurnCount` high enough that implementation is mostly complete before compaction fires. A value of 30–50 is a reasonable default for a four-agent dev team. Compacting at turn 4–6 mid-implementation loses live context before the agents have established their state.

---

## Iteration control

Three settings cap how long a session runs:

| Setting | Where | What it does |
|---------|-------|-------------|
| `MaxIterations` | `Termination` | Hard cap on total agent turns |
| `MaxTailMessages` | `Agent.ContextWindow` | Trims how much history each agent sees |
| `MaxTotalTokens` | Top-level | Hard budget cap across the whole session |

For a four-agent pipeline use a composite termination strategy:

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

The `regex` strategy ends the session cleanly when the Reviewer emits `APPROVED`. The `maxiterations` child is a safety cap — it fires unconditionally at turn 40 regardless of validators or keywords.

---

## Sandbox enforcement

Restrict agent file and shell access to a directory tree. Paths outside the sandbox are denied at the tool level regardless of agent instructions:

```yaml
Security:
  FileSystemSandboxPath: /workspace/project
```

All `read_file`, `write_file`, `delete_file`, and shell path arguments are resolved canonically. Any access outside the tree returns `[DENIED: sandbox]`. System binary prefixes (`/usr/`, `/bin/`, `/etc/`) are exempted so agents can run standard tools.

For stricter isolation, add `CodeExecution` to the agent's plugins list and configure a Docker sandbox. Commands run inside a container rather than the host shell — they cannot write outside the container filesystem regardless of sandbox config.

---

## A complete hardened config

The following is the minimal skeleton of a four-agent config with all enforcement layers active:

```yaml
Orchestration:
  Name: Software Team

  ChangeTracking:
    Path: .fuseraft/state/changes.json

  Validation:
    BriefPath: .fuseraft/artifacts/brief.json
    TestReportPath: .fuseraft/artifacts/test-report.json
    ChangeLogPath: .fuseraft/state/changes.json
    TestAssertionPatterns:
      - \bassert\b
      - \bexpect\b

  Compaction:
    TriggerTurnCount: 30
    KeepRecentTurns: 8
    Model:
      ModelId: gpt-4o-mini

  Security:
    FileSystemSandboxPath: /workspace

  Models:
    strong:
      ModelId: gpt-4o
    fast:
      ModelId: gpt-4o-mini

  Agents:
    - Name: Planner
      Instructions: >-
        You are a software planner. Read the codebase, identify what needs to change,
        and write .fuseraft/artifacts/brief.json with goal, files_to_change, and acceptance_criteria.
        When done, write HANDOFF TO DEVELOPER on its own line.
      Model: strong
      Plugins: [FileSystem]
      FunctionChoice: auto
      TrustScore: 0.9

    - Name: Developer
      Instructions: >-
        You are a software developer. Read .fuseraft/artifacts/brief.json to understand the task.
        Implement every file in files_to_change. Run the build with shell_run to verify
        before handing off. Write HANDOFF TO TESTER on its own line when done.
      Model: strong
      Plugins: [FileSystem, Shell, Git, Changes]
      FunctionChoice: auto
      TrustScore: 0.9

    - Name: Tester
      Instructions: >-
        You are a software tester. Read .fuseraft/artifacts/brief.json for acceptance criteria.
        Call changes_read_latest() to see what was implemented. Write tests, run them with shell_run,
        and write .fuseraft/artifacts/test-report.json before handing off.
        Write HANDOFF TO REVIEWER on its own line when all tests pass.
        Write BUGS FOUND on its own line when tests fail.
      Model: strong
      Plugins: [FileSystem, Shell, Changes]
      FunctionChoice: auto
      TrustScore: 0.8

    - Name: Reviewer
      Instructions: >-
        You are a code reviewer. Read .fuseraft/artifacts/brief.json and .fuseraft/artifacts/test-report.json.
        Verify the implementation against every acceptance criterion. Re-run key commands.
        Emit a JSON review block before your decision keyword.
        Write APPROVED on its own line when satisfied.
        Write REVISION REQUIRED on its own line if changes are needed.
        Write REPLAN REQUIRED on its own line if the brief itself was wrong.
      Model: strong
      Plugins: [FileSystem, Shell, Changes]
      FunctionChoice: auto
      TrustScore: 0.8

  Selection:
    Type: keyword
    DefaultAgent: Planner
    Routes:
      - Keyword: "HANDOFF TO DEVELOPER"
        Agent: Developer
        Validator: RequireBrief
        SourceAgents: [Planner]

      - Keyword: "HANDOFF TO TESTER"
        Agent: Tester
        Validators:
          - RequireAllFilesWritten
          - RequireShellPass
        RequiredCommandPattern: "go build|go test|cargo build|cargo test|npm run build"
        SourceAgents: [Developer]

      - Keyword: "HANDOFF TO REVIEWER"
        Agent: Reviewer
        Validator: TestReportValid
        SourceAgents: [Tester]

      - Keyword: "BUGS FOUND"
        Agent: Developer
        SourceAgents: [Tester]

      - Keyword: "REVISION REQUIRED"
        Agent: Developer
        SourceAgents: [Reviewer]

      - Keyword: "REPLAN REQUIRED"
        Agent: Planner
        SourceAgents: [Reviewer]

      - Keyword: APPROVED
        Agent: Reviewer
        Validators:
          - RequireShellPass
          - RequireReviewJudgement
        SourceAgents: [Reviewer]

  Termination:
    Type: composite
    MaxIterations: 60
    Strategies:
      - Type: regex
        Pattern: '(?m)^\s*APPROVED\s*$'
        AgentNames: [Reviewer]
      - Type: maxiterations
        MaxIterations: 60

  MaxTotalTokens: 2000000
```

---

## What to adjust per task

Not every task needs all of these controls. Use this table to decide what to include:

| Situation | What to add |
|-----------|------------|
| Agent fabricates file writes | `RequireWriteFile` or `RequireAllFilesWritten` on the outbound route |
| Agent hands off without running the build | `RequireShellPass` + `RequiredCommandPattern` |
| Tester writes placeholder tests | `TestReportValid` + `TestAssertionPatterns` |
| Reviewer gives vague approvals | `RequireReviewJudgement` on the `APPROVED` route |
| One agent triggers another agent's route | `SourceAgents` on every route |
| Agent loops between failure modes | Stagnation detection is always on; confirm counter fires at 3 |
| Compaction loses real state | Increase `TriggerTurnCount`; set `Validation.ChangeLogPath` |
| Agent escapes expected directory | Set `Security.FileSystemSandboxPath` |
| Expensive model burning budget mid-loop | Set `MaxTotalTokens`; use a fast model for the brief and compaction |

---

## Design principles

**Instructions are advisory. Validators are mechanical.** Write agent instructions that describe the workflow, but do not rely on them alone to enforce it. A validator that reads a file on disk cannot be fooled by confident prose. An instruction that says "always write the file before handing off" can be.

**Validators + change tracking together are stronger than either alone.** `RequireWriteFile` checks the conversation history for tool call records. `RequireAllFilesWritten` checks the change log for previous turns. Combine them when completeness matters.

**Ground compaction in facts.** Setting `Validation.ChangeLogPath` alongside `Compaction` means fabricated claims are contradicted at compaction time rather than promoted to the summary. This is the single most important systemic protection against long-session drift.

**Stagnation is a signal, not a failure.** When a `ValidatorStuckException` fires, check the injected error messages in the transcript. They tell you exactly which validator failed and what evidence was missing. The fix is usually either a tighter instruction (add explicit tool-call guidance) or a relaxed validator (e.g. adding `ShellFallbackPattern` when the fix is a dependency update).
