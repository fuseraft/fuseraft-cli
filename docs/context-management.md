# Context Management

Context is the most important resource in a long-running agent session. Every token an agent
sees costs money and time; everything it misses is a potential hallucination or regression.
fuseraft manages context through four layers that fire at different points in a session's
lifetime:

```
Session start
  └─ Layer 1: Context Store      → files imported before the session
  └─ Layer 2: Persistent Memory  → facts recalled from prior sessions

Each agent turn
  └─ Layer 3: ContextWindow      → per-agent history filter (every turn)

History too long
  └─ Layer 4: Compaction         → replace old turns with a summary
```

Each layer is optional and independently configured. Most sessions need only one or two.

---

## Layer 1: Context Store

The context store pre-loads static reference files into `.fuseraft/context/` before a session
starts. Every agent sees a compact index block at the top of its system prompt listing what is
available, and can access the full content with `read_file`.

```yaml
# No config required — populated by CLI before running:
#   fuseraft context add ~/docs/schema.sql --name db-schema
#   fuseraft context add ~/specs/ --name specs
```

**When to use:** Database schemas, API specs, architecture docs, task briefs — anything too
large to paste into the task argument but that agents should know exists from turn one.

See [Context Store](context-store.md) for the full CLI reference.

---

## Layer 2: Persistent Memory

When `EnableMemory: true` is set on an agent, fuseraft loads that agent's persistent memory
store at session start and prepends a structured block to its instructions. Memories survive
between sessions — they accumulate over time, giving agents a working knowledge of the project.

```yaml
Agents:
  - Name: Developer
    EnableMemory: true
    Instructions: |
      You are a Go developer. Write idiomatic, tested code.
```

At session start, the agent sees:

```
MEMORY — facts recalled from prior sessions:
[preference] preferred-test-runner: Use `go test -race ./...` for all test runs.
[fact] auth-middleware: The auth middleware was rewritten in v2.3 — do not touch the legacy layer.
```

**Storage locations:**

| Context | Path |
|---|---|
| REPL sessions | `~/.fuseraft/memory/repl/` |
| Orchestration agents | `~/.fuseraft/memory/agents/{AgentName}/` |

**Memory scoping:** In a project directory that has `.fuseraft/`, only memories saved in that
directory are loaded. Directories without `.fuseraft/` fall back to all global memories.

**REPL:** Memory is always active in the REPL — no config flag needed. Memories are extracted
automatically at the end of each session and scoped to the working directory via
`.fuseraft/memory_refs.json`. Use `/memory` commands to inspect or delete them.

**Memory cap:** The prompt block is capped at 8,000 characters. Entries are ordered by type
then name; entries that would exceed the cap are dropped (header only is kept for visibility).

See [Configuration — Memory](configuration.md#memory) for the full field reference.

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

### Replay truncation

Agents sometimes produce verbose stream-of-consciousness output (3–5k tokens). When that text
is replayed verbatim in every subsequent turn, compaction summaries grow each cycle and input
tokens balloon. fuseraft automatically truncates verbose non-summary assistant messages to
2,000 characters when replaying them into the next turn's history. Compaction summaries are
never truncated.

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

Compaction fires in two situations:
- Before a session stream starts, when resuming a checkpoint already over the threshold.
- Mid-session, after each checkpoint save, once the live history crosses the threshold.

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

**`IncludeReasoning`** — prepends a `[REASONING EXCERPTS]` block containing the model's
thinking for each compacted turn (truncated to ~500 tokens per turn). Useful when the *why*
behind prior decisions matters as much as the *what*. Requires `Events` to be configured
(reasoning excerpts are read from the session events log).

**`IncludeSymbolGraph`** — prepends a `[SYMBOL DEPENDENCY GRAPH]` block listing every
`SymbolDefinition` and `SymbolReference` node in the evidence store for files written during
the session. Gives agents an explicit map of what symbols were in scope during the compacted
turns. Requires `EvidenceStore` and `ChangeTracking` to be configured.

```yaml
Compaction:
  TriggerTurnCount: 40
  KeepRecentTurns: 8
  Mode: hybrid
  IncludeReasoning: true
  IncludeSymbolGraph: true
```

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

## How the layers fit together

Here is the full sequence from session start through a long-running session:

```
1. fuseraft run
   ├─ Context Store index → injected into every agent's system prompt
   └─ Persistent Memory  → prepended to each agent's instructions (if EnableMemory: true)

2. Each agent turn
   └─ ContextWindow filter applied to conversation history
      ├─ TextOnly / ExcludeAgents strip tool noise
      ├─ MaxTurnAge semantic cut
      └─ MaxTailMessages hard cap
         └─ Filtered slice + replay-truncated content → sent to LLM

3. After each checkpoint save
   └─ Compaction check
      ├─ (llm/intent/lossless/hybrid) assistant-turn count ≥ TriggerTurnCount?
      │     YES → compact oldest (Count − KeepRecentTurns) turns into one message
      │           save checkpoint with compacted history → continue
      └─ (window) estimated token count > TokenBudget?
            YES → drop oldest user+assistant pairs until within budget
                  (pinned summaries are never dropped)
```

---

## Choosing a strategy

**For most sessions with `ChangeTracking`:** use `intent` mode.

```yaml
ChangeTracking:
  Path: .fuseraft/changes.json
  IntentLogPath: .fuseraft/state/intents.json

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
