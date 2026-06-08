# Spec-Driven Development

Spec-driven development (SDD) is a workflow where a structured written specification is agreed upon before any code is written. The spec acts as the single source of truth — agents plan, implement, and verify against it rather than interpreting a freeform prompt.

fuseraft supports SDD via the `--spec` flag on `fuseraft run`.

---

## The problem `--spec` solves

Without a spec, the Planner synthesises `brief.json` from whatever you typed as the task. For short, well-scoped tasks this works well. For larger features — new APIs, multi-file refactors, greenfield modules — the Planner's interpretation can drift from your intent before a single line of code is written.

`--spec` gives you a place to write down the design before handing it to agents. The spec anchors `brief.json`, and `brief.json` anchors every validator. Nothing can be declared complete unless it satisfies the spec.

---

## How it works

When you pass `--spec path/to/spec.md`:

1. **System-prompt injection** — the spec is injected into every agent's system prompt as a `## Project Spec (authoritative)` block. All agents — Planner, Developer, Reviewer — see it throughout the session, including after context compaction.
2. **Task injection** — the spec is appended to the task at turn 0 as a fenced block under `--- SPEC (authoritative ...)`. The Planner sees it as the mission statement.
3. **Default task** — if you supply `--spec` with no task argument, the task defaults to `"Implement the specification."` so you never have to repeat yourself.
4. **brief.json derivation** — the Planner is instructed to derive `brief.json` directly from the spec. `acceptance_criteria` and `files_to_change` in `brief.json` must reflect what the spec describes.

Resuming a session (`--resume`) ignores `--spec` — the spec is already in the conversation history and system prompts.

---

## Spec file format

The spec file can be Markdown, plain text, or JSON. There is no required schema — write what is useful for your task.

A useful spec for a software feature typically covers:

- **Goal** — one paragraph on what is being built and why
- **User journeys** — the paths a user or caller will take through the feature
- **Acceptance criteria** — testable statements of correctness (these map directly to `brief.json`'s `acceptance_criteria`)
- **Files to change** — the source files the implementation will touch (maps to `brief.json`'s `files_to_change`)
- **Constraints** — what the implementation must not do (tech stack limits, backward compatibility, performance bounds)
- **Out of scope** — explicit exclusions to prevent scope creep

### Minimal example — `spec.md`

```markdown
## Goal

Add a `/health` endpoint to the Go API server that returns the current service
status and uptime. Used by the load balancer health check.

## Acceptance criteria

- `GET /health` returns HTTP 200 with `{"status":"ok","uptime_seconds":<n>}`
- `uptime_seconds` increases between requests
- Endpoint is reachable without authentication

## Files to change

- `internal/api/routes.go` — register the `/health` route
- `internal/api/health.go` — handler implementation
- `internal/api/health_test.go` — unit tests

## Constraints

- No new dependencies
- Response time < 5 ms under normal load
```

### Structured example — `spec.json`

```json
{
  "goal": "Add a /health endpoint to the Go API server",
  "acceptance_criteria": [
    "GET /health returns 200 with {\"status\":\"ok\",\"uptime_seconds\":<n>}",
    "uptime_seconds increases between calls",
    "Endpoint is reachable without auth"
  ],
  "files_to_change": [
    "internal/api/routes.go",
    "internal/api/health.go",
    "internal/api/health_test.go"
  ],
  "constraints": [
    "No new dependencies",
    "Response time < 5 ms"
  ]
}
```

JSON specs work especially well when you want to feed structured data to the Planner without any prose.

---

## Three levels of SDD

| Level | What you write | What agents write | When to use |
|---|---|---|---|
| **Spec-first** | Spec file (then discard it after the session) | `brief.json` + all code | One-shot features where the spec is a convenience, not a long-term artifact |
| **Spec-anchored** | Spec file committed to the repo | `brief.json` + all code | Features you will evolve — check the spec into version control alongside the code |
| **Spec-as-source** | Spec file only (you never edit code directly) | Everything | Full AI delegation — humans own the spec, agents own the implementation |

fuseraft's `--spec` flag supports all three levels. The difference is whether you commit the spec file and how you treat it when the feature changes.

---

## Relationship to `brief.json`

`brief.json` is fuseraft's machine-validated execution contract:

| | `spec.md` | `brief.json` |
|---|---|---|
| **Written by** | You (the human) | Planner agent |
| **Read by** | All agents (via system prompt) | Validators, Reviewer, Compactor |
| **Format** | Any — prose, Markdown, JSON | Structured JSON |
| **Scope** | Design intent, user journeys, constraints | Precise file list, testable criteria |
| **Lives in** | Anywhere on disk | `.fuseraft/artifacts/sessions/<id>/brief.json` |

With `--spec`, the Planner is instructed to derive `brief.json` from the spec rather than synthesising it from the task prompt. The spec drives the plan; the plan drives the implementation; validators enforce the plan.

---

## Combining `--spec` with other flags

`--spec` composes with all other flags:

```bash
# Spec + human-in-the-loop so you can review the Planner's brief before implementation
fuseraft run --spec spec.md --hitl

# Spec + context files for supplementary reference material
fuseraft run --spec spec.md --context-file openapi.yaml --context-file schema.sql

# Spec + custom config for a specialised agent team
fuseraft run --spec spec.md -c configs/swe.yaml

# Spec + task override (use when the spec covers multiple features and you want one now)
fuseraft run --spec spec.md "Implement only the /health endpoint for now"

# Spec + CI mode — exits 2 if any acceptance criterion fails
fuseraft run --spec spec.md --ci
```

`--spec` differs from `--context-file`:

- Context files are supplementary reference material appended to the task and read from disk by agents when needed.
- The spec is framed as authoritative: all agents are instructed to treat it as the single source of truth, and `brief.json` must derive from it.

---

## Quick start

1. Write a `spec.md` in your project directory.
2. Run `fuseraft run --spec spec.md`.
3. The Planner reads the spec and writes `brief.json` with criteria and files derived from it.
4. Validators block handoffs until the implementation matches the brief.
5. Commit `spec.md` alongside your code if you want spec-anchored SDD.
