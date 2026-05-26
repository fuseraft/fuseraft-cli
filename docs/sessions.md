# Sessions

## REPL sessions

REPL sessions (`fuseraft repl`) are automatically saved after every user turn to `~/.fuseraft/repl-sessions/repl-<id>.json`. No configuration is needed — every session is resumable by default.

**Starting and resuming**

```bash
# Start a new session — session ID is shown in the header
fuseraft repl

# List your resumable sessions from inside the REPL
/sessions

# Resume a specific session by ID
fuseraft repl --resume a87569bcd7b0
```

When resuming:

- The full conversation history (text, tool calls, tool results) is restored.
- The system prompt is refreshed to pick up any new memories or `AGENTS.md` changes.
- The turn counter continues from where it left off.

**Branching sessions**

A session can be forked at any point to create a diverging copy of the conversation from the current turn.

```bash
# Inside the REPL — snapshot to a new ID, stay in the current session
/fork

# Fork and immediately switch to it (original is already auto-saved)
/fork switch
```

`/fork` writes a complete snapshot — conversation history, plan queue, and halted-step state — to a new session ID and saves it immediately. The running session is not affected. Resume the fork later:

```bash
fuseraft repl --resume <fork-id>
```

`/fork switch` does the same but mutates the live session to become the fork: future auto-saves, events, and turn tracking all use the new ID. The original session is left on disk at the branch point (the last turn's auto-save).

All forks appear in `/sessions` and `fuseraft repl --resume` like any other saved session.

**Rewinding**

Use `/conversation` to list all turns in memory with their 1-based indices, then `/rewind` to truncate history to a chosen point:

```
/conversation           # see turn numbers and previews
/rewind 3               # keep turns 1–3, discard the rest
/rewind -1              # drop the last turn
/rewind 0               # clear all turns (like /clear)
```

Rewind updates the turn counter, resets plan state, and adjusts token tracking. Out-of-range values are clamped silently — `/rewind -99` is always safe.

A common pattern: fork to preserve the current state, then rewind in the fork to explore a different direction from an earlier point.

**Session files**

REPL snapshots are stored at `~/.fuseraft/repl-sessions/repl-<id>.json` with owner-only permissions (Unix mode 0600). Each file contains:

| Field | Description |
|-------|-------------|
| `SessionId` | 12-character hex identifier shown in the REPL header |
| `ModelId` | Model used for the session |
| `Cwd` | Working directory when the session was started |
| `StartedAt` | UTC timestamp when the session was first created |
| `LastUpdatedAt` | UTC timestamp of the most recent save |
| `TurnIndex` | Number of completed turns |
| `History` | Full serialized conversation (text, function calls, function results) |

Sessions are never automatically deleted. Remove old ones manually from `~/.fuseraft/repl-sessions/` when no longer needed.

---

## Session self-inspection (REPL agents)

REPL agents can inspect their own session and diagnostic logs using the built-in `repl_session_*` tools. The current session ID, start time, snapshot path, and event log path are also injected into the system prompt so the agent can orient itself immediately.

**Tools available to the agent:**

| Tool | What it returns |
|------|----------------|
| `repl_session_current` | Session ID, model, start time, working directory, snapshot path, and all log file locations |
| `repl_session_list` | All saved sessions newest-first — the active session is marked `◄ current` |
| `repl_session_read_event_log` | Entries from `repl_events.jsonl` filtered to a session (current by default) |
| `repl_session_read_log` | Tail of any diagnostic log: `repl_events`, `events`, `provider_errors`, or `app` |

**Log files written per working directory:**

| Log name | Path | Contents |
|----------|------|----------|
| `repl_events` | `.fuseraft/logs/repl_events.jsonl` | REPL lifecycle events (session start/end, each turn) tagged with session ID |
| `events` | `.fuseraft/logs/events.jsonl` | Orchestration events from `fuseraft run` sessions |
| `provider_errors` | `.fuseraft/logs/provider_errors.jsonl` | Provider API errors and retry attempts |
| `app` | `.fuseraft/logs/app.log` | Application diagnostic log |

All REPL events are tagged with the session ID (`session` field in the JSONL), so the agent can distinguish events from different sessions in the same log file.

---

## Orchestration sessions (`fuseraft run`)

## How sessions work

A session begins when you run `fuseraft run`. The orchestrator:

1. Generates an 8-character hex session ID (e.g. `a3f92c1d`)
2. Saves a checkpoint to `~/.fuseraft/sessions/<sessionId>.json` after every agent turn
3. Streams responses to the terminal as they arrive
4. Marks the session complete when the termination strategy fires

If the run is interrupted for any reason (network error, `Ctrl+C`, process kill), the checkpoint is already saved up to the last completed turn.

---

## Resuming a session

```bash
# Show a list of incomplete sessions and choose one
fuseraft run --resume

# Resume a specific session by ID
fuseraft run --resume a3f92c1d
```

The orchestrator re-injects the prior history into the group chat and continues from the next agent turn. The model receives the full conversation context (or a compacted summary if compaction fired).

You can resume with a different config or task — the session ID is what ties the checkpoint to the run, not the config file.

---

## Session files

Sessions are stored at `~/.fuseraft/sessions/<sessionId>.json` with owner-only read/write permissions (Unix mode 0600).

**Checkpoint fields:**

| Field | Description |
|-------|-------------|
| `SessionId` | 8-character hex identifier |
| `Task` | Original task string |
| `ConfigPath` | Path to the orchestration config used |
| `Messages` | All agent messages produced so far |
| `StartedAt` | UTC timestamp when session was first created |
| `LastUpdatedAt` | UTC timestamp of the most recent update |
| `IsComplete` | `true` once the session has run to completion |
| `ResumeExecutorId` | Hint for `GraphOrchestrator` identifying which node was active when the session was last checkpointed; `null` for non-`GraphOrchestrator` sessions |
| `MagenticState` | `MagenticCheckpointState` snapshot (`CurrentPlan`, `RoundIndex`, `StallCount`, `ResetCount`, `AwaitingPlanReview`) used to resume the Magentic inner loop; `null` for non-Magentic sessions |
| `StateHistory` | Ordered list of `AgentState` snapshots produced during the session; `null` for non-`GraphOrchestrator` sessions |
| `StructuredTask` | `TaskModel` snapshot (`Goal`, `Constraints`, `ActiveTargets`, `Phase`) injected into agent context at session start. Populated automatically from the task string; can be enriched by the orchestrator as agents write files. |

**Message fields:**

| Field | Description |
|-------|-------------|
| `AgentName` | Agent name, or `"Human"` for HITL injections |
| `Content` | Text content of the response |
| `Timestamp` | UTC timestamp |
| `TurnIndex` | Zero-based turn index within the session |
| `Role` | `"assistant"` for agent turns, `"user"` for HITL injections |
| `Usage` | Token counts and estimated cost (null for HITL turns) |
| `IsCompactionSummary` | `true` if this message is a compaction summary |

---

## Managing sessions

```bash
# List all incomplete sessions
fuseraft sessions

# List all sessions including completed
fuseraft sessions --all

# Delete a session by ID
fuseraft sessions --delete a3f92c1d

# Purge all completed sessions
fuseraft sessions --delete all
```

---

## Human-in-the-loop (HITL)

Run with `--hitl` to pause after every agent turn and review before continuing:

```bash
fuseraft run --hitl "Refactor the payment module"
```

After each turn you see:

```
[HITL] Press Enter to continue, type a message to inject it, or 'q' to quit:
```

| Input | Effect |
|-------|--------|
| Enter | Continue to the next agent turn |
| Any text | Inject that text as a user message, then continue |
| `q` | Save checkpoint and exit cleanly |

Injected messages are saved in the session checkpoint with `Role: "user"` and `AgentName: "Human"`. They are re-injected on resume so the conversation context is complete.

---

## Token tracking and cost estimation

Token counts and estimated costs are tracked per turn and accumulated for the session.

Token counts appear in `--verbose` mode and in the `--output` transcript. The session summary table shows input and output tokens per agent.

**Per-turn data:**

| Field | Description |
|-------|-------------|
| `InputTokens` | Tokens in the model's input (prompt + history) |
| `OutputTokens` | Tokens in the model's output |

**Token budget:** Set `MaxTotalTokens` in the config to stop the run before the next turn if the cumulative token count (input + output combined) exceeds the limit. The session is saved and can be resumed later.

```yaml
MaxTotalTokens: 200000
```

Token counts are always exact — reported directly by the provider API.

---

## Conversation compaction

Long-running sessions will eventually exceed the model's context window. Compaction automatically reduces old turns to keep the session alive indefinitely.

```yaml
Compaction:
  TriggerTurnCount: 30
  KeepRecentTurns: 8
  Mode: intent      # or "llm" (default), "lossless", "hybrid", or "window"
```

**How it works (llm / lossless / hybrid / intent):**

1. When the assistant-turn count reaches `TriggerTurnCount`, compaction fires.
2. All turns except the most recent `KeepRecentTurns` are replaced by a single context message.
3. The checkpoint is saved with the compacted history.
4. The session continues with the context message providing background for older work.

`TriggerTurnCount` must be greater than `KeepRecentTurns`. An error is thrown at startup if this constraint is violated.

**How it works (window):**

1. After each checkpoint save, the estimated token count of the full history (characters ÷ 4) is compared against `TokenBudget`.
2. When over budget, the oldest user+assistant pairs are dropped one at a time until the count falls within the budget.
3. No LLM call is made and no summary message is injected — only the tail of the conversation is kept.
4. Prior compaction summaries (from an earlier `llm`/`lossless`/`hybrid` run) are pinned and never dropped.

**Compaction modes**

| Mode | Behavior |
|------|----------|
| `llm` | Default. An LLM call summarises the compacted turns using conversation history and `changes.json`. Requires a model. |
| `lossless` | Reconstructs context from durable disk artifacts only — the evidence graph, all contract evaluations, and the current state machine position. No LLM call is made. No hallucination is possible because every fact is read directly from disk. Falls back to `llm` when no state machine strategy with evidence store is active. |
| `hybrid` | Prepends the lossless reconstruction before the LLM summary. Agents get authoritative ground-truth (what was actually done) plus narrative context (how it unfolded). |
| `window` | Sliding window: drops the oldest user+assistant pairs until the estimated token count is within `TokenBudget` (default 80,000). No LLM call; no summary message is injected. `TriggerTurnCount` and `KeepRecentTurns` are ignored. |
| `intent` | Reconstructs context from the intent log (`intents.json`). Produces a `✓`/`✗`/`⧖` per-operation block covering every tracked tool call in the compacted range. No LLM call; no hallucination. Requires `ChangeTracking` to be configured. Falls back to `lossless` then `llm` when the intent log is unavailable. Works with any selection strategy. |

**Intent compaction** is the recommended mode for most sessions when `ChangeTracking` is configured. It doesn't require a state machine or evidence store. The reconstructed context looks like:

```
[INTENT-DERIVED RECONSTRUCTION — covers turns 1–20]

OPERATIONS (chronological):
  ✓ write_file → "src/api/users.go" (turn 3, Developer)
  ✗ patch_file → "src/api/auth.go" — oldText not found… (turn 4, Developer)
  ✓ shell_run → "go test ./..." (turn 5, Tester)

RESUMPTION NOTE: History compacted from intent log — deterministic ground truth.
Do not re-execute operations marked ✓ (applied).
Operations marked ✗ (failed) should be retried if the task requires them.
```

**Lossless compaction** is recommended for `statemachine` sessions with an `EvidenceStore`. The reconstructed context message looks like:

```
[CONTEXT RECONSTRUCTION — derived from durable evidence, not summarised by LLM]

STATE MACHINE POSITION: Testing

CONTRACT STATUS:
  ✓ BriefExists
  ✓ ImplementationComplete
  ✗ TestsValid
      Reason: test-report.json has 2 FAIL results

RECENT EVIDENCE (newest first):
  [CommandRun] go test ./... → exit 1 (turn 14, agent Developer)
  [FileWrite] wrote src/api/users.go (turn 13, agent Developer)
  ...

RESUMPTION NOTE: History compacted. Continue from state 'Testing'.
Satisfy all ✗ contracts before emitting a transition signal.
```

This is structurally superior to an LLM summary: the state machine position cannot drift, contract status is re-evaluated fresh at compaction time, and every evidence item is a real disk record.

**Change log grounding (`llm` and `hybrid` modes):** When `ChangeTracking` or `Validation.ChangeLogPath` is configured, the compactor reads `changes.json` at compaction time and injects it into the LLM summary prompt as authoritative ground truth. Fabricated progress reports are corrected before they are carried forward as fact.

**Compaction model:** By default, the first agent's model is used for generating summaries (`llm` and `hybrid` only). Override with `Compaction.Model`:

```yaml
Compaction:
  TriggerTurnCount: 50
  KeepRecentTurns: 10
  Mode: hybrid
  Model:
    ModelId: gpt-4o-mini
```

**Window mode example:**

```yaml
Compaction:
  Mode: window
  TokenBudget: 60000   # optional — defaults to 80000
```

`TriggerTurnCount` and `KeepRecentTurns` have no effect in `window` mode and can be omitted.

**Cost accounting:** The summary message's cumulative cost includes all the turns it replaced, so budget tracking remains accurate after compaction. `lossless`, `intent`, and `window` modes incur no LLM cost at compaction time.

---

## Saving transcripts

Write the full session transcript to a Markdown file:

```bash
fuseraft run --output my-session.md "Build a CLI todo app"
```

The file includes every agent turn with agent name, timestamp, content, and per-turn token/cost data. Useful for review, sharing, or archiving completed sessions.
