# Knowledge Layer

The knowledge layer is a set of persistent, cross-session subsystems that let agents accumulate and query durable knowledge about a codebase — architectural decisions, structural symbols, verified claims, recurring patterns, and long-horizon objectives. All subsystems share a single `IKnowledgeLayer` interface and are wired together through the `ContextBroker` at session start.

## Overview

```
Session input (task/brief)
        │
        ▼
  IntentAnalyzer   → extract keywords, symbols, failure patterns
        │
        ▼
  KnowledgeRetriever → query ADR registry, repository graph, repository memory
        │
        ▼
  ContextBudgeter  → rank by confidence tier, trim to token budget
        │
        ▼
  ContextBroker    → assemble formatted context block → agent system prompt
```

Agents interact with the knowledge layer through plugin tools (`decision_*`, `graph_*`, `objective_*`). Validators write provenance claims after successful checks. The lifecycle manager (`fuseraft knowledge gc`) periodically archives stale artifacts.

---

## Subsystems

### Architecture Decision Registry (ADR)

Stores and indexes architecture decision records (ADRs) as JSON files under `.fuseraft/knowledge/decisions/`. Each ADR records the context, decision text, alternatives, consequences, and the symbols or files it governs.

Agents use the `decision_search`, `decision_read`, `decision_create`, and `decision_supersede` plugin tools to interact with ADRs. ADRs are automatically linked into the repository semantic graph via `adr_governs` edges when their `Governs` list is populated.

**Lifecycle:** Superseded ADRs are archived to `.fuseraft/knowledge/decisions/archive/` by `fuseraft knowledge gc`. They remain queryable via `decision_search` but are excluded from default injection.

---

### Repository Semantic Graph

A structural index of every file, namespace, type, interface, method, property, and field in the project, plus ADR nodes linked via `adr_governs` edges. Persisted as a single JSON file at `.fuseraft/state/repository.graph`.

Build the graph with:

```bash
fuseraft graph build
```

The harness rebuilds affected nodes incrementally after every `FileWrite` tool call. Agents query the graph via `graph_search` (find nodes by name/type), `graph_refs` (what references this symbol), and `graph_dependents` (transitive dependents).

**SymbolId scheme** — node identities are stable, fully-qualified strings:

| Prefix | Example |
|--------|---------|
| `file:` | `file:src/Core/Models/AdrEntry.cs` |
| `namespace:` | `namespace:fuseraft.Core.Models` |
| `type:` | `type:fuseraft.Core.Models.AdrEntry` |
| `interface:` | `interface:fuseraft.Core.IKnowledgeLayer` |
| `method:` | `method:fuseraft.Core.Models.AdrEntry.SomeMethod` |
| `property:` | `property:fuseraft.Core.Models.AdrEntry.Title` |
| `adr:` | `adr:ADR-0042` |

**Edge types:** `defines`, `imports`, `inherits`, `implements`, `references`, `depends_on`, `adr_governs`.

---

### Provenance and Confidence Tracking

Every verifiable claim made during a session can be recorded with supporting evidence in the provenance registry (`.fuseraft/state/provenance.json`). Validators emit `ClaimRecord` entries when they pass; downstream agents and the Context Broker use the registry to determine whether evidence supports a given artifact.

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

Once approved, repository memories are prepended to every agent session's system prompt. When the same pattern recurs across sessions, its `ReinforcementCount` is incremented and its confidence tier is recomputed.

```bash
# Review pending candidates
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

### Adaptive Context Broker

The broker ties all subsystems together. When an agent config declares a `broker:*` context source, the broker runs before each turn:

1. **IntentAnalyzer** — extracts keywords, PascalCase symbols, and failure patterns from the task description.
2. **KnowledgeRetriever** — queries the ADR registry, repository graph, and approved repository memories for each signal.
3. **ContextBudgeter** — ranks results by confidence tier (`Verified` > `Inferred` > `Assumed` > `Guessed`), excludes expired claims, and trims to the configured token budget.
4. **Prompt assembly** — formats the surviving items into a labelled context block injected into the agent system prompt.

When the broker produces no results it falls back gracefully to static context assembly.

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
| Demote aged memories | Demotes `Approved` memories not reinforced within the window back to `Candidate` |
| Decay provenance confidence | Downgrades `Verified` claims older than `ConfidenceDecayDays` to `Inferred` |
| Prune orphaned graph nodes | Removes nodes with no edges and no recent file touch |
| Compact provenance registry | Archives expired `ClaimRecord` entries to `.fuseraft/state/provenance.archive.json` |

Configure retention windows in `.fuseraft/knowledge/lifecycle.yaml` (created by `fuseraft init`).

---

## Directory Layout

```
.fuseraft/
├── architecture.yaml                   ← layer manifest (user-authored)
├── knowledge/
│   ├── lifecycle.yaml                  ← lifecycle policy
│   ├── decisions/
│   │   ├── ADR-0001.json               ← architecture decision records
│   │   └── archive/                    ← superseded ADRs (still queryable)
│   ├── repository/
│   │   ├── <id>.json                   ← repository memory entries
│   │   └── MEMORY.md                   ← human-readable index
│   └── objectives/
│       └── OBJ-0001.yaml               ← long-horizon objectives
└── state/
    ├── repository.graph                ← repository semantic graph
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
