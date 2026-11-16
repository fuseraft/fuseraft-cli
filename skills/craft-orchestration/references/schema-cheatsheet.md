# Team File Schema Cheatsheet

Quick reference for writing a fuseraft team file. Full reference: `docs/configuration.md`. Unknown fields are load errors; `fuseraft validate <file>` names the exact field.

## Top-level structure

```yaml
team: <name>                      # required
description: <one line>

models:                           # aliases, referenced by name
  fast:      { modelId: grok-4.3, endpoint: https://api.x.ai/v1, apiKeyEnvVar: XAI_API_KEY, reasoningEffort: none }
  reasoning: { modelId: grok-4.3, endpoint: https://api.x.ai/v1, apiKeyEnvVar: XAI_API_KEY, reasoningEffort: low }

artifacts:                        # files agents hand each other, declared once
  brief:
    path: .fuseraft/artifacts/brief.json
    shape:
      goal: string
      files_to_change: [string]
      acceptance_criteria: [string]
      verify_command: string
  testResults:
    path: .fuseraft/artifacts/test-report.json
    shape:
      results:
        - criterion: string
          status: PASS|FAIL
          command: string

agents:                           # required; a map keyed by agent name
  Planner:
    role: Scopes the task and writes the brief.
    model: fast
    tools: [FileSystem, Search, Subagent]
    writes: [brief]
    instructions: |
      Explore the codebase, then write a focused brief for the task.

flow:                             # required
  start: Planner
  steps:
    Planner:
      - to: end
        requires: [brief]

limits:
  maxTurns: 40
  maxTokens: 2000000

security:
  fileSystemSandboxPath: .

mcpServers: []

advanced: {}
```

## Agent fields

| Field | Notes |
|-------|-------|
| `role` | One-line description. |
| `model` | Alias, model ID, or map (`{ modelId: fast, maxTokens: 8192 }`). Omit to use the user's default model. |
| `tools` | Plugin names or MCP server names. `Handoff` is added automatically for flow agents. |
| `reads` / `writes` | Artifact names. Reads are included in context; writes get their path and shape in the prompt. |
| `sees` | `handoff` (default), `conversation`, `fork`. Magentic agents default to `conversation`. |
| `instructions` | Required. Describe the work, not the routing. |
| `agentFile` | Load fields from another YAML file; inline fields win. |
| `remoteAgent` | `{ url, timeoutSeconds, headers }`. Can't be combined with `model`, `tools`, `sees`, `advanced`. |
| `advanced` | `functionChoice` (auto/required/none), `maxInTurnToolPairs`, `maxToolCallsPerTurn`, `maxInTurnContextTokens`, `capabilities`, `contextWindow`, `context`, `trustScore`, `subagentModel`, `subagentPlugins`, `subagentMaxToolCalls`, `skipExecutionState`, `knowledgeWeight`. |

## Sees

| Value | The agent gets |
|-------|----------------|
| `handoff` | The task or the previous agent's `handoff` goal/background/constraints, its `reads` artifacts, its own last 4 turns, corrections addressed to it, and any `advanced.context` sources. Never other agents' transcripts. |
| `conversation` | The whole shared conversation (filtered by `advanced.contextWindow`). |
| `fork` | The shared conversation plus the handoff directive. Use for a Verifier that audits other agents' claims. |

`advanced.context` sources: `session_context`, `changes_recent:N`, `brief_field:FIELD`, `file:PATH`, `own_history:N`, `execution_state`, `investigation_log`.

## Shape syntax

| Sketch | Matches |
|--------|---------|
| `string`, `int`, `number`, `bool`, `any` | That type |
| `A\|B` | One of those strings |
| `[T]` | A list of `T` |
| nested map | An object with those fields (extra fields allowed) |
| trailing `?` | Optional field |

## Flow: Steps

```yaml
flow:
  start: Planner
  steps:
    Planner:
      - to: Developer
        requires: [brief]
    Developer:
      - to: Tester
        requires:
          - filesWritten: brief.files_to_change
          - commandSucceeded: brief.verify_command
      - to: Planner
        when: the plan needs rethinking
    Tester:
      - to: Reviewer
        when: every criterion passes
        requires:
          - testReport: testResults
        onStuck: Developer
      - to: Developer
        when: a criterion fails
    Reviewer:
      - to: end
        when: every criterion is verified
        requires: [reviewJudgement]
      - to: Developer
        when: the code needs fixing
      - to: Planner
        when: the plan is fundamentally wrong
```

| Edge field | Notes |
|------------|-------|
| `to` | Agent, `end`, or `[A, B]` for parallel branches. |
| `when` | When to take the edge; shown to the agent. |
| `requires` | Gates; all must pass. |
| `onStuck` | Agent called in after two consecutive gate failures on this edge, or `abort`. |
| `name` | Route name; defaults to the target. Needed only for two edges to the same target. |
| `join` | Required with a parallel `to`: the agent that runs after the branches. |
| `merge` | Parallel only: `{ strategy: union }` (default), `ranked` or `semantic_diff` (both need `agent`). |
| `maxRevisits` | On a loop, escalate after N repeats, quoting the step agent's first `writes` artifact. |
| `approve` | `true`: a person approves the handoff after its gates pass; a refusal sends the work back. |

Rules: every agent an edge targets needs its own `steps` entry (except parallel branches); at least one edge must go to `end`; an agent can only hand off on its own step's routes.

## Flow: Patterns

```yaml
flow: { pattern: magentic, model: reasoning, maxRoundCount: 20, maxStallCount: 3, maxResetCount: 2 }
flow: { pattern: mapreduce, splitter: S, mapper: M, reducer: R, itemsJsonPath: items, maxConcurrency: 4 }
flow: { pattern: scattergather, participants: [A, B, C], synthesizer: Lead }
flow: { pattern: adversarial, rounds: 3, passKeyword: APPROVED, stages: [{ generator: Dev, critic: Reviewer }] }
```

Pattern agents never call `handoff`; patterns end on their own.

## Gates (`requires`)

| Item | Passes when |
|------|-------------|
| `<artifact>` | File exists and matches its `shape`. |
| `fileExists: <path>` | File exists. |
| `filesWritten: <artifact>.<field>` | Every path listed in that field was written this run. |
| `checklistComplete: <artifact>.<field>` | Every file-writing step in that field was written. |
| `commandSucceeded: <artifact>.<field>` or `"<pattern>"` | A matching shell command exited 0 this run. Option `fallback: <pattern>`. |
| `testReport: <artifact>` | Report exists; options `noFailures` and `hasAssertions` (both default `true`: no FAIL, every PASS names a command that ran). |
| `relatedTestsPass` | Tests for changed files pass. Needs `advanced.testSelector`. |
| `acceptanceCriteriaPassed: <brief>` | Criteria with `expected_output_contains` were met by real command output. |
| `reviewJudgement` | The agent's reply has a ```` ```json {"review": [...]} ```` block, no FAIL, one entry per brief criterion, and a successful shell run this turn if any PASS. |
| `anyFileWritten` | The agent wrote a file this turn. Option `fallback: <pattern>`. |
| `noRepeatedShellFailures: <pattern>` | Matching commands aren't failing turn after turn. |
| `sessionContextWritten` | The agent called `session_context_write` this turn. |
| `architecture` | Changes follow the project's architecture rules. |

## Advanced

| Key | Purpose |
|-----|---------|
| `compaction` | `{ triggerTurnCount: 30, keepRecentTurns: 8, mode: lossless\|intent\|hybrid\|llm\|window, model: <alias> }` |
| `contextBudget` | `{ warnAt: 60000, cutoverAt: 100000, maxSingleTurnInputTokens: 200000 }`; `cutoverAt` needs `compaction`. |
| `failureHandling` | `{ missingEvidence: { action: Reinstruct, threshold: 3 }, conflictingEvidence: { action: Reinstruct, threshold: 2 }, noProgress: { action: Abort, threshold: 3 }, maxConsecutiveContractFailures: 6, maxConsecutiveTurnsWithoutSignal: 8 }` |
| `verifier` | `{ agentName: Verifier, everyNTurns: 5, triggerOnSuspiciousTransition: true, findingsKeyword: INCONSISTENCY }` |
| `brownfield` | `{ entryPoints, discoveryBriefPath, conventionProfilePath, seedEnvelopeFromBrief }` |
| `testSelector` | `{ findRelatedCommand: "... {file} ...", fullSuiteCommand: "..." }` |
| `systemPrompt` / `systemPromptPath` | Replace the base prompt. |
| `warnTurnTokens`, `memory`, `skillCuration`, `telemetry`, `checkpoint`, `output`, `apiProfiles`, `scratchpad`, `chatroom` | See `docs/configuration.md#advanced`. |

Change tracking, the evidence graph, events and checkpoints are always on; there is nothing to configure.

## All plugin names

| Plugin | What it provides |
|--------|-----------------|
| `FileSystem` | read_file, write_file, patch_file, list_files, get_file_info, delete_file, … |
| `Shell` | shell_run, shell_run_script, shell_run_background, shell_get_job_* |
| `Git` | git_status, git_diff, git_log, git_add, git_commit, git_push, git_pull, … |
| `Search` | search_content, search_symbol, search_callers |
| `Http` | http_get, http_post, http_put, http_patch, http_delete |
| `Json` | json_format, json_get, json_keys, json_merge, json_validate |
| `Scratchpad` | scratchpad_write, scratchpad_read, scratchpad_read_all, scratchpad_search |
| `Chatroom` | chatroom_send, chatroom_read |
| `Changes` | changes_read, changes_read_latest |
| `Subagent` | subagent_explore, subagent_explore_many, subagent_locate |
| `Probe` | probe_code, probe_assert_output, probe_compare_outputs, probe_run_hypothesis |
| `CodeExecution` | code_execution_sandbox_run, code_execution_repl_start/exec/stop |
| `Compaction` | compact_conversation |
| `Document` | document_extract_text, document_get_info, document_list_sheets |
| `Investigation` | investigation_create_hypothesis, investigation_reject_hypothesis, … |
| `Decision` | decision_search, decision_read (read); decision_create, decision_supersede (write) |
| `Graph` | graph_search, graph_refs, graph_dependents — read-only; needs `fuseraft graph build` |
| `Objective` | objective_create, objective_read, objective_update, objective_list, objective_link_task |

## Common providers

| Provider | `modelId` example | `endpoint` | `apiKeyEnvVar` | Notes |
|----------|----------------|----------|-------------|-------|
| xAI | `grok-4.3` | `https://api.x.ai/v1` | `XAI_API_KEY` | Set `reasoningEffort` (none/low/medium/high/…) |
| Anthropic | `claude-sonnet-4-6` | `https://api.anthropic.com/v1` | `ANTHROPIC_API_KEY` | |
| OpenAI | `gpt-4o` | `https://api.openai.com/v1` | `OPENAI_API_KEY` | |
| Ollama (local) | `llama3.1` | `http://localhost:11434/v1` | *(none needed)* | |

## Brief (written by the Planner)

```json
{
  "goal": "One sentence describing the task.",
  "files_to_change": ["src/api/users.py", "tests/test_users.py"],
  "acceptance_criteria": [
    "GET /users?page=2&limit=10 returns the correct slice",
    "Invalid page values return 400 with a descriptive error"
  ],
  "constraints": ["Do not change the existing response shape"],
  "verify_command": "pytest tests/test_users.py"
}
```

## Test report (written by the Tester)

```json
{
  "results": [
    { "criterion": "<criterion text from the brief>", "status": "PASS", "command": "<exact shell_run command>", "exit_code": 0 }
  ]
}
```

`command` must name a command that actually ran this run for every PASS.

## Review judgement block (for `reviewJudgement`)

```json
{
  "review": [
    { "criterion": "<criterion text>", "verdict": "PASS", "evidence": "ran pytest, exit 0" },
    { "criterion": "<another>",        "verdict": "FAIL", "evidence": "has_next is missing" }
  ]
}
```

One entry per acceptance criterion; a successful shell run is needed in the same turn as any PASS.
