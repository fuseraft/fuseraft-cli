---
name: craft-orchestration
description: Build a working fuseraft team file (YAML) for a multi-agent pipeline. Trigger when the user asks to create, scaffold, or design a team file, an orchestration, a fuseraft config, or a multi-agent workflow.
---

# Craft a Team File

Build a valid, runnable team file by gathering requirements, generating the YAML, validating it, and writing it to disk.

## Purpose

A team file declares agents, the files they hand each other (artifacts), and the flow between them, with gates that block a handoff until the evidence exists. This skill drives the process: ask the right questions, generate the YAML, validate it, and write it so the user can run it immediately.

## When to Use

Use this skill when the user asks to:
- Create or scaffold a fuseraft team file or orchestration
- Set up a multi-agent pipeline
- Design a new agent workflow for a project
- Convert a described workflow into a runnable config

Do **not** use this skill to modify an existing team file; edit it directly. If the user has a file starting with `Orchestration:`, that's the old format: run `fuseraft migrate <file>` first.

## Workflow

### Step 1: Gather Requirements

Ask these questions. If the user already described the workflow, extract the answers instead of asking again.

**Agents and flow**
- Which agents, and what does each one do?
- What does each agent leave behind for the next one? (A brief, code, a test report, a review.) These become **artifacts**.
- Where can the work loop back? (Tester → Developer when a test fails, Reviewer → Planner when the plan is wrong.)
- Where does the run finish? Every flow needs an edge to `end`.
- Can any step fan out to agents that work in parallel and are then joined?
- Is this really a fixed flow? If a manager model should decide who works each round, use `pattern: magentic`; for the same work over many items, `mapreduce`; for several independent opinions, `scattergather`.

**Model**
- Which provider and model? One model for all agents, or different ones per agent (a fast model for cheap steps, a reasoning model for review)?

**Tools per agent**
- `FileSystem`, `Shell`, `Git`, `Search`, `Http`, `Json`, `Document`, `Scratchpad`, `Subagent`, `Changes` (read the change log; give it to agents that check others' work), `Decision`, `Graph`, `Objective`, or an MCP server's name.
- Don't list `Handoff`: every agent in a `steps` flow gets it automatically.

**Evidence** (skip for quick prototypes)
- Which handoffs need proof? A brief must exist, every planned file written, the verify command passing, a test report with no failures, a structured review verdict.

**Output path**: default `.fuseraft/config/team.yaml`.

### Step 2: Build the YAML

Load `references/schema-cheatsheet.md` for the full field reference. Apply these rules:

1. **Declare models once** under `models:` and reference them by alias (`model: fast`).
2. **Declare every artifact once** under `artifacts:` with a `path` and a `shape`. List it under the writer's `writes` and the readers' `reads`. fuseraft tells each agent the path and shape, so **don't repeat them in `instructions`**.
3. **Draw the flow** under `flow: { Start, Steps }`. Each step is an agent name with a list of edges `{ to, when, requires, onStuck }`. Give back-edges a `when` that says when to take them.
4. **Gate handoffs** with `requires:`: the artifact name, `filesWritten: brief.files_to_change`, `commandSucceeded: brief.verify_command`, `testReport: testResults`, `reviewJudgement`. Use `onStuck: <agent>` where another agent can fix a gate that keeps failing.
5. **Don't write routing into instructions.** fuseraft generates each agent's routes, when to take them and what each requires. Instructions describe the work: what to read, what to do, which tools to use.
6. **Agents see their handoff by default** (`sees: handoff`): the task or handoff note, their `reads` artifacts and their own recent turns. Use `sees: conversation` only for agents that need the discussion; a Verifier that audits other agents' claims uses `sees: fork`.
7. **Set `advanced.functionChoice: required`** on agents that must act every turn (Developer, Tester).
8. **Set `limits.maxTurns`** (40 suits a dev pipeline).
9. **For long runs**, add `advanced.compaction` (`mode: lossless` for Steps flows) and `advanced.contextBudget` (`warnAt: 60000`, `cutoverAt: 100000`).
10. **For pipelines of 3+ agents**, add `advanced.failureHandling` with `maxConsecutiveContractFailures: 6` and `maxConsecutiveTurnsWithoutSignal: 8`, so a gate that can't pass, or an agent that never hands off, stops the run instead of looping.
11. **Parallel fan-out**: `- to: [A, B]` with `join: C`. Branch agents run one turn each and need no `steps` entry of their own. `merge: { strategy: union }` concatenates outputs; `ranked` and `semantic_diff` need `merge.agent`.

Write each agent's instructions like this, under ~25 lines:

```
You are a <role>.

1. <first action, e.g. read what changed with changes_read_latest>
2. <main work, naming the tools to call>
3. <what must be true before handing off>
```

### Step 3: Validate

```bash
fuseraft validate <output-path>
```

Fix every error before finishing. Errors name the exact field: an unknown field, an agent or artifact that isn't declared, a gate that isn't a real check, an edge to an agent with no `steps` entry, or a flow with no edge to `end`. `fuseraft validate <path> --diagram` prints the flow as a Mermaid chart.

### Step 4: Write and Confirm

1. Call `write_file` to save the YAML.
2. Show the run command: `fuseraft run --config <output-path> "Your task here"`.
3. Mention what to adjust before a real run: API key env vars, `modelId`/`endpoint` for their provider, and any project-specific commands in the instructions.

## References

- `references/schema-cheatsheet.md` — every field, the gate checks, patterns, tools and provider settings
- `config/examples/` in the fuseraft-cli repo — complete, tested team files
