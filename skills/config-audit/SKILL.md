---
name: config-audit
description: Review an existing fuseraft orchestration YAML or JSON config for correctness before running it. Trigger when the user wants to validate, review, or sanity-check an orchestration config, or when fuseraft validate passes but the run still fails unexpectedly.
---

# Config Audit

Run `fuseraft validate`, then perform a deeper semantic audit — keyword alignment, missing required blocks, validator prerequisites, and instruction quality — before the user burns tokens on a broken config.

## When to Use

Use this skill when:
- The user wants to review a config before running it
- `fuseraft run` fails immediately or after one turn for reasons that look config-related
- The user just wrote or modified an orchestration config and wants a second opinion
- The config passed `fuseraft validate` but the run behaves incorrectly

Do **not** use this skill to create a config from scratch — use `craft-orchestration` for that.

## Workflow

### Step 1: Locate the Config

If the user gave a path, use it. Otherwise check for the default:

```bash
ls .fuseraft/config/
```

If multiple configs exist, ask the user which one to audit.

### Step 2: Run the Built-in Validator

```bash
fuseraft validate <config-path>
```

Fix any errors reported here before continuing. Schema errors (unknown fields, wrong types, missing required fields) are shown with line numbers. Common ones:

- `TriggerTurnCount` must be greater than `KeepRecentTurns` — adjust the compaction settings
- `SystemPromptPath` file does not exist — fix the path or remove the field
- Agent references a model alias not defined in `Models` — add the alias or use a direct `ModelId`

### Step 3: Read the Config

Call `read_file` on the config. Parse it mentally into its major sections: `Agents`, `Selection`, `Termination`, `Validation`, `ChangeTracking`, `EvidenceStore`, `Compaction`, `FailureHandling`, `Contracts`.

### Step 4: Semantic Audit

Work through each check category below. Note every issue found.

---

#### A. Routing keyword alignment

For **keyword routing** (`Selection.Type: keyword`):

1. Extract every `Keyword` from `Selection.Routes`.
2. For each keyword, find the agent that should emit it (the `SourceAgents` entry).
3. Read that agent's `Instructions` and verify the exact keyword string appears verbatim.
   - **Issue:** Instructions say `"HAND OFF TO DEVELOPER"` but the route has `Keyword: "HANDOFF TO DEVELOPER"` — they must match exactly, including spaces and casing.
4. Verify the `Agent` the route points to is a real agent name in `Agents`.
5. Verify `SourceAgents` contains real agent names.

For **state machine routing** (`Selection.Type: statemachine`):

1. Extract every `Signal` from each state's `Transitions`.
2. For each **sequential** transition (no `Parallel: true`), find the source state's agent and verify the signal appears in their instructions.
3. Verify every `To` state name is declared in `States` — no dangling references.
4. Verify the initial state is defined.
5. For each **parallel** transition (`Parallel: true`):
   - Verify `Targets` is non-empty and every entry names a declared state.
   - Verify `To` (the join state) is declared in `States` and is distinct from all `Targets` entries.
   - If `Merge.Strategy` is `ranked` or `semantic_diff`, verify `Merge.Agent` is set and names a real agent in `Agents`.
   - Check that branch agents' instructions do **not** instruct them to emit a handoff signal — branch agents run for exactly one turn with no signal evaluation; a handoff call will be ignored and may confuse the agent.

---

#### B. Plugin prerequisites

For each agent:

1. **`Handoff` plugin:** Any agent whose instructions tell it to call `handoff(...)` must have `Handoff` in its `Plugins` list. If an agent is the final agent (no outgoing route), it does not need `Handoff`.
2. **`FileSystem` plugin:** Agent instructions that mention `read_file`, `write_file`, `patch_file`, `list_files`, etc. require `FileSystem`.
3. **`Shell` plugin:** Instructions mentioning `shell_run` or `shell_run_script` require `Shell`.
4. **`Git` plugin:** Instructions mentioning `git_commit`, `git_status`, etc. require `Git`.
5. **`Changes` plugin:** Instructions mentioning `changes_read` or `changes_read_latest` require both `Changes` in `Plugins` and `ChangeTracking` in the config.
6. **`Scratchpad` plugin:** Instructions mentioning `scratchpad_read` or `scratchpad_write` require `Scratchpad`.
7. **`Decision` plugin:** Instructions mentioning `decision_search` or `decision_read` require `Decision`. Instructions using `decision_create` or `decision_supersede` additionally require the `write` capability (`Capabilities: {Decision: [read, write]}` or no `Capabilities` restriction for that plugin).
8. **`Graph` plugin:** Instructions mentioning `graph_search`, `graph_refs`, or `graph_dependents` require `Graph`. All three tools are read-only; no capability restriction needed.
9. **`Objective` plugin:** Instructions mentioning `objective_create`, `objective_read`, `objective_update`, `objective_list`, or `objective_link_task` require `Objective`. Objective tools are not in the capability map — they cannot be restricted and are always passed through.

---

#### C. Validator prerequisites

Check these dependency rules. Each failing check is a guaranteed runtime error:

| Validator / Predicate | Requires |
|----------------------|----------|
| `RequireBrief` | `Validation.BriefPath` set |
| `RequireAllFilesWritten` | `Validation.BriefPath` set |
| `RequireAcceptanceCriteriaPassedValidator` | `Validation.BriefPath` + `Validation.ChangeLogPath` + `ChangeTracking` |
| `TestReportValid` | `Validation` section with `TestReportPath` |
| `TestReportValid` (check 8) | also `Validation.ChangeLogPath` + `ChangeTracking` |
| `RequireReviewJudgement` (coverage check) | `Validation.BriefPath` |
| `RequireRelatedTestsPass` | `TestSelector` config + `ChangeTracking` |
| `FilesWritten` contract predicate | `EvidenceStore` |
| `TestReport` contract predicate | `EvidenceStore` |
| `RelatedTestsPass` contract predicate | `EvidenceStore` + `TestSelector` + `ChangeTracking` |
| `CommandSucceeded` contract predicate | `ChangeTracking` |
| `Compaction.Mode: intent` | `ChangeTracking` |
| `Compaction.Mode: lossless` | `EvidenceStore` + state machine selection |
| `Compaction.Mode: hybrid` | `EvidenceStore` + state machine selection + `ChangeTracking` |

Also verify:
- `Validation.ChangeLogPath` matches `ChangeTracking.Path` (they should point to the same file).
- `Validation.BriefPath` matches the path the Planner agent is instructed to write.
- `Validation.TestReportPath` matches the path the Tester agent is instructed to write.

---

#### D. Termination safety

1. **Hard cap:** Every config must have a `MaxIterations` ceiling — either directly on `Termination` or as a `maxiterations` child strategy. Without it a stuck pipeline runs forever.
   - Safe default for dev pipelines: 40. Adjust upward only for long research or generation tasks.
2. **Regex termination:** If `Termination.Type: regex` is used without a `maxiterations` sibling, flag it — a model that never emits the pattern runs indefinitely.
3. **Compaction:** If `Compaction` is configured and `Mode` is not `window`, verify `TriggerTurnCount > KeepRecentTurns`.

---

#### E. Context budget

If `ContextBudget` is present, verify:

1. **Compaction required:** `CutoverAt > 0` or `MaxSingleTurnInputTokens > 0` without a `Compaction` section is an error — `fuseraft validate` now catches this, but flag it here too.
2. **WarnAt < CutoverAt:** both fields non-zero and `WarnAt >= CutoverAt` is an error.
3. **WarnTurnTokens < CutoverAt:** if `WarnTurnTokens` (top-level) is set alongside `CutoverAt`, verify `WarnTurnTokens < CutoverAt`. Equal or greater means the per-turn warning is useless — it fires in the same turn as compaction.
4. **MaxSingleTurnInputTokens > CutoverAt:** a sensible value is 1.5–2× `CutoverAt`. Setting it equal to or below `CutoverAt` means every turn that hits the cumulative cutover also hits this ceiling — they fire together with no differentiation.
5. **Compaction mode vs selection type:** `Mode: lossless` requires a state machine snapshotter. Graph sessions (`Selection.Type: graph`) have no snapshotter — they silently fall back to LLM compaction. Use `Mode: intent` for graph sessions with `ChangeTracking`, or `Mode: llm` if no change tracking is configured.

#### F. Failure handling

For pipelines with **3 or more agents**, the absence of `FailureHandling` means a `Reinstruct` action has no exit condition — a contract that can never be satisfied will loop until `MaxIterations` kills the session. Check the following:

1. **Global backstops present:** verify `MaxConsecutiveContractFailures` and `MaxConsecutiveTurnsWithoutSignal` are set.
   - `MaxConsecutiveContractFailures` — fires HITL when any single transition accumulates this many consecutive contract failures regardless of the per-type action. Without it, a `Reinstruct` policy loops indefinitely.
   - `MaxConsecutiveTurnsWithoutSignal` — fires HITL when the active-state agent runs this many consecutive turns without emitting any routing signal (the "silent stuck" case — agent completed work but never called handoff). This counter lives in strategy state and survives compaction cycles, unlike the loop-warning injection which resets after each compaction.

   ```yaml
   FailureHandling:
     MissingEvidence:
       Action: Reinstruct
       Threshold: 3
     NoProgress:
       Action: Abort
       Threshold: 3
     MaxConsecutiveContractFailures: 6   # backstop for stuck contracts
     MaxConsecutiveTurnsWithoutSignal: 8 # backstop for silent stuck agents
   ```

2. **Per-type thresholds reasonable:** `NoProgress` should be `Abort` not `Reinstruct` — an agent that re-emits a handoff without any tool calls cannot self-correct through reinstructions.

3. **WarnTurnTokens vs CutoverAt:** if `ContextBudget.CutoverAt` is set, verify `WarnTurnTokens < CutoverAt`. If `WarnTurnTokens >= CutoverAt`, the per-turn warning fires in the same turn as compaction and gives no advance signal. `fuseraft validate` now surfaces this as a warning.

---

#### G. Instruction quality

For each agent, read `Instructions` and flag:

1. **Missing handoff call:** Instructions describe work but never say to call `handoff(route_keyword: "...")`. An agent with no handoff instruction will never advance the pipeline.
2. **Vague file references:** Instructions say "write the implementation" but don't name a path. Vague instructions cause validator failures (`RequireWriteFile` passes, but `RequireAllFilesWritten` fails because the wrong file was written).
3. **FunctionChoice:** Agents expected to call tools every turn (Developer, Tester) should have `FunctionChoice: required`. Without it the model may produce a text-only response that satisfies no validator.
4. **Instruction length:** Warn if an agent's instructions exceed ~50 lines — long instructions crowd the context and cause the model to lose track of the handoff step.
5. **Parallel branch agents:** For any agent that only appears in `Targets` lists (never as the primary `Agent` of a non-parallel state), verify their instructions do **not** tell them to call `handoff(...)` or emit a transition signal. Branch agents run one turn and return — no signal is evaluated. Instructing them to hand off is misleading and may waste turns on a tool call that has no effect.

---

#### H. Model aliases

1. Every `Model.ModelId` in agents must either be a direct provider model ID (e.g. `gpt-4o`, `claude-sonnet-4-6`) or an alias defined in `Models`.
2. Every alias in `Models` must have a `ModelId` field.
3. If `Compaction.Model` is set, apply the same check.
4. Flag any model that likely requires an API key env var not mentioned in the config or a local `README`.

---

#### I. SchemaVersion

`SchemaVersion` is optional. If present:

1. Verify the value matches a version recognized by the current fuseraft-cli build (e.g. `"2026-05"`). An unrecognized value causes a `LogWarning` at startup and may indicate the config was written for a newer or older build.
2. If absent, note this as a suggestion — setting it makes version drift visible across upgrades.

---

#### J. RemoteAgent (preview)

For any agent where `RemoteAgent` is set:

1. Flag it as a **preview feature** — fuseraft-cli emits a `LogWarning` at session startup for every agent using `RemoteAgent` because the A2A SDK dependency (`1.0.0-preview2`) may have breaking changes in future releases.
2. Verify that `Model`, `Plugins`, `FunctionChoice`, `Capabilities`, `SubAgentModel`, and `SubAgentPlugins` are **not** set on that agent — those fields are silently ignored when `RemoteAgent` is present, which can mislead readers into thinking tool access or model selection is in effect.
3. Confirm `RemoteAgent.Url` is set and reachable in the target environment.

---

### Step 5: Report Findings

Group findings by severity:

**Errors** (will cause runtime failure):
- Missing `Validation` section for validators that require it
- Missing `ChangeTracking` for validators/predicates that require it
- Missing `EvidenceStore` for contract predicates that require it
- Agent missing `Handoff` plugin
- Route keyword mismatch between instructions and config
- Undefined agent name in `SourceAgents` or route `Agent` field

**Warnings** (will likely cause unexpected behavior):
- No `MaxIterations` hard cap
- 3+ agents with no `FailureHandling`
- `FunctionChoice` absent on Developer/Tester agents
- Vague path references in instructions
- `Validation.ChangeLogPath` ≠ `ChangeTracking.Path`
- `SchemaVersion` set to an unrecognized value (startup `LogWarning` emitted; check build compatibility)
- `RemoteAgent` present with ignored fields (`Model`, `Plugins`, `FunctionChoice`, etc.) still set

**Suggestions** (improvement opportunities):
- Instructions longer than 50 lines
- Compaction mode `llm` on a state machine config (suggest `lossless` or `hybrid`)
- No `Description` on the orchestration or agents
- `SchemaVersion` absent (recommend setting it for upgrade safety)
- `RemoteAgent` in use — note the A2A pre-release status and recommend verifying SDK compatibility before production use

For each finding, quote the relevant config field and give the exact fix to apply.

### Step 6: Offer to Apply Fixes

After reporting, offer to apply any error-level fixes directly using `patch_file` or `write_file`. Confirm with the user before writing. After applying, re-run `fuseraft validate` to confirm the config is clean.

## References

- Full field reference: `docs/configuration.md`
- Validator prerequisites: `docs/validators.md`
- Plugin tool names: `docs/plugins.md`
- Routing strategies: `docs/strategies.md`
