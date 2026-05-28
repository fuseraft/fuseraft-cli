---
name: debug-session
description: Diagnose a failing, stuck, or unexpectedly terminated fuseraft run session. Trigger when the user reports that a session looped, stopped early, threw a ValidatorStuckException, hit the iteration cap, crashed, or produced unexpected output.
---

# Debug Session

Examine a session checkpoint, its event log, and any crash dumps to identify exactly why the run failed or stalled, then recommend a concrete fix.

## When to Use

Use this skill when:
- A `fuseraft run` session stopped with an error or unexpected termination
- An agent looped without making progress (same validator error repeating)
- A `ValidatorStuckException` was raised
- The session hit `MaxIterations` without completing the task
- The session stopped with a budget or circuit-breaker error
- The user wants to understand what happened in a completed or interrupted run

Do **not** use this skill for REPL sessions (`fuseraft repl`) — those use `repl_session_read_event_log` directly.

## Workflow

### Step 1: Identify the Session

If the user provided a session ID, use it. Otherwise:

```bash
fuseraft sessions --all
```

Pick the most recent incomplete session, or ask the user to confirm which one they mean.

The session checkpoint is at `~/.fuseraft/sessions/<sessionId>.json`.

### Step 2: Read the Checkpoint

Call `read_file` on `~/.fuseraft/sessions/<sessionId>.json`. The checkpoint contains:

| Field | What to look at |
|-------|-----------------|
| `Task` | The original goal — use this as the expected outcome anchor |
| `ConfigPath` | The config that was used — read it next |
| `IsComplete` | `false` means the session was interrupted or stuck |
| `Messages` | The full turn history — read from the end backward |
| `StructuredTask` | `Phase`, `ActiveTargets` — shows what the orchestrator believed was in progress |
| `MagenticState` | Non-null for Magentic runs — check `StallCount` and `ResetCount` |
| `StateHistory` | Non-null for Graph runs — shows which node was active at each turn |

**Read the last 5–10 messages.** For each message look at:
- `AgentName` — which agent spoke
- `Content` — what they said; look for validator error injections (lines starting with `Handoff blocked:` or `APPROVED blocked:`)
- `TurnIndex` — spot large gaps (compaction may have fired)
- `IsCompactionSummary: true` — if present, a compaction happened here

### Step 3: Read the Config

Call `read_file` on the `ConfigPath` from the checkpoint. Note:
- Which selection strategy is used (`keyword`, `statemachine`, `magentic`, `graph`)
- The `MaxIterations` cap and how many turns the session ran
- `MaxTotalTokens` — compare against cumulative token counts in the messages
- Which validators are on which routes
- `FailureHandling` presence (missing on a 3+ agent pipeline is a common cause of infinite reinstruct loops)
- `Compaction` settings — if present, check `TriggerTurnCount` vs. the turn count at failure

### Step 4: Read the Events Log

The events log for the working directory is at `.fuseraft/logs/events.jsonl` (relative to the directory where `fuseraft run` was called — check `ConfigPath` to infer the project root).

Call `read_file` on it (or `shell_run("tail -n 100 .fuseraft/logs/events.jsonl")`). Event types to look for:

| Event type | What it means |
|------------|---------------|
| `validator_blocked` | A route was blocked by a validator — note the validator name and turn |
| `validator_stuck` | `ValidatorStuckException` threshold reached (3 consecutive blocks) |
| `tool_blocked` | Sandbox or injection detector denied a tool call |
| `session_started` / `session_completed` | Bookends for normal runs |
| `compaction_fired` | Compaction triggered — check if context loss may have caused drift |
| `budget_exceeded` | `MaxTotalTokens` was hit |
| `circuit_breaker_open` | 5 consecutive model API failures |

### Step 5: Check for Crash Dumps

```bash
ls -lt ~/.fuseraft/crashdump/ | head -10
```

If a crash dump exists for the session's timeframe, call `read_file` on the most recent one. Look at:
- `ExceptionType` and `Message` — the C# exception that caused the crash
- `AgentName` and `TurnIndex` — where in the run it happened
- `StackTrace` — needed only for runtime bugs; skip for logic/config issues

### Step 6: Diagnose

Match the evidence to a root cause using this table:

| Symptom | Root cause | Fix |
|---------|------------|-----|
| Same validator error 3× in a row → `validator_stuck` event | Agent can't satisfy the validator (missing tool call, wrong keyword, fabricating output) | Tighten agent instructions: name the exact tool to call and the exact keyword to emit; or add `FailureHandling` to reroute after N failures |
| Agent emits the right keyword but validator still blocks | Validator requires evidence that wasn't produced this turn (e.g. `RequireShellPass` but `shell_run` was in a prior turn) | Clarify in instructions that the required tool call must happen in the same turn as the handoff keyword |
| Agent emits no routing keyword | Instructions don't match the keyword exactly, or model ignored instructions | Check instructions for exact keyword text; set `FunctionChoice: required` if the agent should always call a tool |
| Session stopped at `MaxIterations` | Pipeline needs more turns than allowed | Raise `MaxIterations`; or add `FailureHandling` to detect loops early |
| `budget_exceeded` event | Token budget too low for the task | Raise `MaxTotalTokens`; or enable compaction to reduce context size |
| `circuit_breaker_open` event | Model API is returning 5+ consecutive errors | Check API key env var, provider endpoint, and model ID; look at `.fuseraft/logs/provider_errors.jsonl` |
| Compaction fired and agent lost track of what was done | Compaction mode `llm` hallucinated progress | Switch to `intent` mode (requires `ChangeTracking`) or `lossless` mode (requires `EvidenceStore` + state machine) |
| `StallCount` or `ResetCount` high in `MagenticState` | Magentic orchestrator repeatedly re-planned without making progress | Lower the stall threshold or add more concrete subtask hints in the initial task string |
| `StateHistory` shows same node repeating in Graph run | Back-edge loop without a progress condition | Add a `MaxPhaseIterations` guard on the looping node, or change the back-edge condition |
| Tool call denied (`tool_blocked`) | Agent's `TrustScore` < 0.60 (Ring 3 — no write/shell access) | Raise `TrustScore` to ≥ 0.60 for agents that need write access |

### Step 7: Report and Recommend

State clearly:
1. **What failed** — the exact turn index, agent name, and error text
2. **Why** — the root cause from the table above
3. **How to fix** — the specific config or instruction change

If the session can be resumed after the fix, tell the user:

```bash
fuseraft run --resume <sessionId> --config <configPath>
```

If the checkpoint is too corrupted or the task needs to restart:

```bash
fuseraft sessions --delete <sessionId>
fuseraft run --config <configPath> "<task>"
```

## References

- Session checkpoint format: `docs/sessions.md`
- Validator error messages: `docs/validators.md`
- Governance events (circuit breaker, sandbox denials): `docs/governance.md`
- Compaction modes: `docs/sessions.md#conversation-compaction`
- Failure handling config: `docs/configuration.md#failure-handling`
