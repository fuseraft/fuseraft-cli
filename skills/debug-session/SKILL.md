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
- `agentName` — which agent spoke
- `Content` — what they said; look for gate failure corrections (lines starting with `Handoff blocked:`, `Approval blocked:`, `MISSING ARTIFACT` or `EVIDENCE AUDIT REQUIRED`)
- `TurnIndex` — spot large gaps (compaction may have fired)
- `IsCompactionSummary: true` — if present, a compaction happened here

### Step 3: Read the Config

Call `read_file` on the `ConfigPath` from the checkpoint. Note:
- The flow: `steps` (and which gates are on which edges) or a `pattern`
- `limits.maxTurns` and how many turns the session ran
- `limits.maxTokens` — compare against cumulative token counts in the messages
- `advanced.failureHandling` presence (missing on a 3+ agent pipeline is a common cause of long reinstruct loops)
- `advanced.compaction` settings — if present, check `triggerTurnCount` vs. the turn count at failure

### Step 4: Read the Events Log

The events log for the working directory is at `.fuseraft/logs/events.jsonl` (relative to the directory where `fuseraft run` was called — check `ConfigPath` to infer the project root).

Call `read_file` on it (or `shell_run("tail -n 100 .fuseraft/logs/events.jsonl")`). Event types to look for:

| Event type | What it means |
|------------|---------------|
| `validator_blocked` | A route was blocked by a validator — note the validator name and turn |
| `validator_stuck` | `ValidatorStuckException` threshold reached (3 consecutive blocks) |
| `tool_blocked` | Sandbox or injection detector denied a tool call |
| `session_started` / `session_completed` | Bookends for normal runs |
| `compaction` | Compaction triggered — check if context loss may have caused drift; repeated `compaction` events with no progress between them signal thrashing |
| `budget_exceeded` | `limits.maxTokens` was hit |
| `circuit_breaker_open` | 5 consecutive model API failures |
| `context_budget_warn` | Agent's cumulative input tokens crossed `warnAt` |
| `context_budget_cutover` | Compaction fired due to budget: cumulative tokens ≥ `cutoverAt`, or `reason: "single_turn_limit"` if a single turn exceeded `maxSingleTurnInputTokens` |
| `keyword_not_found` | State machine turn with no matching routing signal — repeated occurrences across compaction boundaries mean a silently stuck agent |

### Step 5: Check for Crash Dumps

```bash
ls -lt ~/.fuseraft/crashdump/ | head -10
```

If a crash dump exists for the session's timeframe, call `read_file` on the most recent one. Look at:
- `ExceptionType` and `Message` — the C# exception that caused the crash
- `agentName` and `TurnIndex` — where in the run it happened
- `StackTrace` — needed only for runtime bugs; skip for logic/config issues

### Step 6: Diagnose

Match the evidence to a root cause using this table:

| Symptom | Root cause | Fix |
|---------|------------|-----|
| Same gate failure 3× in a row → `validator_stuck` event | Agent can't satisfy the gate (missing tool call, wrong file, fabricated output) | Tighten agent instructions: name the exact tool to call and file to write; add `onStuck` on the edge so another agent can fix it |
| Agent hands off but a turn-scoped gate still blocks | The check needs evidence from this turn (e.g. `reviewJudgement` needs a shell run in the same turn as a PASS verdict) | Clarify in instructions that the required tool call must happen in the same turn as the handoff |
| Agent finishes turns without handing off | The model ignored the generated routes, or keeps working | Set `advanced.functionChoice: required` if the agent should always act; add `advanced.failureHandling.maxConsecutiveTurnsWithoutSignal` |
| Session stopped at `limits.maxTurns` | Pipeline needs more turns than allowed | Raise `limits.maxTurns`; or add `advanced.failureHandling` to detect loops early |
| `budget_exceeded` event | Token budget too low for the task | Raise `limits.maxTokens`; or enable compaction to reduce context size |
| `circuit_breaker_open` event | Model API is returning 5+ consecutive errors | Check API key env var, provider endpoint, and model ID; look at `.fuseraft/logs/provider_errors.jsonl` |
| Compaction fired and agent lost track of what was done | Compaction mode `llm` summarized away real progress | Switch `advanced.compaction.mode` to `lossless` (Steps flows) or `intent` |
| Agent repeats same work after compaction (duplicate commits, re-running tests) | The summary gave no "don't redo ✓ work" anchor | Use `advanced.compaction.mode: lossless` or `intent`, which list completed operations from the change log |
| `StallCount` or `ResetCount` high in `MagenticState` | Magentic orchestrator repeatedly re-planned without making progress | Lower the stall threshold or add more concrete subtask hints in the initial task string |
| The same two agents hand work back and forth | A back-edge with no exit condition | Add `maxRevisits` to the back-edge, or `onStuck` on the forward edge |
| `keyword_not_found` events repeat across multiple compaction cycles for the same agent | Agent completed work but never called `handoff()` — "silent stuck" case. Loop-warning counter resets on compaction so standard warnings do not accumulate | Add `failureHandling.maxConsecutiveTurnsWithoutSignal` (e.g. `8`) — this counter lives in strategy state and survives compaction; will escalate to HITL after N silent turns |
| Repeated `compaction` events with no agent progress between them | Post-compaction budget thrashing: first turn after compaction exceeds `cutoverAt`, triggering another immediate compaction | Raise `cutoverAt`, lower the per-agent token usage (use `sees: handoff`, or `advanced.contextWindow.textOnly` + `maxTurnAge` on the expensive agent), or add `maxSingleTurnInputTokens` to catch single-turn explosions |
| Single turn burned nearly all of `limits.maxTokens` | Agent read many large files in one turn; no per-turn ceiling was set | Add `contextBudget.maxSingleTurnInputTokens` — compaction fires before the *next* turn when a single turn exceeds this, preventing inherited bloat |
| Tool call denied (`tool_blocked`) | Agent's `advanced.trustScore` < 0.60 (Ring 3 — no write/shell access) | Raise `advanced.trustScore` to ≥ 0.60 for agents that need write access |

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
