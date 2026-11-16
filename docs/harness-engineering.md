---
title: "Harness Engineering"
description: "Harness engineering is designing a team so agents can't advance without real, mechanical evidence, whatever they claim in prose. This page covers the control layers and how to combine them."
---

Harness engineering is designing a team so agents can't advance without real, mechanical evidence, whatever they claim in prose. This page covers the control layers fuseraft gives you and how to combine them into a team that resists hallucinated progress.

---

## The core problem

Agents write confident output that doesn't always match what happened. An agent might say "I wrote the implementation" without ever calling `write_file`, or "all tests pass" without running a command. Without a mechanical layer, the work advances on claims rather than facts.

fuseraft answers with four control layers:

| Layer | What it does |
|-------|-------------|
| **Recorded actions** | Every file write, shell command (with exit code) and git commit is recorded, per turn, on every run. |
| **Gates** | Block a handoff until a file on disk or a recorded action proves the claim. |
| **Fixed routes** | An agent can only hand off on its step's routes; the `handoff` tool rejects anything else. |
| **Corrections and stuck detection** | A failed gate sends the agent a specific correction; repeated failures stop the run instead of letting it loop. |

> **Scope note: REPL vs teams.** Everything below is team machinery, used by `fuseraft run`. `fuseraft repl` doesn't run a team, so none of these layers apply there. The REPL's only anti-fabrication check is a regex in `ReplTurn.ContainsMutationClaim` that catches "I wrote/fixed/updated …" with no write-class tool call in the same turn, plus forced tool calls for identify/locate-style questions. For work where hallucinated progress is a real risk, run it as a team, even a one-agent team with a gated edge to `end`.

---

## Recorded actions

Every run records each `write_file`, `patch_file`, `delete_file`, `shell_run`, `shell_run_script` and `git_commit` to the change log, with no configuration. A file changed indirectly, say by `sed` in a shell command, is recorded too if the agent has read or written it.

Give the `Changes` tool to agents that check others' work, so they can call `changes_read_latest()` to see what actually happened:

```yaml
Tester:
  tools: [FileSystem, Shell, Changes]
```

### Execution state

Alongside the change log, fuseraft keeps `execution-state.json`, rebuilt after every turn and included in each flow agent's context:

| Field | Content |
|-------|---------|
| `ActiveFailures` | Compiler and linker errors from the latest failed build: file, line, code, message. Cleared on the next successful build. |
| `FailedAttempts` | The last 10 attempts recorded as failed this run. |
| `SignificantChanges` | The last 50 file writes, patches, copies and deletes. |
| `Build` | The latest build result: succeeded, exit code, command, errors. |
| `OpenTasks` | Tasks opened but not completed this run. |

It is reset when a new run starts and survives compaction within a run. A Developer reads `ActiveFailures` to know exactly what to fix; a Planner sent back for a replan sees `FailedAttempts` and avoids an approach that already failed. Set `advanced.skipExecutionState: true` on an agent that doesn't need it.

---

## Gates

A gate checks evidence before a handoff goes through. Declare gates on the edge that needs them; the full list is in [Configuration → Gates](configuration.md#gates) and the details in [Gates](validators.md).

**Require the artifact, with its shape.** A brief described in prose but never written doesn't pass; neither does one missing a field the Developer needs.

```yaml
artifacts:
  brief:
    path: .fuseraft/artifacts/brief.json
    shape: { goal: string, files_to_change: [string], acceptance_criteria: [string], verify_command: string }

flow:
  start: Planner
  steps:
    Planner:
      - to: Developer
        requires: [brief]
```

**Require the whole job, and proof it builds.** `filesWritten` closes the loophole where an agent writes one trivial file and hands off. `commandSucceeded` reads the verify command from the brief and checks that it ran and exited 0.

```yaml
    Developer:
      - to: Tester
        requires:
          - filesWritten: brief.files_to_change
          - commandSucceeded: brief.verify_command
```

**Require test results backed by real commands.** `testReport` rejects any FAIL, and any PASS whose command never actually ran.

```yaml
    Tester:
      - to: Reviewer
        requires:
          - testReport: testResults
```

**Require a structured verdict to finish.** `reviewJudgement` stops a run ending on a vague "looks good": the Reviewer must give a PASS/FAIL verdict with evidence per acceptance criterion, and must have run something this turn if it claims PASS.

```yaml
    Reviewer:
      - to: end
        requires: [reviewJudgement]
```

All gates on an edge must pass. When one fails, the agent is told exactly what's missing, and runs again.

---

## Fixed routes

Each agent can only hand off on its own step's edges: the `handoff` tool's `route_keyword` is an enum of them. A Developer can't send work to the Reviewer by skipping the Tester, and a Tester can't approve the run. This replaces the old `SourceAgents` restrictions: role boundaries are the shape of the flow itself.

fuseraft writes each agent's routes, and the gates on each, into its prompt, so agents know the bar before they try.

---

## Corrections and stuck detection

When a gate fails, fuseraft injects a correction naming the missing artifact or action, and the agent runs again. When an agent finishes a turn without handing off, it gets a reminder listing its routes.

Failures are classified, and each kind escalates on its own threshold (see [Failure handling](configuration.md#failure-handling)):

| Kind | Example | Default |
|------|---------|---------|
| Missing evidence | The brief doesn't exist yet | 3 |
| Conflicting evidence | A PASS names a command that never ran | 2 |
| No progress | The agent hands off again without calling any tool | 3 |

When a threshold is reached, the run stops with a stuck error naming the failing gate and its last message, instead of looping. Two optional backstops: `maxConsecutiveContractFailures` caps failures on one edge across kinds, and `maxConsecutiveTurnsWithoutSignal` stops an agent that keeps working without ever handing off.

`onStuck` on an edge calls in a different agent after two consecutive failures: a Tester that can't produce a passing report because the code is broken gets the Developer, not another retry.

```yaml
    Tester:
      - to: Reviewer
        requires:
          - testReport: testResults
        onStuck: Developer
```

---

## Isolated context

Agents default to `sees: handoff`: each gets the task or the previous agent's handoff note, the artifacts it reads, its own recent turns and any correction addressed to it, and never another agent's transcript. That stops one agent's mistaken claim from becoming the next agent's premise. Artifacts carry the durable facts; the handoff carries the instructions. Use `sees: conversation` only for agents that genuinely need the discussion.

---

## Compaction grounding

When compaction summarizes old turns, fabricated claims in them could otherwise be baked into the summary. fuseraft passes the change log to the summarizer as ground truth, so a claim like "I wrote `src/main.rs`" is corrected if the log records no such write.

```yaml
advanced:
  compaction:
    triggerTurnCount: 30
    keepRecentTurns: 8
    mode: lossless
```

`lossless` mode goes further: it rebuilds context from the evidence graph and gate results, with no LLM call at all. Set `triggerTurnCount` high enough that implementation is mostly done before compaction fires; 30–50 suits a four-agent team.

---

## Run limits

| Setting | What it does |
|---------|-------------|
| `limits.maxTurns` | Hard cap on agent turns (default 40). |
| `limits.maxTokens` | Hard cap on total tokens; the run is saved and can be resumed. |
| `advanced.contextBudget` | Per-agent input budget that compacts instead of stopping. |

---

## Sandbox enforcement

Restrict file and shell access to a directory tree. Paths outside it are denied at the tool level, whatever the instructions say:

```yaml
security:
  fileSystemSandboxPath: /workspace/project
```

Path arguments are resolved canonically; anything outside the tree returns `[DENIED] '<path>': <reason>`. System binary prefixes (`/usr/`, `/bin/`, `/sbin/`, `/lib/`, `/lib64/`, `/opt/`, `/nix/`, `/run/current-system/`, `/snap/`) are exempt so agents can run standard tools; `/etc/` is not. `~/.fuseraft` is always accessible, since agents read and write run artifacts there. For stronger isolation, use the `CodeExecution` tool with a Docker sandbox.

---

## A complete hardened team

```yaml
team: HardenedSoftwareTeam

models:
  strong: { modelId: gpt-4o }
  fast:   { modelId: gpt-4o-mini }

artifacts:
  brief:
    path: .fuseraft/artifacts/brief.json
    shape:
      goal: string
      files_to_change: [string]
      acceptance_criteria: [string]
      verify_command: string
  testResults:
    path: .fuseraft/artifacts/test-report.json
    shape:
      results:
        - criterion: string
          status: PASS|FAIL
          command: string

agents:
  Planner:
    model: strong
    tools: [FileSystem, Search]
    writes: [brief]
    instructions: Read the codebase, decide what must change, and write the brief.
  Developer:
    model: strong
    tools: [FileSystem, Shell, Git, Changes]
    reads: [brief]
    instructions: Implement every file in the brief, then run its verify_command.
    advanced: { functionChoice: required }
  Tester:
    model: strong
    tools: [FileSystem, Shell, Changes]
    reads: [brief]
    writes: [testResults]
    instructions: See what was implemented, write and run tests, and record one result per acceptance criterion.
    advanced: { functionChoice: required }
  Reviewer:
    model: strong
    tools: [FileSystem, Shell, Changes]
    reads: [brief, testResults]
    instructions: Verify the change against every acceptance criterion and re-run the key commands yourself.

flow:
  start: Planner
  steps:
    Planner:
      - to: Developer
        requires: [brief]
    Developer:
      - to: Tester
        requires:
          - filesWritten: brief.files_to_change
          - commandSucceeded: brief.verify_command
      - to: Planner
        when: the brief is wrong or incomplete
    Tester:
      - to: Reviewer
        when: every criterion passes
        requires:
          - testReport: testResults
        onStuck: Developer
      - to: Developer
        when: a test fails
    Reviewer:
      - to: end
        when: every criterion is verified
        requires: [reviewJudgement]
      - to: Developer
        when: the code needs fixing
      - to: Planner
        when: the brief itself was wrong

limits:
  maxTurns: 60
  maxTokens: 2000000

security:
  fileSystemSandboxPath: /workspace

advanced:
  compaction:
    triggerTurnCount: 30
    keepRecentTurns: 8
    model: fast
```

Notice what the instructions don't say: where the brief lives, what it must contain, which keyword to write, or what each handoff requires. fuseraft generates all of that from the team file.

---

## What to add for which problem

| Problem | Add |
|---------|-----|
| An agent claims files it never wrote | `filesWritten: brief.files_to_change`, or `anyFileWritten` |
| An agent hands off without building | `commandSucceeded: brief.verify_command` |
| A tester invents passing results | `testReport: testResults` |
| A reviewer approves vaguely | `reviewJudgement` on the edge to `end` |
| An artifact is missing fields | A `shape` on the artifact, and the artifact in `requires` |
| Two agents loop forever | `maxRevisits` on the back-edge; `onStuck` where another agent can fix it |
| Stuck detection fires too early or late | `advanced.failureHandling` thresholds |
| Compaction loses real state | Raise `triggerTurnCount`; use `lossless` mode |
| An agent escapes the project | `security.fileSystemSandboxPath` |
| A run burns budget in a loop | `limits.maxTokens`; a cheaper model for compaction |

---

## Design principles

**Instructions are advisory; gates are mechanical.** Describe the work in instructions, but don't rely on them to enforce it. A gate that reads a file on disk can't be talked out of its answer.

**Declare each fact once.** Put paths and shapes in `artifacts`, routes in `flow`. fuseraft tells every agent what it needs from there, so nothing drifts between a prompt and a check.

**Gate on what ran, not on what was said.** Prefer `commandSucceeded` and `testReport`, which read the recorded actions, over anything that trusts an agent's reply.

**A stuck error is a signal.** The correction messages in the transcript say exactly which gate failed and what was missing. The fix is usually a clearer instruction or a different gate, such as `anyFileWritten` with a `fallback` command when the fix is a dependency update.
