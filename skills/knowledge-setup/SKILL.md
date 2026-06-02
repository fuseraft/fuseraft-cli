---
name: knowledge-setup
description: Bootstrap the fuseraft knowledge layer in a new or existing project. Trigger when the user wants to set up ADR tracking, the repository semantic graph, architecture drift detection, or objective tracking — or when Decision, Graph, or Objective plugins are wired in a config but the backing stores have not been initialized.
---

# Knowledge Setup

Initialize the knowledge layer so agents can accumulate and query durable knowledge about a codebase across sessions.

## When to Use

Use this skill when:
- Starting a project that will use the `Decision`, `Graph`, or `Objective` plugins
- `fuseraft graph build` has not been run and agents report missing graph data
- The knowledge directory tree (`.fuseraft/knowledge/`) does not exist yet
- The user wants to configure architecture drift detection (`fuseraft arch check`)
- The user wants to tune the knowledge lifecycle / GC policy

Do **not** use this skill to modify an already-working knowledge layer — `patch_file` the specific config file instead.

## Workflow

### Step 1: Scaffold the Knowledge Directory

Run `fuseraft init` in the project root. This is idempotent — safe to re-run.

```bash
fuseraft init
```

What it creates on first run:

| Path | Purpose |
|------|---------|
| `.fuseraft/architecture.yaml` | Layer manifest for `fuseraft arch check` |
| `.fuseraft/knowledge/lifecycle.yaml` | Retention policy for `fuseraft knowledge gc` |
| `.fuseraft/knowledge/decisions/` | ADR store |
| `.fuseraft/knowledge/repository/` | Cross-session repository memory patterns |
| `.fuseraft/knowledge/objectives/` | Long-horizon objective tracking |

To scaffold with a template and model at the same time:

```bash
fuseraft init --template graph --model claude-sonnet-4-6
```

### Step 2: Build the Repository Semantic Graph

Index the codebase so `graph_search`, `graph_refs`, and `graph_dependents` have data to query.

```bash
fuseraft graph build
```

Options:
- `--dir <path>` — limit to a subdirectory (default: project root)
- `--output <path>` — override graph file location (default: `.fuseraft/state/repository.graph`)

The harness rebuilds affected nodes incrementally after every agent `write_file` call during a run. Re-run manually after large refactors or initial setup.

Add the `Graph` plugin to agents that need to locate symbols, trace dependencies, or understand what references a given type or method. All graph tools are read-only; no `Capabilities` restriction is needed.

### Step 3: Configure Architecture Drift Detection

Edit `.fuseraft/architecture.yaml` to define the project's real layer boundaries.

```yaml
Layers:
  - Name: Core
    Namespaces: ["MyProject.Core"]
    MayDependOn: []
  - Name: Infrastructure
    Namespaces: ["MyProject.Infrastructure"]
    MayDependOn: ["Core"]
  - Name: Cli
    Namespaces: ["MyProject.Cli"]
    MayDependOn: ["Core", "Infrastructure"]
```

Run the check at any time:

```bash
fuseraft arch check
```

Violations are printed with file path, source namespace, and the forbidden dependency. Fix the manifest (not the source code) only when the dependency is intentional and the boundary rule was wrong.

To wire architecture checking into a pipeline, add `fuseraft arch check` as a `shell_run` step in the Reviewer agent's instructions, or attach it as a `RequireShellPass` validator on the Reviewer → Done edge/transition.

### Step 4: Tune the Lifecycle Policy

Edit `.fuseraft/knowledge/lifecycle.yaml` to control how artifacts age and are pruned. The defaults are conservative and suitable for most projects without modification. Tune only when:

- ADR archive lag is too short (`DecisionSupersededGracePeriodDays`)
- Repository memory candidates accumulate too slowly (`RepositoryMemoryMinConfidence`)
- Provenance claims expire too aggressively (`ProvenanceClaimDefaultTtlDays`)

Run GC manually after major sessions or on a schedule:

```bash
fuseraft knowledge gc
fuseraft knowledge gc --dry-run    # preview without writing
```

### Step 5: Enable Repository Memory (Optional)

Repository memory captures recurring patterns from the evidence graph at session close. Review and approve candidates before they are injected into future agent prompts:

```bash
fuseraft memory review
```

Approved patterns are stored in `.fuseraft/knowledge/repository/` and injected into agent context by the Knowledge Broker at session start. Reject patterns that are too project-specific or volatile to be useful across sessions.

### Step 6: Wire Knowledge Plugins into Agents

With the layer initialized, add plugins to agent `Plugins` lists in the orchestration config:

| Plugin | When to add | Tools |
|--------|------------|-------|
| `Decision` | Agents that read or create ADRs | `decision_search`, `decision_read`, `decision_create`, `decision_supersede` |
| `Graph` | Agents that navigate codebase structure | `graph_search`, `graph_refs`, `graph_dependents` |
| `Objective` | Agents that track long-horizon goals | `objective_create`, `objective_read`, `objective_update`, `objective_list`, `objective_link_task` |

Restrict `Decision` to read-only for agents that should query but not create:

```yaml
Plugins: [Decision]
Capabilities:
  Decision: [read]
```
