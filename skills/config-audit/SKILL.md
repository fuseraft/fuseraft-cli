---
name: config-audit
description: Review an existing fuseraft team file (YAML or JSON) for design problems before running it. Trigger when the user wants to validate, review, or sanity-check a team file, or when fuseraft validate passes but the run still behaves unexpectedly.
---

# Team File Audit

Run `fuseraft validate`, then audit what validation can't see: whether the flow is gated where it matters, whether loops can end, whether agents have the tools their instructions need, and whether each agent sees the right context.

## When to Use

Use this skill when:
- The user wants a review before running a team
- `fuseraft run` fails or stalls for reasons that look config-related
- The user just wrote or changed a team file and wants a second opinion
- The file passes `fuseraft validate` but the run behaves wrongly

Do **not** use this skill to create a team from scratch; use `craft-orchestration`. If the file starts with `Orchestration:`, it's the old format: run `fuseraft migrate <file>` first and audit the result.

## Workflow

### Step 1: Locate the File

Use the path the user gave. Otherwise look in `.fuseraft/config/` and ask which one if there are several.

### Step 2: Run the Validator

```bash
fuseraft validate <path>
fuseraft validate <path> --diagram
```

Fix every error first. Loading is strict, so validation already catches unknown fields, undeclared agents or artifacts, misspelled checks and options, malformed shapes, edges to agents with no step, flows that never reach `end`, missing models, and out-of-range values. The diagram shows each edge and its gates.

### Step 3: Read the File

Call `read_file` on it and note its `artifacts`, `agents`, `flow` and `advanced` sections.

### Step 4: Design Audit

Note every issue found.

#### A. Gates on forward edges

1. Every edge that hands work *forward* to an agent that depends on it should require that work. A Developer's edge to the Tester with no `requires` lets "I implemented it" through on a claim alone.
2. Suggested minimums: brief → `[brief]`; implementation → `filesWritten: brief.files_to_change` plus `commandSucceeded: brief.verify_command`; testing → `testReport: testResults`; the final edge to `end` → `reviewJudgement` (or another check of the result).
3. Artifacts used in gates should declare a `shape`, so a malformed file is caught at the handoff rather than by the next agent.
4. `commandSucceeded` with a literal pattern (`"build|test"`) is weaker than a field reference (`brief.verify_command`); prefer the brief's command when the Planner writes one.

#### B. Loops

1. Every back-edge (Tester → Developer, Reviewer → Planner) needs a `when` that says when to take it; otherwise agents guess.
2. A pair of agents that can bounce work forever needs a way out: `maxRevisits` on the back-edge, or `onStuck` on the forward edge where a third agent can break the deadlock.
3. With 3+ agents, check `advanced.failureHandling` sets `maxConsecutiveContractFailures` (e.g. 6) and `maxConsecutiveTurnsWithoutSignal` (e.g. 8). Without them a gate that can never pass, or an agent that never hands off, runs until `limits.maxTurns`.
4. `noProgress` should be `action: Abort`, not `Reinstruct`: an agent that hands off again without calling any tool won't fix itself through more corrections.

#### C. Tools versus instructions

For each agent, check its `tools` cover what its instructions ask for:

| Instructions mention | Needs |
|----------------------|-------|
| `read_file`, `write_file`, `patch_file`, `list_files` | `FileSystem` |
| `shell_run`, `shell_run_script` | `Shell` |
| `git_commit`, `git_status`, … | `Git` |
| `changes_read`, `changes_read_latest` | `Changes` |
| `subagent_explore`, `subagent_locate` | `Subagent` |
| `scratchpad_*` | `Scratchpad` |
| `decision_*` | `Decision` (`decision_create`/`decision_supersede` need the `write` capability if `capabilities` restricts it) |
| `graph_*` | `Graph` (needs `fuseraft graph build`) |
| `objective_*` | `Objective` |
| a skill, e.g. `load_skill("git-commit")` | the skill installed (see `fuseraft skills list`) |

An agent that writes an artifact needs `FileSystem`. Agents never need `Handoff` listed.

#### D. Instructions

1. **Routing in instructions:** flag `handoff(route_keyword: "...")` lines and "write X on its own line" signals. fuseraft generates routes and gates into each agent's prompt; hand-written ones drift. Any route named must be one of the agent's own edges (`InitTemplatesTests` checks this for shipped files).
2. **Paths and shapes in instructions:** flag paths or JSON formats that duplicate an artifact's `path`/`shape`. Declare them once under `artifacts` and list them in `reads`/`writes`.
3. **Vague work:** "write the implementation" with no files named causes `filesWritten` failures. Point at the brief's `files_to_change` instead.
4. **`functionChoice`:** agents that must act every turn (Developer, Tester) should set `advanced.functionChoice: required`.
5. **Length:** warn above ~50 lines; long instructions bury the important steps.
6. **Parallel branches:** agents that only appear in a parallel `to: [...]` list run one turn and return; their instructions shouldn't talk about handing off.

#### E. Context

1. Agents default to `sees: handoff`. Check each one can do its job from the task or handoff note, its `reads` artifacts and its own recent turns. An agent that needs a file it doesn't `read` will rediscover it every turn.
2. `sees: conversation` should be rare and deliberate. A reviewer or tester on the conversation can mistake an earlier agent's claim for a fact.
3. A Verifier that audits other agents' claims needs to see them: `sees: fork`.
4. `advanced.contextWindow` only affects agents that see the conversation; on a `handoff` agent it does nothing useful.

#### F. Limits, compaction and budget

1. `limits.maxTurns` should fit the work: 40 suits a dev pipeline; raise it only for long research or generation.
2. With `advanced.compaction`, `triggerTurnCount` must exceed `keepRecentTurns`. For `steps` flows prefer `mode: lossless` or `hybrid`; for patterns, `intent` or `llm`.
3. With `advanced.contextBudget`: `cutoverAt` needs `compaction`; `warnAt < cutoverAt`; `warnTurnTokens < cutoverAt`; `maxSingleTurnInputTokens` around 1.5–2× `cutoverAt`.

#### G. Models and remote agents

1. Every model should resolve: an alias from `models`, a model ID with a known provider prefix, or a map with an `endpoint`. Flag API key env vars that aren't set (`fuseraft validate -c` checks keys live).
2. `remoteAgent` uses a pre-release A2A SDK; note it, and confirm the `url` is reachable from where the team runs.

### Step 5: Report Findings

Group by severity, quote the relevant field, and give the exact fix:

**Errors** (the run will fail or stall): validation errors; an agent missing a tool its instructions depend on; an instruction naming a route the agent doesn't have.

**Warnings** (likely wrong behaviour): forward edges with no gates; loops with no exit; no failure-handling backstops on 3+ agents; `functionChoice` unset on acting agents; reviewers or testers on `sees: conversation`.

**Suggestions**: shapes for gated artifacts; routing or paths duplicated in instructions; long instructions; a better compaction mode.

### Step 6: Offer Fixes

Offer to apply error-level fixes with `patch_file` or `write_file`, confirm with the user before writing, then re-run `fuseraft validate`.

## References

- Field reference: `docs/configuration.md`
- Gates in depth: `docs/validators.md`
- Flows: `docs/strategies.md`
- Plugin tool names: `docs/plugins.md`
