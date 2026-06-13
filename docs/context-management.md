# Context Management

Context is the most important resource in a long-running agent session. Every token an agent
sees costs money and time; everything it misses is a potential hallucination or regression.
fuseraft manages context through a unified assembly pipeline that fires every agent turn, plus
several independent layers that augment it:

```
Session start
  └─ Auto-injection              → runtime environment + .gitignore (always on, no config)
  └─ Layer 1: Context Store      → files imported before the session

Each agent turn — ContextAssemblyPipeline (always on)
  └─ Intent analysis             → keywords, PascalCase symbols, failure patterns extracted from task
  └─ Memory block                → per-agent memories ranked by relevance (not alphabetical)
  └─ Knowledge retrieval         → ADRs, graph nodes, repository memory, session findings (KnowledgeWeight)
  └─ Graph expansion             → one-hop symbol neighbours for KnowledgeWeight.High agents
  └─ Context window filter       → per-agent history slice (ContextWindow config)
  └─ Session context injection   → session summary prepended (if present)
  └─ Artifact offloading         → tool results > 40k chars stored to disk; stub replaces inline (always on)
  └─ Task Reminder               → task repeated at recency end when context > 2 000 chars (primacy+recency sandwich)

History too long
  └─ Compaction                  → replace old turns with a summary + tool-call trace
  └─ Context Budget              → token-based compaction trigger per agent

After each run
  └─ Visualization               → HTML chart of cumulative input tokens per agent
```

The pipeline is the single entry point for every agent invocation across all orchestrator
types — `AgentOrchestrator` (sequential, parallel, verifier), `MagenticOrchestrator`
(participant agents), and `GraphOrchestrator` (node executors, parallel nodes, recovery
agents) all call `AssembleAsync` identically. Most layers are always-on; use `KnowledgeWeight`
on an agent's config to tune retrieval depth.

> **Upgrading from `EnableMemory`:** `EnableMemory: true` is deprecated. Memory is now
> runtime-injected by the pipeline every turn and ranked by relevance to the current task.
> Remove `EnableMemory` from your agent configs — it will be ignored in a future release.

---

## Automatic runtime injection

Before any configurable layer runs, fuseraft injects two blocks into every agent's system prompt automatically — no configuration required.

**Runtime environment** — OS, CPU architecture, shell, working directory, and current date/time:

```
## Runtime Environment
OS: Linux
Architecture: x64
Shell: /usr/bin/bash
Working directory: /home/dev/my-project
Date/time: 2026-05-27 10:30:00 -05:00 (America/Chicago)
```

This prevents agents from spending tool calls probing for environment details they can read directly from their instructions.

**`.gitignore`** — the project's `.gitignore` (capped at 100 lines) is read from the session working directory and injected so agents know which paths to avoid writing to without discovering the file via tool calls. Omitted silently when no `.gitignore` is present.

---

## Layer 1: Context Store

The context store pre-loads static reference files into `.fuseraft/context/` before a session
starts. Every agent sees a compact index block at the top of its system prompt listing what is
available, and can access the full content with `read_file`.

```yaml
# No config required — populated by CLI before running:
#   fuseraft context add ~/docs/schema.sql --name db-schema
#   fuseraft context add ~/specs/          --name specs
#   fuseraft context add ~/docs/design.pdf --name design   # text extracted automatically
```

**When to use:** Database schemas, API specs, architecture docs, slide decks, spreadsheets,
task briefs — anything too large to paste into the task argument but that agents should know
exists from turn one.

**Binary documents:** When you import a `.pdf`, `.docx`, `.pptx`, or `.xlsx` file, fuseraft
extracts the plain text at import time and stores a `.txt` file instead. Agents access it
via `read_file` with no extra plugin. For documents found *during* a session — or when you
need individual Excel sheets — use the [`Document` plugin](plugins.md#document) directly.

See [Context Store](context-store.md) for the full CLI reference.

---

## Layer 2: Persistent Memory (pipeline-injected)

Every agent's persistent memory store is loaded and ranked by relevance before each turn.
Memories survive between sessions — they accumulate over time, giving agents a working
knowledge of the project.

The memory block is automatically injected into the agent's system prompt by the pipeline.
No per-agent config is required.

```
MEMORY — facts recalled from prior sessions:
[feedback] preferred-test-runner: Use `go test -race ./...` for all test runs.
[fact] auth-middleware: The auth middleware was rewritten in v2.3 — do not touch the legacy layer.
```

**Ranking:** Memories are now ranked by relevance to the current task — entries whose name,
description, or body contain keywords or symbols extracted from the task score higher. Type
priority (`feedback` > `project` > `user` > `reference`) is used as a tiebreaker.
The prompt block is capped at 8,000 characters; entries that do not fit are silently dropped.

**Storage locations:**

| Context | Path |
|---|---|
| REPL sessions | `~/.fuseraft/memory/repl/` |
| Orchestration agents | `~/.fuseraft/memory/agents/{AgentName}/` |

**Memory scoping:** In a project directory that has `.fuseraft/`, only memories saved in that
directory are loaded. Directories without `.fuseraft/` fall back to all global memories.

**REPL:** Memory is always active in the REPL — no config flag needed. Memories are extracted
automatically at the end of each session and scoped to the working directory via
`.fuseraft/memory/sessions/{session_id}/memory_refs.json`. Use `/memory` commands to inspect or delete them.

> **Deprecated:** `EnableMemory: true` on an agent config is no longer needed. Memory is now
> injected at runtime by the pipeline regardless of this flag.

See [Configuration — Memory](configuration.md#memory) for the full field reference.

---

## Layer 2b: Memory provider (per-turn)

The `Memory:` top-level config key activates a live provider that runs pre- and post-turn hooks around every agent turn. The provider can persist the accumulated history after each turn and supply additional context from external sources.

```yaml
Memory:
  Provider: local    # or "webhook"
```

Two built-in providers are available:

- **`local`** — refreshes the file-backed memory store every turn. Useful when another process is writing new memories during the session.
- **`webhook`** — delegates load and save to an HTTP endpoint you control (vector store, knowledge graph, managed memory service).

See [Configuration — Pluggable memory provider](configuration.md#pluggable-memory-provider) for the full reference.

---

## Layer 3: ContextWindow (per-agent history filter)

By default every agent receives the full accumulated conversation history, including tool-call
frames and tool-result messages from all prior turns. In a long multi-agent session this can
reach hundreds of thousands of tokens — most of it irrelevant to late-stage agents.

`ContextWindow` lets each agent declare a lighter view. The shared history is never mutated;
only the slice passed to that agent's turn is affected.

### Filters and their order

Filters are applied in this order every turn:

1. **TextOnly / ExcludeAgents** — strip tool noise or specific agents' output
2. **MaxTurnAge** — keep only messages from the last N agent turns (semantic cut)
3. **MaxTailMessages** — hard cap: keep only the last N messages (raw count)

```yaml
Agents:
  - Name: Reviewer
    ContextWindow:
      TextOnly: true          # strip all tool-call frames and tool results
      ExcludeAgents:          # also strip all output from these agents
        - Tester
      MaxTurnAge: 5           # only keep messages from the last 5 assistant turns
      MaxTailMessages: 40     # hard cap after the above filters
      ContextCapFraction: 0.8 # emit context_cap_warning when at 80% of MaxTailMessages
      MaxToolResultChars: 8000  # truncate individual tool results in replayed history
      ToolResultCharOverrides:  # raise the cap for specific tools
        search_content: 20000
        grep_file: 20000
      MaxReplayChars: 4000    # truncate verbose assistant messages in replayed history
```

### TextOnly

Strips all tool-call frames (assistant messages containing only a function-call request) and
all tool-result messages from the history slice. Text-bearing assistant messages and all user
messages are kept.

**This is the primary lever for context reduction.** A Reviewer that independently re-reads
files and re-runs commands gains nothing from seeing the hundreds of tool results produced by
the Developer — stripping them can reduce input tokens by 90%+ in typical sessions.

When `ExcludeAgents` is set, tool-result messages are stripped automatically even when
`TextOnly` is false. Tool results are not attributed to a specific agent; leaving them without
their corresponding call frames produces a malformed context with orphaned result IDs.

### ExcludeAgents

Names of agents whose messages should be excluded entirely — both text-bearing replies and
tool-call frames.

### MaxTurnAge

Keeps only messages from the last N *agent turns*, where each turn ends with an assistant
reply. Unlike `MaxTailMessages` (a raw message count), `MaxTurnAge` is semantic: it counts
backward from the end of history and discards everything before the cut-point.

Use this to discard early-session context from phases or agents no longer relevant to the
current work — without needing to know the exact message count.

### MaxTailMessages

Hard cap applied after the other filters. When the filtered list still exceeds this count,
the oldest messages are dropped. Set `ContextCapFraction` to receive a `context_cap_warning`
event as an early signal before the hard cap is reached.

### Replay truncation (`MaxReplayChars`)

Agents sometimes produce verbose stream-of-consciousness output (3–5k tokens). When that text
is replayed verbatim in every subsequent turn, compaction summaries grow each cycle and input
tokens balloon. fuseraft truncates verbose non-summary assistant messages to 2,000 characters
by default when replaying them; set `MaxReplayChars` to override this cap per agent.
Compaction summaries are never truncated regardless of this setting.

```yaml
Agents:
  - Name: Developer
    ContextWindow:
      MaxReplayChars: 4000   # truncate replayed assistant messages to 4 000 chars
```

Default: `0` (uses the global 2,000-character fallback).

### Tool-result truncation (`MaxToolResultChars`)

A large tool result — for example, a `read_file` on a 200 KB source file — is replayed
verbatim into every subsequent agent turn, compounding context growth each cycle. Set
`MaxToolResultChars` to truncate `FunctionResultContent` strings in the replayed history
slice. A suffix noting the omitted character count is appended so agents know the result
was cut.

Unlike `TextOnly` (which drops tool messages entirely), this keeps the result visible
but bounded:

```yaml
Agents:
  - Name: Developer
    ContextWindow:
      MaxToolResultChars: 8000   # truncate tool results in replayed history to 8 000 chars
      ToolResultCharOverrides:   # per-tool overrides (search tools can afford a higher cap)
        search_content: 20000
        grep_file: 20000
```

Default: `0` (no truncation). `ToolResultCharOverrides` is only meaningful when `MaxToolResultChars` is also set; a value of `0` in the overrides map disables truncation for that specific tool entirely.

**Consumed-read optimisation:** fuseraft distinguishes between `read_file` results that
the agent has already acted on and those that are still load-bearing:

- **Consumed read** — a `write_file` or `patch_file` to the same path appears later in
  the history. The content is stale (the file has since been rewritten). These are capped
  at 500 characters regardless of `MaxToolResultChars`, with a stub noting that the file
  was subsequently modified and can be re-read if needed.
- **Unconsumed read** — no downstream write to the same path exists. The model may still
  need this content to plan its next action, so it is left at the full `MaxToolResultChars`
  limit.
- **All other tool results** (shell output, grep results, etc.) are truncated uniformly
  at `MaxToolResultChars`.

This means a file that was read and then immediately patched stops consuming context across
all subsequent turns, while a file that was read but not yet written remains fully visible.

---

## HandoffContext (targeted transition injection)

Declared on a `TransitionConfig` in the state machine. When a transition fires, the orchestrator reads from durable disk artifacts and injects a compact block into shared history before the receiving agent's first turn. Agents that don't use a `Context` spec see the injected block as part of the conversation history.

```yaml
Transitions:
  - To: Testing
    Signal: "HANDOFF TO TESTER"
    Contract: ImplementationComplete
    HandoffContext:
      - Source: session_context
      - Source: changes_recent
      - Source: brief_field:test_targets
```

**Supported source types** (same as `Context` spec, minus `own_history`):

| Source | Description |
|--------|-------------|
| `session_context` | Handoff summary from `session_context_write` |
| `changes_recent[:N]` | Last N entries from `changes.json` (default: all recent) |
| `brief_field:FIELD` | A named field from `brief.json` |
| `file:PATH` | Raw contents of an artifact file |

`own_history` is not supported in `HandoffContext`. Use the `Context` spec on the receiving agent instead.

**How it differs from `Context` spec:** `HandoffContext` injects content *into shared history* so any agent (including those without a `Context` spec) sees it in subsequent turns. `Context` spec is a per-agent read at invocation time and does not touch shared history at all.

---

## Layer 3a: Context spec (artifact-first assembly)

When `Context:` is declared on an agent, the orchestrator assembles that agent's context from disk artifacts instead of filtering or replaying the shared transcript. The agent receives only the declared sources plus its own prior turns — no Planner analysis, no Developer tool traces, nothing from other agents.

```yaml
Agents:
  - Name: Tester
    Context:
      - Source: session_context
      - Source: changes_recent:5        # last 5 change-log entries
      - Source: brief_field:test_targets
      - Source: brief_field:build_command
      - Source: own_history:4           # agent's own last 4 turns, text-only, char-bounded
```

**`ContextSource` fields:**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Source` | string | — | Required. One of: `session_context`, `changes_recent[:N]`, `brief_field:FIELD`, `file:PATH`, `own_history[:N]` |
| `MaxChars` | int | 4000 (artifacts) / 8000 (own_history) | Per-source character cap |
| `Label` | string | derived from source type | Section header override |

**`own_history` semantics:** text-only (tool-call frames and tool results stripped), char-bounded to `MaxChars`, oldest turns dropped first if the cap is reached. If the last surviving turn is still over the cap, it is truncated at the cap boundary.

**Architectural shift:**

| Mode | What the agent receives |
|------|------------------------|
| Without `Context` spec | `filtered_history` (via `ContextWindow`) + optional `HandoffContext` injection |
| With `Context` spec | `task` + `own_history` + assembled artifact block |

Token cost with a `Context` spec is O(relevant artifacts + own recent work) rather than O(session length).

**`ContextWindow` interaction:** when `Context:` is declared, `ContextWindow:` is ignored for that agent. Shared history is still written after each turn so routing and termination strategies work normally; only what the model receives changes.

---

## Layer 4: Compaction

When conversation history grows long enough to approach a model's context window, compaction
fires. It replaces the oldest turns with a single context message that agents treat as
background, then resumes from the retained tail.

### Trigger

```yaml
Compaction:
  TriggerTurnCount: 50   # fire when assistant-turn count reaches this
  KeepRecentTurns: 10    # keep this many turns verbatim; compact the rest
```

Compaction fires in three situations:
- Before a session stream starts, when resuming a checkpoint already over the threshold.
- Mid-session, after each checkpoint save, once the live history crosses the threshold.
- On demand, when an agent calls `compact_conversation` via the [`Compaction` plugin](plugins.md#compaction).

`TriggerTurnCount` must be greater than `KeepRecentTurns`.

### Modes

| Mode | How context is reconstructed | LLM call? | Requirements |
|---|---|---|---|
| `llm` | LLM summarizes the compacted turns | Yes | A model |
| `intent` | Deterministic `✓`/`✗`/`⧖` per tool call from `intents.json` | No | `ChangeTracking` |
| `lossless` | Evidence graph + contract status + state machine position | No | `statemachine` strategy + `EvidenceStore` |
| `hybrid` | Lossless reconstruction prepended before the LLM summary | Yes | `statemachine` strategy + `EvidenceStore` |
| `window` | Oldest user+assistant pairs dropped until within `TokenBudget` | No | — |

**`intent` is the recommended mode** for most sessions when `ChangeTracking` is configured.
It requires no state machine and produces a deterministic record of every tool call:

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

**`lossless` is the recommended mode** for `statemachine` sessions with an `EvidenceStore`.
Instead of summarizing the conversation, it reads disk state directly — state machine position,
contract pass/fail, evidence items — and injects it as ground truth. No hallucination is
possible because no LLM generates the summary.

**`window` mode** trades context continuity for simplicity. No summary is injected; the oldest
turns are silently dropped. Useful for exploratory sessions where older context genuinely
doesn't matter, or when you want no compaction LLM cost at all.

### Pinned summaries

Prior compaction summaries (`IsCompactionSummary`) are pinned and never dropped by `window`
mode. This preserves the head of the conversation — each compaction cycle adds a new summary
at the front while the window trims from behind it.

### Compaction model

By default, `llm` and `hybrid` modes use the first agent's model to generate the summary.
Override with `Compaction.Model` to use a cheaper model for compaction:

```yaml
Compaction:
  TriggerTurnCount: 50
  KeepRecentTurns: 10
  Mode: hybrid
  Model:
    ModelId: gpt-4o-mini
```

### Enriching summaries

Two optional flags add structured context blocks before the LLM summary text. Both are
prefixed in this order when both are enabled: symbol graph first, then reasoning excerpts.

**`IncludeReasoning`** (default `true`) — prepends a `[REASONING EXCERPTS]` block containing
the model's thinking for each compacted turn (truncated to ~500 tokens per turn). Useful when
the *why* behind prior decisions matters as much as the *what*. Requires `Events` to be
configured (reasoning excerpts are read from the session events log). When the events log is
absent or contains no reasoning events the block is omitted silently.

**`IncludeSymbolGraph`** (default `true`) — prepends a `[SYMBOL DEPENDENCY GRAPH]` block
listing every `SymbolDefinition` and `SymbolReference` node in the evidence store for files
written during the session. Gives agents an explicit map of what symbols were in scope during
the compacted turns. Requires `EvidenceStore` and `ChangeTracking` to be configured. When no
evidence store is wired the block is omitted silently.

```yaml
Compaction:
  TriggerTurnCount: 40
  KeepRecentTurns: 8
  Mode: hybrid
  IncludeReasoning: true    # default; set to false to suppress
  IncludeSymbolGraph: true  # default; set to false to suppress
```

### History pre-pruning

Before passing conversation history to the LLM summarizer, fuseraft truncates any single
message whose content exceeds `MaxCharsPerHistoryMessage` (default 8 000 characters, ≈2 000
tokens). Long messages receive a `[TRUNCATED — N chars total]` suffix, and any tool calls
recorded for that turn are appended as a compact one-line list so the summarizer still knows
what operations were attempted.

This prevents a single verbose agent turn — a large shell output or a file read embedded in the
reply — from dominating the summary prompt and inflating LLM cost.

```yaml
Compaction:
  TriggerTurnCount: 50
  KeepRecentTurns: 10
  MaxCharsPerHistoryMessage: 4000   # stricter; ~1 000 tokens per message max
```

Set to `0` to disable truncation and restore the previous behaviour (full content verbatim).

### Anti-thrash protection

If repeated compactions save very little — for example, a conversation that is near the trigger
threshold but whose LLM summary is nearly as long as the history it replaced — fuseraft
suppresses further compaction until the history grows meaningfully.

The guard tracks the savings ratio of the last `AntiThrashWindow` compactions (default 10). If
every entry in that window is below `AntiThrashMinSavingsRatio` (default 10%), `ShouldCompact`
returns `false`. The guard resets automatically as new turns extend the conversation past the
trigger again.

```yaml
Compaction:
  TriggerTurnCount: 20
  KeepRecentTurns: 5
  AntiThrashMinSavingsRatio: 0.15   # suppress if saving less than 15% (default: 0.10)
  AntiThrashWindow: 4               # look at last 4 compactions (default: 10)
```

Set either field to `0` to disable the guard entirely.

### Failure resilience

When the LLM summary call fails (network error, rate limit, model timeout), fuseraft injects a
`[COMPACTION FAILED]` marker message that tells agents the history for that range could not be
preserved, and instructs them to read disk state directly rather than relying on memory. The
session then continues from the retained tail.

For `hybrid` mode specifically, if the LLM call fails the session falls back to the lossless
reconstruction alone — still useful, just without the narrative summary layer.

If compaction itself fails for any other reason (infrastructure error, serialization failure),
the session terminates gracefully with a crash dump written to `~/.fuseraft/crashdumps/` and a
resume hint printed to the terminal. The checkpoint saved before compaction began is intact and
can be resumed.

### Change log grounding

When `ChangeTracking` or `Validation.ChangeLogPath` is configured, `llm` and `hybrid`
compactors read `changes.json` at compaction time and inject it into the summary prompt as
authoritative ground truth. Agent success claims are overridden by what `changes.json` actually
records — exit codes and file writes are facts; assistant self-reports are not.

### Cost accounting

The summary message's cumulative cost includes all the turns it replaced. Budget tracking
remains exact across compaction boundaries. `intent`, `lossless`, and `window` modes incur
no LLM cost at compaction time.

---

## Layer 5: Context Budget (per-agent token trigger)

While Layer 4 Compaction fires on *turn count*, the Context Budget fires on *cumulative input tokens* — a more direct measure of context pressure. Each agent's input token consumption is tracked independently across turns, and two thresholds can be configured:

```yaml
ContextBudget:
  WarnAt: 80000      # warn when any agent exceeds this
  CutoverAt: 120000  # compact when any agent exceeds this
```

**Why input tokens?** Turn count is a coarse proxy — a single tool-heavy turn can consume as many tokens as ten brief turns. Tracking cumulative input tokens catches context rot directly, regardless of turn count.

**Warn phase (`WarnAt`):** when a single agent's cumulative input tokens since the last compaction reach `WarnAt`, a yellow warning is printed to the console and a `context_budget_warn` event is emitted. This is a signal that compaction is coming soon. The warning fires at most once per agent per compaction cycle so it doesn't spam.

**Cutover phase (`CutoverAt`):** when a single agent's cumulative input tokens reach `CutoverAt`, compaction triggers immediately (same as if turn-count compaction had fired). The per-agent counters reset after compaction, so the next context window starts with a fresh budget — allowing the session to run indefinitely.

**Relationship to turn-count compaction:** both triggers are active simultaneously. Whichever fires first wins. If turn-count compaction fires, the context budget counters reset too. The two mechanisms complement each other: turn count protects against verbose-but-sparse sessions; token budget protects against dense, token-heavy turns.

**Requires `Compaction`:** `CutoverAt` cannot fire without a configured compactor. `fuseraft validate` reports an error if `CutoverAt > 0` without a `Compaction` section.

See [Configuration — Context budget](configuration.md#context-budget) for the full field reference.

---

## In-turn tool-result sliding window

Compaction and Context Budget operate across turns. The in-turn sliding window operates
*within* a single agent turn — before each inner LLM call in the tool-calling loop.

Without a cap, N sequential tool calls cost O(N²) cumulative tokens across the turn because
each iteration resends all prior tool results. The sliding window keeps this cost at O(window)
by replacing every tool result older than the last `MaxInTurnToolPairs` with a compact
placeholder before the next LLM call:

```yaml
Agents:
  - Name: Developer
    MaxInTurnToolPairs: 12   # keep only the last 12 tool call/result pairs in full
```

**Deterministic vs. budget-reactive:**

| Field | When it fires | Guarantee |
|-------|--------------|-----------|
| `MaxInTurnToolPairs` | Every inner LLM call, unconditionally | O(N) tool-result footprint always |
| `MaxInTurnContextTokens` | Only when total in-turn chars exceed the budget | Fires only after the budget is exceeded |

Use `MaxInTurnToolPairs` when you want a hard bound regardless of result sizes. Use
`MaxInTurnContextTokens` when result sizes vary and you want to preserve more context for
turns with small results. Both can be set simultaneously — the sliding window runs first.

**Replaced results:** replaced pairs become `[result omitted — sliding window]`. The
`CallId` on each `FunctionResultContent` is preserved so the conversation structure stays
valid for strict providers. The agent can re-read a file or re-run a command if it needs
the full content again.

**Recommended values:** 8–16 for high-volume action agents (Developer, Tester, Operator).

---

### Token-budget-based tool-result window (`MaxToolResultTokens` / `InTurnToolWindow`)

A complementary mechanism in `ContextBudget` applies a token-budget cap across all tool results in the context slice sent to the model on any single invocation — not just per agent-config:

```yaml
ContextBudget:
  MaxToolResultTokens: 80000   # evict oldest tool results once total exceeds this
  InTurnToolWindow:    20      # always retain at least the last 20 results verbatim
```

When the cumulative estimated token cost of all tool-result messages in the context slice exceeds `MaxToolResultTokens`, the oldest results beyond the last `InTurnToolWindow` are replaced with enriched tombstones that include the tool name, a key argument label, and up to 300 characters of the original content as a preview:

```
[tool result — evicted: read_file(src/LargeService.cs). Preview: "using System;…". Re-read with targeted ranges if needed.]
```

When evictions occur, a `[Context Manifest]` message is also appended at the end of the context slice listing active tool results still in context alongside the superseded (evicted) ones, so the agent knows which reads are still available and which must be re-issued with targeted ranges.

**Key difference from `MaxInTurnToolPairs`:** `MaxInTurnToolPairs` is an agent-level count-based cap applied unconditionally before every inner LLM call. `MaxToolResultTokens` is a session-level token-budget cap applied at the `ContextBudget` layer — it only fires when the total tool-result token footprint actually exceeds the threshold, preserving full context for turns with few or small results.

**Audit trail:** the full tool results remain in the shared conversation history and on-disk artifacts. Only the slice passed to the model is trimmed — compaction and session replay are unaffected.

**Recommended values:** set `MaxToolResultTokens` to 50–80% of your model's context window and `InTurnToolWindow` to 15–25 for action agents that call many tools per turn.

---

## Tool-result artifact offloading

When a tool returns a result that exceeds 40,000 characters (~10k tokens), fuseraft offloads the full content to disk and replaces the inline result with a compact reference stub before it enters the conversation history.

**Why this matters:** a large result injected once is replayed on every subsequent agent turn. In a long session with many tool calls, that compounds quadratically. Offloading prevents the payload from ever landing in history — the stub is what all future turns replay, not the raw content.

**What the agent sees instead of the full result:**

```
[result offloaded — 52,000 chars stored to artifact store]
Tool: read_file | path=src/LargeService.cs
Artifact: a3f9c20b1d7e
Use targeted tools (e.g. read_file with startLine/maxLines, or grep_in_file) for specific sections.
```

The stub is actionable: it tells the agent what happened, which tool produced the result, and how to access specific sections without pulling the full payload back into context.

**Storage:** the full content is written to `.fuseraft/artifacts/sessions/{sessionId}/tool-results/{id}.json`. Nothing is lost — the artifact is available for inspection or future retrieval.

**Coverage:** applies to all tools in both `fuseraft run` sessions and `fuseraft repl` sessions. No configuration is required.

**Threshold:** 40,000 characters (approximately 10,000 tokens at 4 chars/token). Results below this threshold are passed through unchanged.

**Relationship to `MaxToolResultChars`:** these two mechanisms are complementary and both may be active simultaneously. Artifact offloading fires at production time — large results never enter history. `MaxToolResultChars` fires at replay time — medium-sized results already in history are truncated before being sent to the model. Together they form a two-stage defence against context inflation from tool outputs.

---

## Session read cache (cross-turn file deduplication)

Every `fuseraft run` session maintains a per-session read cache keyed by resolved file path, validated by mtime + file size, and persisted to `read_cache.json` in the session directory. Its purpose is to prevent agents from re-injecting full file content into the conversation on every turn.

**How it works:**

When an agent calls `read_file` without `startLine`/`maxLines`:

1. The cache checks whether the file has changed since it was last read or written this session.
2. If unchanged, the tool returns a hint message instead of the full content:
   - **Write-primed entry** (`ReadCount = 0`): the file was written via `write_file` this session and not yet read. The hint says the content is in history via the `write_file` call.
   - **Read-primed entry** (`ReadCount ≥ 1`): the file was previously read this session. The hint says the content is in history from that earlier read, and reports how many times and how long ago.
3. Both hints include the caveat "(unless compacted away)" so agents know to force a re-read with `startLine`/`maxLines` if the turn is post-compaction.

**Write priming (`write_file`):**

When `write_file` succeeds, the session cache is primed with `ReadCount: 0` rather than invalidated. This prevents the next cross-turn read of an unchanged written file from re-injecting its full content into context — the agent already has the content from the write call itself.

Within the same turn, the cache hint is suppressed so the agent can immediately read back and verify what it just wrote. The suppression is cleared at the next `BeginTurn()`.

**`patch_file` is not write-primed:** `patch_file` gives the agent a delta, not full content, so the patched file's cache entry is invalidated rather than primed. A subsequent cross-turn `read_file` will read the actual content.

**Bypassing the cache:** pass `startLine` and/or `maxLines` to any `read_file` call to bypass the session cache and force a targeted re-read. This is the correct recovery path after compaction removes earlier file content from context.

**Storage:** the cache is persisted to `read_cache.json` in the session directory and survives compaction. Cache entries record `mtime`, `size`, `reads` (read count), and `last` (last read or write timestamp).

No configuration is required — the session read cache is always active when `fuseraft run` is used.

---

## Adaptive context-trim retry

When a provider call fails due to a context or payload size error — HTTP 413, a Bedrock
thinking-budget mismatch, or an orphaned tool-call pair — fuseraft automatically retries
with progressively reduced tool-result content rather than failing the session outright.

**Retry stages (non-streaming path):**

| Stage | Action |
|-------|--------|
| 1 | Truncate all `FunctionResultContent` to 4,000 chars; consumed `read_file` results capped at 500 chars |
| 2 | Truncate all `FunctionResultContent` to 500 chars; consumed `read_file` results capped at 500 chars |
| 3 | Drop all tool messages entirely (text-only nuclear option) |

The consumed-read cap applies at every stage: a `read_file` result whose file was
subsequently written or patched is always capped at 500 characters because the content is
stale regardless of how aggressive the retry is.

Each stage re-runs the pre-flight budget/payload checks on the trimmed context before
calling the provider, so both fuseraft's own pre-flight throws and provider 400/413
rejections recover automatically.

**Streaming path:** when `MaxContextTokens` or `MaxPayloadBytes` is configured, fuseraft
proactively pre-trims before streaming begins (streaming cannot retry mid-response). Without
explicit limits, provider errors on the streaming path surface normally.

No configuration is required — the retry logic fires automatically on every classifiable
context error.

---

## Context window visualization

After every `fuseraft run`, fuseraft automatically writes a Chart.js HTML file that shows
how each agent's cumulative input token count grew turn by turn.

**Files written to `~/.fuseraft/logs/sessions/{project_slug}/{session_id}/`:**

| File | Contents |
|------|----------|
| `ctx_snapshots.jsonl` | Raw per-turn snapshots (one JSON line per turn) |
| `ctx_viz.html` | Self-contained Chart.js visualization |

The path to the HTML file is printed at the end of the run:

```
Context viz → ~/.fuseraft/logs/sessions/home-scs-github-myproject/abc123/ctx_viz.html
```

Open the file in a browser. It requires internet access for the Chart.js CDN.

**What the chart shows:**

- One line per agent — cumulative input tokens on the Y axis, turn number on the X axis.
- **Compaction events** appear as vertical dashed grey lines labelled `⚡ compact`.
- **`warn_at` threshold** appears as a horizontal dashed yellow line (when `ContextBudget.WarnAt` is set).
- **`cutover_at` threshold** appears as a horizontal dashed red line (when `ContextBudget.CutoverAt` is set).
- Hovering a point shows the turn's per-turn input and output token counts alongside the cumulative total.

**Why it's useful:** Visual inspection quickly reveals which agent is consuming the most context, whether compaction is firing at the right threshold, and whether any single turn caused a spike that warrants `TextOnly` or `MaxTurnAge` tuning on that agent.

---

## How the layers fit together

Here is the full sequence from session start through a long-running session:

```
1. fuseraft run
   ├─ Auto-injection     → runtime environment block + .gitignore (always on)
   └─ Context Store index → injected into every agent's system prompt

2. Each agent turn — ContextAssemblyPipeline
   ├─ Intent analysis    → keywords + PascalCase symbols + failure patterns from task
   ├─ Memory block       → aggregated via MemoryManager (all providers + repository-approved entries)
   ├─ Knowledge retrieval → ADR registry + graph nodes + repository memory + session findings
   │     KnowledgeWeight.None    → skip retrieval entirely
   │     KnowledgeWeight.Low     → Verified/Inferred items only
   │     KnowledgeWeight.Default → all non-expired items (default)
   │     KnowledgeWeight.High    → Default + one-hop graph expansion on seed symbols
   ├─ System prompt      → instructions + memory block (unified)
   ├─ HandoffContext injection (state machine only) → artifact block written into shared history when a transition fires
   ├─ ContextWindow filter or Context: spec
   │  ├─ TextOnly / ExcludeAgents strip tool noise
   │  ├─ MaxTurnAge semantic cut
   │  ├─ MaxTailMessages hard cap
   │  ├─ MaxToolResultChars — truncate large tool results in replayed history
   │  └─ SanitizeToolPairs — strip orphaned assistant tool-call frames (strict providers)
   ├─ Session context injection → context_summary.md prepended when present
   ├─ Knowledge artifact appended as [Pipeline Knowledge] user message
   ├─ Task Reminder appended when context > 2 000 chars — primacy+recency sandwich reduces lost-in-the-middle drift
   └─ Assembled context → sent to LLM
      ├─ Session read cache — read_file returns hint instead of full content if file unchanged since last read/write this session
      ├─ Tool-result artifact offloading — results > 40k chars stored to disk; stub replaces inline content
      ├─ MaxInTurnToolPairs — sliding window: keep only last N tool pairs per inner call
      ├─ MaxInTurnContextTokens — budget-reactive: trim oldest pairs when over budget
      ├─ MaxToolResultTokens / InTurnToolWindow — tombstone oldest results with label+preview; append [Context Manifest] when evictions occur
      └─ On context/413 error → adaptive trim retry (up to 3 stages)

   Post-turn
   ├─ Memory provider    → post-turn hooks (if Memory: is configured)
   └─ Knowledge findings → entity-scoped observations persisted to knowledge_findings.json

3. After each checkpoint save
   └─ Compaction check
      ├─ (llm/intent/lossless/hybrid) assistant-turn count ≥ TriggerTurnCount?
      │     YES → compact oldest (Count − KeepRecentTurns) turns into one message
      │           save checkpoint with compacted history → continue
      ├─ (window) estimated token count > TokenBudget?
      │     YES → drop oldest user+assistant pairs until within budget
      │           (pinned summaries are never dropped)
      ├─ (ContextBudget) any agent's cumulative input tokens ≥ CutoverAt?
      │     YES → compact (same as turn-count trigger)
      │           reset per-agent token counters → continue
      └─ (Compaction plugin) agent called compact_conversation()?
            YES → compact (same as turn-count trigger)

4. After run completes
   └─ Context window visualization rendered to ~/.fuseraft/logs/sessions/{project_slug}/{session_id}/ctx_viz.html
```

---

## Choosing a strategy

**For most sessions with `ChangeTracking`:** use `intent` mode.

```yaml
ChangeTracking:
  Path: .fuseraft/state/changes.json

Compaction:
  TriggerTurnCount: 40
  KeepRecentTurns: 8
  Mode: intent
```

**For `statemachine` sessions with `EvidenceStore`:** use `lossless` or `hybrid`.

```yaml
Compaction:
  TriggerTurnCount: 50
  KeepRecentTurns: 10
  Mode: lossless   # or "hybrid" to add an LLM narrative on top
```

**For exploratory / throw-away sessions:** use `window` to avoid any compaction cost.

```yaml
Compaction:
  Mode: window
  TokenBudget: 60000
```

**For a downstream agent (Reviewer, Tester) that needs less history:** use `ContextWindow`.

```yaml
Agents:
  - Name: Reviewer
    ContextWindow:
      TextOnly: true
      MaxTurnAge: 3
```

**For an agent that should know nothing about earlier phases:** combine `ExcludeAgents` with
`MaxTailMessages` so it only sees the final handoff.

```yaml
Agents:
  - Name: Auditor
    ContextWindow:
      ExcludeAgents:
        - Developer
        - Tester
      MaxTailMessages: 20
```

**For sessions where turns vary wildly in token size** (e.g. a Developer that runs large shell
commands or reads big files), add `ContextBudget` so compaction tracks actual context pressure
rather than a coarse turn count:

```yaml
ContextBudget:
  WarnAt: 80000
  CutoverAt: 120000

Compaction:
  TriggerTurnCount: 40
  KeepRecentTurns: 8
  Mode: intent
```

Both triggers are active simultaneously — whichever fires first wins.

**For an agent that should see NO cross-agent history — only artifacts and its own work:**

```yaml
Agents:
  - Name: Tester
    Context:
      - Source: session_context
      - Source: changes_recent:5
      - Source: brief_field:test_targets
      - Source: own_history:4
        MaxChars: 8000
```

**For action agents that make many sequential tool calls** (Developer, Tester, Operator), set
`MaxInTurnToolPairs` to keep within-turn context cost at O(N) regardless of how many tool
calls the agent makes in a single turn:

```yaml
Agents:
  - Name: Developer
    MaxInTurnToolPairs: 12
```

Combine with `ContextBudget` to protect against both within-turn spikes and across-turn accumulation.
