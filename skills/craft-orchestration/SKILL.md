---
name: craft-orchestration
description: Build a working fuseraft orchestration YAML config for a multi-agent pipeline. Trigger when the user asks to create, scaffold, or design an orchestration file, a fuseraft config, or a multi-agent workflow.
---

# Craft Orchestration

Build a valid, runnable `orchestration.yaml` by gathering requirements through targeted questions, generating the config, validating it, and writing it to disk.

## Purpose

An orchestration file wires together agents, models, routing, validators, and termination. Getting all the pieces right from scratch is tedious. This skill drives the process — ask the right questions, generate the YAML, validate it, and write it to disk so the user can run it immediately.

## When to Use

Use this skill when the user asks to:
- Create or scaffold a fuseraft orchestration file
- Set up a multi-agent pipeline
- Design a new agent workflow for a project
- Convert a described workflow into a runnable config

Do **not** use this skill to modify an existing config — use `patch_file` or `write_file` directly for edits.

## Workflow

### Step 1: Gather Requirements

Ask these questions. If the user already described the workflow in detail, extract answers from their description instead of asking again.

**Pipeline topology**
- How many agents? What are their names and roles?
- Is this a linear pipeline (A → B → C), does it branch (retry loops, recovery agents), or does it fan out to parallel work?
- Parallel fan-out: does any step produce N independent results that can later be combined? (e.g., backend + frontend + migration written at the same time) If so, note which step fans out, which agents run concurrently, and how outputs should be merged (union = concatenate all; ranked = pick best; semantic_diff = LLM resolves conflicts).

**Model**
- Which provider and model? (xAI Grok, Claude, OpenAI, Ollama, etc.)
- One model for all agents, or different models per agent (e.g. fast model for cheap steps, reasoning model for review)?

**Routing strategy**
- Keyword routing: agents emit a keyword string; simple, good for linear flows.
- State machine routing: explicit states and transitions; good for branching, recovery agents, or terminal states.
- Ask only if the user hasn't indicated a preference. Default to state machine for pipelines with 3+ agents or any retry logic.

**Plugins per agent**
- Which agents need filesystem access (`FileSystem`)?
- Which need shell commands (`Shell`)?
- Which need git (`Git`)?
- Which need web search or HTTP (`Search`, `Http`)?
- Which need scratchpad memory across sessions (`Scratchpad`)?
- Add `Handoff` to every agent that advances the pipeline.

**Validators / evidence contracts** (ask only if the user wants enforcement — skip for simple prototypes)
- Should handoffs be blocked until files are written, shell commands pass, or a brief exists?
- Should the test handoff require a valid test report?

**Output path**
- Default: `.fuseraft/config/orchestration.yaml`

### Step 2: Choose a Skeleton

Pick the appropriate skeleton based on routing type and agent count. Load `references/schema-cheatsheet.md` for the full field reference if needed.

**Keyword routing** — simple linear flow, 2–4 agents, no recovery loops.

**State machine** — any pipeline with retry logic, recovery agents, branching transitions, or terminal states. Preferred when 3+ agents are involved.

**State machine with parallel fan-out** — use when two or more agents can do independent work simultaneously and their outputs need to be combined before the pipeline continues. The fan-out transition uses `Parallel: true`, lists branch states in `Targets`, and sets `To` to the join state entered after merge.

### Step 3: Build the YAML

Construct the YAML from the gathered answers. Apply these rules:

1. **Name model aliases** under `Models:` and reference them by alias in each agent's `Model.ModelId` — avoids repeating endpoint and API key.
2. **Add `Handoff` to every agent** that needs to advance the pipeline. Agents call `handoff(route_keyword: "KEYWORD")` — the keyword must match the `Signal` (state machine) or `Keyword` (keyword routing) exactly.
3. **Set `FunctionChoice: required`** on agents that must call at least one tool every turn (Developer, Tester).
4. **Include `ChangeTracking`** when agents use `changes_read` / `changes_read_latest` (the `Changes` plugin), or when validators like `TestReportValid` or `RequireAllFilesWritten` perform cross-session checks.
5. **Include `EvidenceStore`** when using evidence contracts or lossless compaction.
6. **Include a `Validation` section** whenever `TestReportValid`, `RequireBrief`, `RequireAllFilesWritten`, or `RequireAcceptanceCriteriaPassedValidator` are used.
7. **Always include a `Termination` block** — use `MaxIterations` as a hard cap (40 is a safe default for dev pipelines).
8. **Include `FailureHandling`** for any pipeline longer than 2 agents to prevent infinite reinstruct loops. Always set both global backstops:
   - `MaxConsecutiveContractFailures: 6` — prevents a `Reinstruct` policy from looping indefinitely when a contract cannot be satisfied.
   - `MaxConsecutiveTurnsWithoutSignal: 8` — escalates to HITL when an agent completes work but never calls `handoff()`. This counter survives compaction cycles; the built-in loop warning does not.
9. **Include `ContextBudget`** when using `Compaction`. Recommended defaults: `WarnAt: 60000`, `CutoverAt: 100000`, `MaxSingleTurnInputTokens: 200000`. Keep `WarnTurnTokens` (top-level) below `CutoverAt` so the per-turn warning fires before compaction is forced.
10. **Parallel fan-out rules** (state machine only):
   - Put `Parallel: true`, `Targets: [BranchStateA, BranchStateB, ...]`, and `To: JoinState` on the triggering transition. `To` is the join state entered after all branches finish — it is **not** a branch target.
   - Each branch state must be declared in `States` with an `Agent`. Branch agents run for **one turn only** with an isolated history snapshot — do **not** instruct them to emit a handoff signal.
   - Branch agents do not need the `Handoff` plugin.
   - If `Merge.Strategy` is `ranked` or `semantic_diff`, set `Merge.Agent` to a named agent (declared in `Agents`) that will evaluate or reconcile the outputs. This agent needs no special plugins — it receives the branch outputs as context and returns text.
   - `Merge.Strategy: union` (default) concatenates all branch outputs in declaration order — no merge agent needed.

Write instructions for each agent using this pattern:
```
You are a <role>.

FOLLOW THESE STEPS IN ORDER:
1. <first action — usually read something from disk>
2. <main work>
...
N. HAND OFF: Call handoff(route_keyword: "<KEYWORD>").
```

Keep instructions under 30 lines per agent. Name specific tools to call (e.g. `read_file`, `write_file`, `shell_run`) and the exact keyword to emit. Be explicit about what to write to disk before handing off — vague instructions cause validator failures.

### Step 4: Validate

After generating the YAML, call `shell_run` to validate it:

```bash
fuseraft validate <output-path>
```

Fix all reported errors before writing the file. Common issues:
- Route keyword mismatch: agent instructions say `"HANDOFF TO X"` but config uses a different string
- Missing `Validation` section when `TestReportValid` or `RequireBrief` is used
- Missing `ChangeTracking` when `Changes` plugin is listed or when `TestReportValid` cross-references `changes.json`
- Agent references a plugin that is not in its `Plugins` list
- `EvidenceStore` missing when `Contracts` reference `FilesWritten` or `TestReport` predicates

### Step 5: Write and Confirm

1. Call `write_file` to save the YAML to the output path.
2. Show the user the command to run it:
   ```bash
   fuseraft run --config <output-path> "Your task here"
   ```
3. Show the validate command for CI:
   ```bash
   fuseraft validate <output-path>
   ```
4. Briefly explain what the user should adjust before their first real run:
   - Set any required API key env vars
   - Update `ModelId` / `Endpoint` if they are using a different provider
   - Replace placeholder acceptance criteria in agent instructions with task-specific ones

## References

- `references/schema-cheatsheet.md` — Quick-reference for all config sections, plugin names, validator names, routing patterns, and common providers
