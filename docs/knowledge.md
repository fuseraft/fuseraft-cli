# Knowledge Layer

The knowledge layer is a set of persistent, cross-session subsystems that let agents accumulate and query durable knowledge about a codebase — architectural decisions, structural symbols, verified claims, recurring patterns, long-horizon objectives, and session-discovered findings. All subsystems share a single `IKnowledgeLayer` interface and are queried automatically on every agent turn by the `ContextAssemblyPipeline`.

## Overview

```
Each agent turn
        │
        ▼
  IntentAnalyzer      → extract keywords, PascalCase symbols, failure patterns
        │
        ▼
  KnowledgeRetriever  → query ADR registry, repository graph, repository memory,
        │                 knowledge findings store (entity-driven, cross-session)
        │
        ▼
  ContextBudgeter     → rank by confidence tier, trim to 6 000-char budget
        │
        ▼
  ContextAssemblyPipeline → inject as [Pipeline Knowledge] user message
```

Knowledge retrieval is **always on** — no per-agent config is required. Use `KnowledgeWeight`
on an agent config to control retrieval depth:

| Value | Behaviour |
|---|---|
| `None` | Skip retrieval entirely |
| `Low` | `Verified` and `Inferred` items only |
| `Default` | All non-expired items (default for all agents) |
| `High` | Default + one-hop graph expansion on seed symbols |

Agents interact with the knowledge layer through plugin tools (`decision_*`, `graph_*`, `objective_*`). Validators write provenance claims after successful checks. The lifecycle manager (`fuseraft knowledge gc`) periodically archives stale artifacts.

---

## Subsystems

### Architecture Decision Registry (ADR)

Stores and indexes architecture decision records (ADRs) as JSON files under `.fuseraft/knowledge/decisions/`. Each ADR records the context, decision text, alternatives, consequences, and the symbols or files it governs.

Agents use the `decision_search`, `decision_read`, `decision_create`, and `decision_supersede` plugin tools to interact with ADRs. ADRs are automatically linked into the repository semantic graph via `adr_governs` edges when their `Governs` list is populated.

**Lifecycle:** Superseded ADRs are archived to `.fuseraft/knowledge/decisions/archive/` by `fuseraft knowledge gc`. They remain queryable via `decision_search` but are excluded from default injection.

---

### Repository Semantic Graph

A structural index of every file, namespace/package, type, interface, method, property, and field in the project, plus ADR nodes linked via `adr_governs` edges. Persisted as a single JSON file at `~/.fuseraft/state/{project_slug}/repository.graph`. Scanning is per-language via a pluggable `IRepositoryGraphStrategy` — C#, Go, and Python are supported out of the box, and a repo can mix all three.

Build the graph with:

```bash
fuseraft graph build
```

The harness rebuilds affected nodes incrementally after every `FileWrite` tool call. Agents query the graph via `graph_search` (find nodes by name/type), `graph_refs` (what references this symbol), and `graph_dependents` (transitive dependents).

**SymbolId scheme** — node identities are stable, fully-qualified strings:

| Prefix | Example |
|--------|---------|
| `file:` | `file:src/Core/Models/AdrEntry.cs` |
| `namespace:` | `namespace:fuseraft.Core.Models` (C#) |
| `package:` | `package:mathutil` (Go package) · `package:app.models` (Python module) |
| `type:` | `type:fuseraft.Core.Models.AdrEntry` |
| `interface:` | `interface:fuseraft.Core.IKnowledgeLayer` |
| `method:` | `method:fuseraft.Core.Models.AdrEntry.SomeMethod` |
| `property:` | `property:fuseraft.Core.Models.AdrEntry.Title` |
| `adr:` | `adr:ADR-0042` |

Go and Python have no formal interface keyword: Go embedding resolves to `inherits`/`implements` heuristically (checking known nodes first, then an `-er`/`-or` naming convention), while Python emits `inherits` only — every base class, abstract or not.

**Edge types:** `defines`, `imports`, `inherits`, `implements`, `references`, `depends_on`, `adr_governs`.

---

### Provenance and Confidence Tracking

Every verifiable claim made during a session can be recorded with supporting evidence in the provenance registry (`~/.fuseraft/state/{project_slug}/provenance.json`). Validators emit `ClaimRecord` entries when they pass; downstream agents and the Context Broker use the registry to determine whether evidence supports a given artifact.

**Confidence tiers** are computed mechanically from the evidence composition — never from API response text:

| Tier | Evidence required |
|------|-------------------|
| `Verified` | Two or more of: `TestResult`, `ExitCode`, `Validator`, `GitHistory` |
| `Inferred` | One hard evidence source, or `ADR`/`RepositoryMemory` backing |
| `Assumed` | `AgentAssertion` only, no corroborating hard evidence |
| `Guessed` | No support at all |

Claims carry an optional `ExpiresAt` timestamp set by the caller based on the volatility of the claim. Claims past their `ExpiresAt` are excluded from broker output and archived by `fuseraft knowledge gc`.

---

### Repository Memory

Cross-session patterns extracted from the evidence graph and change log after each session closes. Entries start as `Candidate` and are never injected into agent prompts until a human approves them via `fuseraft memory review` or an automated reviewer agent promotes them.

Once approved, repository memories are prepended to every agent session's system prompt.

**Pattern sources** — extraction is deterministic; no LLM call is made:

| Source | Pattern prefix | Evidence class |
|--------|---------------|----------------|
| Shell commands that exited 0 | `Shell command succeeds: …` | `ExitCode` |
| Test results that passed | `Test passes: …` | `TestResult`, `ExitCode` |
| Files written more than once in a session | `File is modified repeatedly in sessions: …` | `EvidenceGraph` |
| Shell commands that exited non-zero more than once | `Shell command fails repeatedly: …` | `ExitCode` |

Failure patterns flag commands with unstable preconditions — missing dependencies, write-block loops, or brittle invocations — so future agents verify the environment before relying on them.

**Reinforcement** — when the same pattern recurs in a later session, `ReinforcementCount` is incremented regardless of whether the entry is `Approved` or still `Candidate`. This does not promote a Candidate; promotion requires explicit review. It does make high-reinforcement candidates surface first in `MEMORY.md` and in `fuseraft memory review` output, so the most reliably observed patterns are easiest to approve.

```bash
# Review pending candidates (sorted by reinforcement count descending)
fuseraft memory review

# Browse all entries
fuseraft memory review --all
```

**Lifecycle:** Approved memories not reinforced within the `MemoryReinforceWindowDays` window (default 90 days) are demoted back to `Candidate` by `fuseraft knowledge gc`.

---

### Architecture Drift Detection

Compares `using` directives in every `.cs` source file against the layer manifest in `.fuseraft/architecture.yaml` and reports violations. A violation is a source file in one layer importing a namespace owned by a layer it is not permitted to depend on.

`fuseraft init` writes a default `architecture.yaml` on first run. Edit its `Layers` and `MayDependOn` lists to match your project structure.

```yaml
# .fuseraft/architecture.yaml
Layers:
  - Name: Core
    Paths: [src/Core/]
    MayDependOn: []

  - Name: Infrastructure
    Paths: [src/Infrastructure/]
    MayDependOn: [Core]

  - Name: Orchestration
    Paths: [src/Orchestration/]
    MayDependOn: [Core, Infrastructure]

  - Name: Cli
    Paths: [src/Cli/]
    MayDependOn: [Core, Infrastructure, Orchestration]
```

```bash
fuseraft arch check          # exits 0 if clean, 1 if violations found
```

Violations are also emitted as `Violation` nodes in the evidence graph so they carry provenance and are queryable.

---

### Dependency Planner

When agents declare `Produces` and `Requires` tokens in their `AgentConfig`, the `DependencyPlanner` builds an execution DAG, detects cycles (reported as config errors at startup), and schedules agents in parallel whenever their dependencies are already fulfilled. This is activated automatically when any agent in the config declares `Produces` or `Requires`.

```yaml
Produces:
  - artifact:session-persistence
  - file:src/SessionManager.cs
Requires:
  - symbol:ISessionStore
  - artifact:repository-graph
```

---

### Objective Tracking

Long-horizon objectives span multiple sessions. Active objectives are summarised in every agent system prompt and in compaction summaries so the team always has the big picture in view.

```bash
fuseraft objective create --title "Ship auth refactor" --tasks "Design,Implement,Test"
fuseraft objective list
fuseraft objective status OBJ-0001
```

Progress is computed on demand from `CompletedTasks.Count / (CompletedTasks.Count + RemainingTasks.Count)`. The `objective_link_task` plugin tool lets agents update task status within a session.

---

### Session Knowledge Findings Store

Factual discoveries made during agent tool calls are persisted to `~/.fuseraft/state/{project_slug}/knowledge_findings.json` after every turn and surfaced in future sessions without any embedding index.

After each agent turn, `ObservationExtractor` inspects the turn's tool call results and creates an `Observation` for each discovery or state-change tool. The entity is derived from the tool's arguments — the file path for `read_file`, the search pattern for `grep_file`, etc. Observations with a non-null entity are written to `RepositoryKnowledgeStore` as `RepositoryKnowledgeFinding` records.

**Example finding:**
```json
{
  "id": "a3f29c1e8b4d7f20",
  "entity": "src/Infrastructure/AgentFactory.cs",
  "finding": "File content: ...",
  "source": "session-20260603-1",
  "confidence": 0.85,
  "agentName": "Developer",
  "kind": "observation",
  "recordedAt": "2026-06-03T14:22:11Z"
}
```

**Finding kinds:** `observation` (read/search), `change` (write/patch/delete), `ownership`, `architectural_decision`, `dependency`, `pitfall`.

`KnowledgeRetriever` queries the store during the retrieval phase by matching entity names against the current intent signals. This makes knowledge cumulative across sessions: an agent that reads `AuthService.cs` in session 1 leaves a finding; an agent working on auth in session 2 retrieves that finding automatically.

---

### Context Assembly Pipeline

The `ContextAssemblyPipeline` is the unified entry point for all agent context construction. Every invocation — sequential, parallel, and verifier agents — goes through the same pipeline stages. The pipeline emits a `context_assembly` event via `EventEmitter` after each turn with the following fields:

| Field | What it measures |
|---|---|
| `knowledge_retrieved` | Items returned by `KnowledgeRetriever` before budget trimming |
| `knowledge_included` | Items that survived the 6 000-char budget and were injected |
| `memory_loaded` | Memory entries loaded from the agent's store |
| `memory_included` | Entries that fit within the 8 000-char memory block budget |
| `artifacts` | Typed context artifacts assembled (knowledge + session_context) |
| `context_chars` | Total character count of all messages in the assembled context |
| `system_prompt_chars` | Character length of the system prompt |
| `assembly_ms` | Wall-clock time spent in `AssembleAsync` |
| `context_chars_breakdown` | Per-source char breakdown: `system_prompt`, `memory`, `session_context`, `knowledge`, `history`. Use this to identify which source dominates startup context cost. |
| `tool_count` | Number of tool schemas included in the API `tools` parameter |
| `tool_schema_est_tokens` | Estimated token cost of tool schemas (`tool_count × 450`). Tool schemas are sent as the API `tools` parameter, not as messages, so they are invisible to `context_chars`. This estimate closes the gap between `context_chars` and actual input tokens reported by the provider. |

These events are written to `~/.fuseraft/logs/sessions/{project_slug}/{session_id}/events.jsonl` alongside `turn_end` and `reasoning` events and can be consumed by dashboards or CI pipelines to track context utilization over time.

---

### Adaptive Context Pipeline (formerly Context Broker)

The pipeline ties all subsystems together. Retrieval runs automatically before every agent turn:

1. **IntentAnalyzer** — extracts keywords, PascalCase symbols, and failure patterns from the task description.
2. **KnowledgeRetriever** — queries the ADR registry, repository graph, approved repository memories, and the session knowledge findings store for each signal.
3. **ContextBudgeter** — ranks results by confidence tier (`Verified` > `Inferred` > `Assumed` > `Guessed`), excludes expired claims, and trims to the 6 000-character budget (~1 500 tokens).
4. **Prompt assembly** — formats the surviving items into a `[Pipeline Knowledge]` user message appended to the context.

When retrieval produces no results the pipeline proceeds without a knowledge block — there is no fallback needed because the agent's instructions and memory block are always present.

---

### Knowledge Lifecycle Management

Without periodic maintenance, every knowledge subsystem accumulates stale data. The lifecycle manager runs all retention policies in one command:

```bash
fuseraft knowledge gc          # dry-run: shows what would change
fuseraft knowledge gc --apply  # applies all policies
```

| Policy | What it does |
|--------|-------------|
| Archive superseded ADRs | Moves `Superseded` ADRs to `.fuseraft/knowledge/decisions/archive/` |
| Demote aged memories | Demotes `Approved` memories not reinforced within the window back to `Candidate` (does not affect `Candidate` entries — their counts accumulate indefinitely until reviewed) |
| Decay provenance confidence | Downgrades `Verified` claims older than `ConfidenceDecayDays` to `Inferred` |
| Prune orphaned graph nodes | Removes nodes with no edges and no recent file touch |
| Compact provenance registry | Archives expired `ClaimRecord` entries to `~/.fuseraft/state/{project_slug}/provenance.archive.json` |
| Delete ephemeral state files | When `.fuseraft/.fuseraftignore` is present, deletes state files marked ephemeral (e.g. `knowledge_findings.json`). `provenance.archive.json` is never deleted — gc writes to it. |
| Delete ephemeral log files | When `.fuseraft/.fuseraftignore` is present, deletes files under `~/.fuseraft/logs/{project_slug}/` marked ephemeral (e.g. `app.log`, `repl_events.jsonl`). |

Configure retention windows in `.fuseraft/knowledge/lifecycle.yaml` (created by `fuseraft init`).

---

## Directory Layout

Most knowledge artifacts are project-local (`.fuseraft/`), but a few — repository graph, repository memory, provenance, and session knowledge findings — live in the global `~/.fuseraft/` home directory, keyed by `{project_slug}`:

```
.fuseraft/                                (project-local)
├── architecture.yaml                   ← layer manifest (user-authored)
└── knowledge/
    ├── lifecycle.yaml                  ← lifecycle policy
    ├── decisions/
    │   ├── ADR-0001.json               ← architecture decision records
    │   └── archive/                    ← superseded ADRs (still queryable)
    └── objectives/
        └── OBJ-0001.yaml               ← long-horizon objectives

~/.fuseraft/                             (global, keyed by {project_slug})
├── knowledge/{project_slug}/repository/
│   ├── <id>.json                       ← repository memory entries
│   └── MEMORY.md                       ← human-readable index
└── state/{project_slug}/
    ├── repository.graph                ← repository semantic graph
    ├── knowledge_findings.json         ← entity-scoped findings from all sessions
    ├── provenance.json                 ← active claim records
    └── provenance.archive.json         ← archived (expired) claim records
```

## Agent Plugin Tools

| Tool | Plugin | Description |
|------|--------|-------------|
| `decision_search` | Decision | Search ADRs by keyword, tag, or status |
| `decision_read` | Decision | Read a specific ADR by ID |
| `decision_create` | Decision | Create a new ADR (requires write capability) |
| `decision_supersede` | Decision | Mark an ADR as superseded by a newer one |
| `graph_search` | Graph | Find graph nodes by name or type |
| `graph_refs` | Graph | What symbols reference a given node |
| `graph_dependents` | Graph | Transitive dependents of a node |
| `objective_create` | Objective | Create a new objective |
| `objective_read` | Objective | Read an objective by ID |
| `objective_update` | Objective | Update objective status or task lists |
| `objective_list` | Objective | List objectives |
| `objective_link_task` | Objective | Mark a task complete or add a remaining task |
