---
title: "Configuration Reference"
description: "A team file defines agents, the files they hand each other, and the flow between them. Every fact is written once, and every handoff is gated the same way."
---

A team file defines your agents, the files they hand each other (**artifacts**), and the **flow** between them. It is YAML or JSON, and you pass its path to `fuseraft run`:

```bash
fuseraft run --config .fuseraft/config/team.yaml "Add pagination to the users API"
fuseraft validate .fuseraft/config/team.yaml
```

The smallest useful team:

```yaml
team: Solo
agents:
  Assistant:
    tools: [FileSystem, Shell]
    instructions: You are a careful assistant. Complete the task, then finish.
flow:
  start: Assistant
  steps:
    Assistant:
      - to: end
```

An agent with no `model` uses your default model from `~/.fuseraft/config`. See `config/examples/orchestration.yaml` for a complete team with artifacts and gated handoffs.

**Unknown fields are errors.** A misspelled field, check, agent or artifact name stops the run before the first turn, with a message naming the field. Nothing is silently ignored.

**Keys are camelCase** (`maxTurns`, `onStuck`), as in every example here. Matching ignores case, so a file written in PascalCase still loads, but writing the same field twice in different cases is an error. Names you choose (agents, artifacts, model aliases, environment variables) are used exactly as written, and an artifact can't share a gate's name: call a test report artifact `testResults`, not `testReport`.

> **Upgrading an older config?** Files that start with `Orchestration:` use the previous format. Convert one with `fuseraft migrate <file>` (prints the result) or `fuseraft migrate <file> --write` (overwrites it). The converter keeps the leading comment block and notes anything it had to approximate.

---

## Top-level fields

| Field | Required | Description |
|-------|----------|-------------|
| `team` | yes | Team name, shown when a run starts. |
| `description` | no | One-line summary. |
| `models` | no | Named model aliases. See [Models](models.md). |
| `artifacts` | no | Files agents exchange, each declared once. See [Artifacts](#artifacts). |
| `agents` | yes | Agents keyed by name. See [Agents](#agents). |
| `flow` | yes | Who runs next, and what each handoff requires. See [Flow](#flow). |
| `limits` | no | Hard caps on a run. See [Limits](#limits). |
| `security` | no | Sandbox, permissions and network rules. See [Security](#security). |
| `mcpServers` | no | MCP servers whose tools agents can use. See [MCP](mcp.md). |
| `advanced` | no | Opt-in layers: compaction, verifier, telemetry and more. See [Advanced](#advanced). |

---

## `artifacts`

An artifact is a file one agent writes and another reads: a brief, a test report, a review. Declare it once; checks and prompts refer to it by name.

```yaml
artifacts:
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
          exit_code: int
```

| Field | Required | Description |
|-------|----------|-------------|
| `path` | yes | File path, relative to the working directory. `{project_slug}` and `{session_id}` are expanded at run time. |
| `shape` | no | A sketch of the JSON the file must hold. Gates that name the artifact check the file against it. |

**Shape syntax**

| Sketch | Matches |
|--------|---------|
| `string`, `int`, `number`, `bool`, `any` | A value of that type. |
| `A\|B\|C` | One of those strings, e.g. `PASS\|FAIL`. |
| `[T]` | A list whose items all match `T`. |
| a nested map | An object with those fields. Extra fields are allowed. |
| a trailing `?` | An optional field, e.g. `notes: string?`. |

**What fuseraft does with an artifact**

- Agents list artifacts under `reads` and `writes`. fuseraft tells each agent the artifact's path and, for writers, its shape, so you never repeat them in `instructions`.
- A `reads` artifact is included in the agent's context whenever the file exists.
- A gate that names the artifact checks that it exists and matches its shape. See [Gates](#gates).

Two names are conventional: an artifact named `brief` is the brief that brief-reading checks use, and one named `testResults` is the report `fuseraft run --ci` reads.

---

## `agents`

```yaml
agents:
  Developer:
    role: Implements the brief.
    model: fast
    tools: [FileSystem, Shell, Git, Search, Changes]
    reads: [brief]
    instructions: |
      Implement every file in the brief's files_to_change, run its
      verify_command, then commit.
    advanced:
      functionChoice: required
      maxInTurnToolPairs: 12
```

| Field | Description |
|-------|-------------|
| `role` | One-line description of what the agent does. |
| `model` | A [model alias](models.md) from `models`, or a full model map (`{ modelId: gpt-4o, maxTokens: 4096 }`). A map's `modelId` may also be an alias, with the other fields overriding it. Leave it out to use your default model. |
| `tools` | Tool groups: built-in plugins (see [Plugins](plugins.md)) or `mcpServers` names. `Handoff` is added automatically for agents in a `steps` flow. |
| `reads` | Artifacts this agent reads. Each is included in its context when the file exists. |
| `writes` | Artifacts this agent writes. Their paths and shapes go into its prompt. |
| `sees` | `handoff` (default), `conversation` or `fork`. See below. |
| `instructions` | Required. What the agent does and how. |
| `agentFile` | Load this agent's fields from a separate YAML file. See [Agent files](#agent-files). |
| `remoteAgent` | Run this agent on a remote A2A server. See [`remoteAgent`](#remoteagent). |
| `advanced` | Per-agent knobs. See [Advanced agent fields](#advanced-agent-fields). |

### What an agent sees

| `sees` | Context each turn |
|--------|-------------------|
| `handoff` (default) | The task, or the `goal`/`background`/`constraints` the previous agent passed to `handoff`; the agent's `reads` artifacts; its own last few turns; and any correction addressed to it. It never sees other agents' transcripts. |
| `conversation` | The whole shared conversation. Use it for agents that genuinely need the discussion, and for every agent in the `magentic` pattern (the default there). |
| `fork` | The shared conversation, plus the handoff directive on top. |

`handoff` keeps contexts small and stops agents copying each other's mistakes. It works because artifacts carry the durable facts and `handoff` carries the instructions.

### Generated prompt text

fuseraft appends a **Team protocol** section to every agent's instructions, built from the team file:

- each artifact the agent reads or writes, with its path, and the shape it must write;
- for agents in a `steps` flow, the routes it may hand off on, when to take each one, and the checks fuseraft runs before letting it through.

Don't repeat any of that in `instructions`. Write about the work itself.

### Advanced agent fields

| Field | Default | Description |
|-------|---------|-------------|
| `functionChoice` | `auto` | `auto`, `required` (must call a tool every turn; use for agents that act), or `none` (only for pattern agents: an agent with `steps` routes has to call `handoff`). |
| `capabilities` | `{}` | Per-plugin tool filter. See [Capabilities](#capabilities). |
| `contextWindow` | — | Filters shared history for `conversation`/`fork` agents. See [`contextWindow`](#contextwindow). |
| `context` | — | Extra context sources: `session_context`, `changes_recent:N`, `brief_field:FIELD`, `file:PATH`, `own_history:N`, `execution_state`, `investigation_log`. |
| `maxToolCallsPerTurn` | `0` | Hard cap on tool calls per turn; `0` means no limit. |
| `maxInTurnContextTokens` | `0` | Soft cap on in-turn context tokens; the oldest tool results are replaced with placeholders to stay under it. |
| `maxInTurnToolPairs` | `0` | Keep only the last N tool call/result pairs in full once a turn nears the context budget. 8–16 suits agents that act a lot. |
| `trustScore` | `0.7` | Governance trust score (0.0–1.0). See [Governance](governance.md#execution-rings). |
| `subagentModel` | parent's model | Model for the `Subagent` plugin's explorer, e.g. a cheaper one. |
| `subagentPlugins` | read-only set | Plugins the subagent may use. |
| `subagentMaxToolCalls` | `20` | Round cap for `subagent_explore`. |
| `skipExecutionState` | `false` | Don't prepend the build/test execution state to this agent's context. |
| `knowledgeWeight` | — | Weighting for knowledge-layer retrieval. |

### Capabilities

Per-plugin tool filter. Keys are plugin names; values are capability tags. Only tools whose tag is listed are registered. Leaving a plugin out grants all its tools.

```yaml
Reviewer:
  tools: [FileSystem, Git]
  advanced:
    capabilities:
      FileSystem: [read]
      Git:        [read]
```

| Plugin | Capability tags |
|--------|----------------|
| `FileSystem` | `read` (read_file, grep_file, get_file_summary, get_file_info, list_files) · `write` (write_file, patch_file, save_file_summary, create_directory, copy_file, move_file, set_permissions) · `delete` (delete_file, delete_directory) |
| `Shell` | `read` (shell_get_env, shell_get_job_status, shell_get_job_output, shell_which, shell_get_working_directory, shell_get_session_temp_dir) · `run` (shell_run, shell_run_script, shell_run_background, shell_set_env, shell_kill_job) |
| `Git` | `read` (git_status, git_diff, git_log, git_show, git_branch_list, git_stash_list, git_is_inside_work_tree, git_is_repo_root) · `write` (git_add, git_commit, git_checkout, git_create_branch, git_init, git_push, git_pull, git_stash, git_stash_pop, git_reset, git_rebase) |
| `Http` | `get` (http_get, http_head) · `post` · `put` · `patch` · `delete` |
| `Json` | `read` · `write` (json_merge) |
| `Document` | `read` (document_extract_text, document_get_info, document_list_sheets, document_get_sheet) |
| `Search` | `read` |
| `Changes` | `read` |
| `Scratchpad` | `read` · `write` |
| `Chatroom` | `read` · `write` |
| `Probe` | `run` |
| `CodeExecution` | `read` (code_execution_check_docker) · `execute` (sandbox_run, repl_*) |
| `Decision` | `read` (decision_search, decision_read) · `write` (decision_create, decision_supersede) |
| `Graph` | `read` (graph_search, graph_refs, graph_dependents) |

Tools not in this map, such as MCP tools, always pass through.

### Agent files

`agentFile` loads an agent's fields from a separate YAML file, so one agent definition can be shared across teams. The file holds the same fields as an inline agent (an `agent:` wrapper key is also accepted). Inline fields win over the file's.

```yaml
# agents/reviewer.yaml
role: Reviews changes for correctness and test coverage.
tools: [FileSystem, Git, Changes]
instructions: |
  You are a senior engineer reviewing code changes...
advanced:
  functionChoice: required
```

```yaml
agents:
  Reviewer:
    agentFile: agents/reviewer.yaml
    model: reasoning        # inline fields win over the file
```

Relative paths resolve from the team file's directory. A missing file is a load error.

### Subagent

With `Subagent` in `tools`, an agent gets `subagent_explore` (a multi-hop read-only exploration loop), `subagent_explore_many` (up to four explorations at once) and `subagent_locate` (a single-target lookup capped at 5 rounds). The subagent never sees the parent's conversation.

```yaml
Developer:
  tools: [FileSystem, Shell, Subagent]
  advanced:
    subagentModel: claude-haiku-4-5-20251001
    subagentMaxToolCalls: 25
    subagentPlugins: [FileSystem, Search, Git]
```

### `remoteAgent`

Runs an agent on a remote server that speaks the [A2A protocol](https://a2a-protocol.org/). The agent card is fetched from `{Url}/.well-known/agent-card.json` when the run starts.

```yaml
RemoteReviewer:
  instructions: You are a code reviewer. Be thorough.
  remoteAgent:
    url: https://reviewer.internal
    timeoutSeconds: 60
    headers:
      Authorization: Bearer ${REVIEWER_TOKEN}
```

| Field | Default | Description |
|-------|---------|-------------|
| `url` | — | Base URL of the remote agent. |
| `timeoutSeconds` | `120` | Timeout for card resolution and each turn. |
| `headers` | — | Headers sent with every request; values support `${ENV_VAR}`. Sent only to `url`'s origin. |

A remote agent runs on its own server, so `model`, `tools`, `sees` and `advanced` don't apply and are rejected. It is sent only its own `instructions`; fuseraft's base prompt and local environment details never leave this machine. A remote agent hands off by writing a route name on its own line.

The remote side can be another fuseraft team: `fuseraft serve --a2a` hosts each agent at `{host}:{port}/{AgentName}`. See [Serve — Hosting agents over A2A](serve.md#hosting-agents-over-a2a).

### `contextWindow`

Filters the shared history passed to an agent that sees the conversation (`sees: conversation` or `fork`).

```yaml
Reviewer:
  sees: conversation
  advanced:
    contextWindow:
      textOnly: true
```

| Field | Default | Description |
|-------|---------|-------------|
| `textOnly` | `false` | Strip tool calls and results; keep only text. Often cuts a late-stage agent's input by 90%. |
| `excludeAgents` | `[]` | Remove everything these agents wrote. |
| `maxTurnAge` | `0` | Keep only the last N agent turns. |
| `maxTailMessages` | `0` | After the other filters, keep only the last N messages. |
| `maxToolResultChars` | `0` | Truncate replayed tool results to this many characters. |
| `toolResultCharOverrides` | `{}` | Per-tool overrides for `maxToolResultChars`. |
| `maxReplayChars` | `0` | Truncate replayed assistant messages; `0` uses a 2,000-character fallback. |

Filters apply in order: `textOnly`/`excludeAgents`, then `maxTurnAge`, then `maxTailMessages`. Shared history itself is never changed.

---

## `flow`

`flow` takes one of two forms: **Steps**, where you draw the flow and gate each handoff, or a **Pattern** with built-in control logic.

### Steps

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
        when: the work is approved
      - to: Developer
        when: the code needs fixing
```

`start` names the agent that runs first. Each key under `steps` is an agent, followed by the edges it may take when it finishes. An edge to `end` finishes the run.

| Edge field | Description |
|------------|-------------|
| `to` | An agent, `end`, or a list of agents to run in parallel. |
| `when` | When to take this edge, in words. The agent sees it as the route's description. |
| `requires` | Gates that must pass before the handoff goes through. See [Gates](#gates). |
| `onStuck` | An agent to call in after the gates on this edge fail twice in a row, e.g. a Developer to fix what a Tester can't. |
| `name` | The route name the agent hands off with. Defaults to the target agent (or `end`); only needed when one step has two edges to the same agent. |
| `join` | For a parallel `to`, the agent that runs after every branch finishes. Required with a list. |
| `merge` | For a parallel `to`, how branch outputs are combined before `join`. |
| `maxRevisits` | On a loop back to an earlier step, escalate after this many repeats. The escalation quotes the first artifact this step's agent `writes` (e.g. a review), so the earlier agent sees the outstanding objections. |
| `approve` | `true` to have a person approve the handoff after its gates pass. A refusal sends the work back to the agent. Asked in the terminal, over `run --bridge`, or by `serve`; with no one to ask (piped stdin), the handoff goes through. |

**How handoffs work.** Every agent in a `steps` flow gets the `handoff` tool. Its `route_keyword` argument only accepts the routes of the agent's current step, so a model can't name a route that doesn't exist. Before the handoff goes through, fuseraft runs the edge's gates; if one fails, the agent gets a message naming what's missing and runs again.

**Every flow must reach `end`.** A flow with no edge to `end` is rejected, because the run could only stop at `limits.maxTurns`. Every agent an edge points to must have its own entry under `steps`, except parallel branches.

**Parallel branches**

```yaml
Lead:
  - to: [Backend, Frontend]
    join: Integrator
    merge: { strategy: union }
```

`Backend` and `Frontend` run at the same time, each with its own snapshot of the history, and then `Integrator` runs.

### Patterns

| Pattern | Fields | Behaviour |
|---------|--------|-----------|
| `magentic` | `model`, `instructions`, `maxRoundCount` (20), `maxStallCount` (3), `maxResetCount` (2), `enablePlanReview` | A manager model plans, picks who works each round, and replans when progress stalls. |
| `mapreduce` | `splitter`, `mapper`, `reducer`, `itemsJsonPath`, `maxConcurrency`, `maxSplitterRetries` | A splitter produces a JSON list of work items, a mapper runs on each in parallel, a reducer combines the results. |
| `scattergather` | `participants`, `synthesizer`, `maxConcurrency` | Every participant gets the same task in parallel; a synthesizer combines their answers. |
| `adversarial` | `stages` (`generator`, `critic`, `label`), `rounds` (3), `passKeyword` | Each stage pairs a generator with a critic that sees only the artifact under review. |

```yaml
flow:
  pattern: magentic
  model: reasoning
  maxRoundCount: 20
```

Patterns end on their own; `limits` still applies. In `magentic`, every agent sees the conversation, because the manager's loop depends on it. See [Strategies](strategies.md) for each pattern in depth.

---

## Gates

A gate is a check that must pass before a handoff goes through. Gates read files on disk and the run's recorded actions; they never call a model. Every edge uses the same `requires:` list, and an item takes one of three forms:

```yaml
requires:
  - brief # an artifact: exists and matches its Shape
  - relatedTestsPass # a check with no argument
  - commandSucceeded: brief.verify_command   # a check with an argument
  - testReport: testResults                   # a check with an argument and options
    noFailures: true
```

| Check | Argument | Passes when |
|-------|----------|-------------|
| *artifact name* | — | The artifact's file exists and, if it declares a `shape`, matches it. |
| `fileExists` | a path | The file exists. |
| `filesWritten` | `artifact.field` | Every path listed in that field of the artifact has been written this run. |
| `checklistComplete` | `artifact.field` | Every file-writing step listed in that field has been written this run. |
| `commandSucceeded` | `artifact.field`, or a pattern | A shell command matching the command in that field (or any `\|`-separated part of the pattern) ran and exited 0 this run. Option: `fallback`, a pattern used when the field is empty. |
| `testReport` | a test report artifact | The report exists and has results. Options: `noFailures` (default `true`): no result is `FAIL`. `hasAssertions` (default `true`): every `PASS` names a command that actually ran this run. |
| `relatedTestsPass` | — | The tests related to the files changed this run pass. Needs `advanced.testSelector`. |
| `acceptanceCriteriaPassed` | the brief artifact | Every acceptance criterion with `expected_output_contains` was met by a successful command's output this run. |
| `reviewJudgement` | optionally, the brief artifact | The agent's last message holds a ```` ```json {"review": [...]} ```` block with a PASS/FAIL verdict and evidence per criterion, no FAIL, and a successful shell command this turn if any verdict is PASS. |
| `anyFileWritten` | — | The agent wrote at least one file this turn. Option: `fallback`, a command pattern that also counts. |
| `noRepeatedShellFailures` | a pattern | The agent's recent runs of matching commands aren't failing over and over. |
| `sessionContextWritten` | — | The agent wrote a summary with `session_context_write` this turn. |
| `architecture` | — | The changes follow the project's declared architecture rules. |

All gates on an edge must pass. Unknown check names, unknown options and references to undeclared artifacts are load errors.

**When a gate fails**, the agent gets a correction message naming what's missing, and runs again. Failures are classified (missing evidence, conflicting evidence, no progress) and escalate after a threshold; tune that with `advanced.failureHandling` ([Failure handling](#failure-handling)). With `onStuck`, the named agent is called in after two consecutive failures on that edge.

The brief schema and the test report schema the checks expect are in [Validators](validators.md).

---

## `limits`

```yaml
limits:
  maxTurns: 40
  maxTokens: 200000
```

| Field | Default | Description |
|-------|---------|-------------|
| `maxTurns` | `40` | The run stops after this many agent turns. |
| `maxTokens` | — | The run stops before the next turn once total tokens (input + output) pass this. The session is saved and can be resumed. |

---

## `security`

```yaml
security:
  fileSystemSandboxPath: /home/user/projects/myapp
  fileSystemPermissions:
    read:  [src/**, docs/**]
    write: [tests/**, docs/**]
    deny:  [secrets/**, infra/prod/**]
  shellPolicy:
    allow: ["go test", "npm test"]
    deny:  ["rm -rf", "curl | bash"]
  httpAllowedHosts:
    - api.github.com
    - registry.npmjs.org
```

| Field | Default | Description |
|-------|---------|-------------|
| `fileSystemSandboxPath` | — | Restricts the FileSystem, Shell and Git plugins to this directory tree. |
| `fileSystemPermissions` | — | Read/write/deny globs within the sandbox. Needs `fileSystemSandboxPath`. See [Security → Filesystem permissions](security.md#filesystem-permissions-read--write--deny-globs). |
| `shellPolicy.Allow` | `[]` | When set, commands must contain one of these patterns (or, with `AllowMode: segments`, every command in the string must start with one). |
| `shellPolicy.AllowMode` | `substring` | `substring` or `segments`. See [Security → Per-segment allow lists](security.md#per-segment-allow-lists). |
| `shellPolicy.deny` | `[]` | Commands containing any of these are blocked. Checked first. |
| `changeEnvelope` | — | Globs that writes must match. Seeded from the brownfield discovery brief when `advanced.brownfield.seedEnvelopeFromBrief` is on. See [Security → Change envelope](security.md#change-envelope). |
| `httpAllowedHosts` | `[]` | Host allowlist for the Http plugin; supports `${ENV_VAR}`. Private IPs are always blocked. |
| `denyCredentialFiles` | `true` | Deny SSH keys, `.aws/credentials`, `.netrc`, `.pgpass` and `.git-credentials`. `.env` is always denied. See [Security → Credential files](security.md#credential-files). |
| `allowPrivateHosts` | `false` | Allow private/loopback hosts. Local development only. |
| `readFileSizeLimit` | `20000` | Max characters one `read_file` call returns. |

See [Security](security.md) for full detail.

---

## `advanced`

Everything most teams never touch lives under `advanced`. Each key binds to the section documented below.

| Key | Purpose |
|-----|---------|
| `systemPrompt`, `systemPromptPath` | Replace the base prompt. See [Base system prompt](#base-system-prompt). |
| `compaction` | Summarize long histories. See [Conversation compaction](#conversation-compaction). |
| `contextBudget` | Per-agent input-token budget that triggers compaction. See [Context budget](#context-budget). |
| `warnTurnTokens` | Warn when one turn's input passes this many tokens. |
| `failureHandling` | How gate failures escalate. See [Failure handling](#failure-handling). |
| `verifier` | A meta-agent that audits claims against the change log. See [Verifier](#verifier). |
| `brownfield` | Recon-phase support for existing codebases. See [Brownfield mode](#brownfield-mode). |
| `testSelector` | Commands for finding and running related tests. See [Test selector](#test-selector). |
| `memory` | A live memory provider around each turn. See [Pluggable memory provider](#pluggable-memory-provider). |
| `skillCuration` | Author a reusable skill from each finished run. See [Skill curation](#skill-curation). |
| `telemetry` | OpenTelemetry export. See [Telemetry](#telemetry). |
| `checkpoint` | Where checkpoints are stored. See [Checkpoint](#checkpoint). |
| `output` | Reporting defaults for scripted runs. See [Output](#output). |
| `apiProfiles` | Named endpoints for the Http plugin. See [`apiProfiles`](#apiprofiles). |
| `scratchpad`, `chatroom` | Storage paths for those plugins. |

### Base system prompt

Every agent's instructions are prefixed with a shared base prompt: by default the embedded `FUSERAFT.md`, which sets tool-use discipline, format rules and safety constraints. Replace it per team:

```yaml
advanced:
  systemPromptPath: ./prompts/my-harness.md   # relative to the team file
```

```yaml
advanced:
  systemPrompt: |
    You are part of a data-engineering team.
    Always produce deterministic, idempotent SQL.
```

**Precedence:** `systemPromptPath`, then `systemPrompt`, then the embedded `FUSERAFT.md`. `fuseraft validate` reports a missing `systemPromptPath` file.

Each agent's final prompt is assembled in this order: base prompt; the agent's `instructions` with the generated team protocol; a manifest of the `.fuseraft/` runtime folder; the context store summary (when `.fuseraft/context/index.json` has entries, see [Context store](context-store.md)); the brownfield convention profile; and the test selector hint.

### Conversation compaction

```yaml
advanced:
  compaction:
    triggerTurnCount: 30
    keepRecentTurns: 8
    mode: lossless
```

| Field | Default | Description |
|-------|---------|-------------|
| `triggerTurnCount` | `50` | Compact when the assistant-turn count reaches this. Ignored in `window` mode. Must exceed `keepRecentTurns`. |
| `keepRecentTurns` | `10` | Turns kept verbatim after compaction. Ignored in `window` mode. |
| `model` | first agent's model | Model for the summary (`llm` and `hybrid` modes). |
| `mode` | `llm` | See below. |
| `tokenBudget` | `80000` | Token budget for `window` mode. |
| `includeReasoning` | `true` | Prepend `[REASONING EXCERPTS]` from the run's reasoning events. |
| `includeSymbolGraph` | `true` | Prepend the symbol dependency graph for files written this run. |
| `maxCharsPerHistoryMessage` | `8000` | Truncate any single message passed to the summarizer. `0` disables. |
| `antiThrashMinSavingsRatio` | `0.10` | Skip compaction while recent ones saved less than this fraction. `0` disables. |
| `antiThrashWindow` | `10` | How many recent compactions the anti-thrash guard looks at. |
| `summaryTemplate` | built-in | Custom summary prompt; see below. |

| Mode | Behaviour |
|------|-----------|
| `llm` | An LLM call summarizes the compacted turns. |
| `lossless` | Rebuilds context from the evidence graph, gate results and the current step; no LLM call. Falls back to `llm` for pattern flows. |
| `hybrid` | `lossless` reconstruction followed by an LLM summary. |
| `window` | Drops the oldest message pairs until the estimate fits `tokenBudget`; no summary. |
| `intent` | Rebuilds a deterministic per-operation log from the intent log; no LLM call. |

The default summary prompt produces four sections agents can rely on after a compaction boundary: `## Completed` (files written, commands with exit codes, commits), `## Open Questions`, `## Remaining Work` and `## Key Findings`. To override it:

```yaml
advanced:
  compaction:
    summaryTemplate: |
      Summarize the following {{$turn_count}} turns for task: {{$task}}
      Change log:
      {{$change_log}}
      History:
      {{$history}}
```

Tokens: `{{$task}}`, `{{$turn_count}}`, `{{$change_log}}` (the run's `changes.json`), `{{$history}}`. See [Sessions](sessions.md).

### Context budget

Counts input tokens per agent and compacts, rather than stopping, when a budget is reached.

```yaml
advanced:
  contextBudget:
    warnAt: 60000
    cutoverAt: 100000
    maxSingleTurnInputTokens: 200000
```

| Field | Default | Description |
|-------|---------|-------------|
| `warnAt` | `0` | Warn once per agent per cycle at this many cumulative input tokens. |
| `cutoverAt` | `0` | Compact before the next turn at this many. Needs `compaction`. Must exceed `warnAt`. |
| `maxSingleTurnInputTokens` | `0` | Compact before the next turn when one turn's input passed this. Needs `compaction`. |
| `maxToolResultTokens` | `0` | Cap on tool-result tokens per invocation; older results become one-line tombstones. |
| `inTurnToolWindow` | `20` | Most-recent tool results always kept when `maxToolResultTokens` applies. |

Counters reset after each compaction, and the first turn after one is exempt, so the compacted history can't trigger an immediate second compaction. Set `warnTurnTokens` below `cutoverAt`. Events: `context_budget_warn`, `context_budget_cutover`.

`limits.maxTokens` stops the run; `contextBudget` keeps it going by compacting.

### Failure handling

Classifies gate failures and decides how each kind escalates. The values shown are the defaults.

```yaml
advanced:
  failureHandling:
    missingEvidence:     { action: Reinstruct, threshold: 3 }
    invalidTransition:   { action: Reinstruct, threshold: 3 }
    conflictingEvidence: { action: Reinstruct, threshold: 2 }
    noProgress:          { action: Abort,      threshold: 3 }
    maxConsecutiveContractFailures: 6     # default 0 (off)
    maxConsecutiveTurnsWithoutSignal: 8   # default 0 (off)
```

| Type | Detected when |
|------|--------------|
| `missingEvidence` | The failure names something not found, missing or absent. |
| `invalidTransition` | The agent handed off without completing prerequisites. |
| `conflictingEvidence` | The failure points to fabricated or inconsistent evidence. |
| `noProgress` | The agent handed off again without calling any tool since the last correction. |

| Action | Behaviour |
|--------|-----------|
| `Reinstruct` | Inject a targeted correction and run the agent again. |
| `ActivateRecovery` | Call in the edge's `onStuck` agent immediately. |
| `EscalateToHuman` | Stop and escalate immediately. |
| `Abort` | Keep correcting until `threshold` consecutive failures, then escalate. |

`maxConsecutiveContractFailures` caps consecutive gate failures on one edge across all types, so a `Reinstruct` policy can't loop forever on a gate that can't pass. `maxConsecutiveTurnsWithoutSignal` escalates when an agent runs that many turns without handing off.

### `verifier`

A meta-agent that audits what agents claim against what the change log recorded. It is a regular agent in `agents`, usually with `sees: fork` and the `Changes` tool, and isn't part of the flow.

```yaml
advanced:
  verifier:
    agentName: Verifier
    everyNTurns: 5
    triggerOnSuspiciousTransition: true
    findingsKeyword: INCONSISTENCY
```

| Field | Default | Description |
|-------|---------|-------------|
| `agentName` | — | Required. An agent declared under `agents`. |
| `everyNTurns` | `0` | Run the verifier every N turns. `0` disables periodic runs. |
| `triggerOnSuspiciousTransition` | `true` | Run it after a conflicting-evidence or no-progress gate failure. |
| `findingsKeyword` | `INCONSISTENCY` | When the verifier's reply contains this, a correction goes to the next agent. |

See `config/examples/orchestration.yaml` for a Verifier agent's instructions.

### Brownfield mode

Recon-phase support for large existing codebases: the change envelope is seeded from the Archaeologist's discovery brief, and the detected convention profile is injected into every agent's prompt.

```yaml
advanced:
  brownfield:
    entryPoints: [src/cmd/server/main.go]
    discoveryBriefPath: ~/.fuseraft/sessions/{project_slug}/{session_id}/brief.brownfield.json
    conventionProfilePath: ~/.fuseraft/sessions/{project_slug}/{session_id}/conventions.json
    seedEnvelopeFromBrief: true
```

| Field | Default | Description |
|-------|---------|-------------|
| `entryPoints` | `[]` | Files or directories that seed the Archaeologist's dependency walk. |
| `discoveryBriefPath` | session `brief.brownfield.json` | Where the Archaeologist writes the discovery brief. Its `in_scope_files` are merged into `security.changeEnvelope` at startup. |
| `conventionProfilePath` | session `conventions.json` | Where the Archaeologist writes the convention profile, which is prepended to every agent's prompt. |
| `seedEnvelopeFromBrief` | `true` | Merge `in_scope_files` into the change envelope. Needs `security.fileSystemSandboxPath`. |

The discovery brief holds `entry_points`, `in_scope_files`, `fragility_signals`, `test_coverage_gaps` and `summary`. The convention profile holds `language`, `naming_patterns`, `error_handling`, `forbidden_patterns`, `test_patterns`, `structural_notes`, `build_command` and `test_command`. A complete runnable example is `config/examples/brownfield.yaml`; see [Examples → Brownfield pipeline](examples.md#brownfield-pipeline).

### Test selector

The commands agents and the `relatedTestsPass` gate use to find and run the tests for a changed file.

```yaml
advanced:
  testSelector:
    findRelatedCommand: "go test -list . $(go list -f '{{.Dir}}' {file}) 2>/dev/null | head -40"
    fullSuiteCommand: "go test ./..."
```

| Field | Description |
|-------|-------------|
| `findRelatedCommand` | Finds tests covering `{file}`. |
| `fullSuiteCommand` | Runs when `findRelatedCommand` finds nothing. |

| Language | `findRelatedCommand` |
|----------|---------------------|
| Go | `go test -list . $(go list -f '{{.Dir}}' {file}) 2>/dev/null \| head -40` |
| Python (pytest) | `pytest --collect-only -q {file} 2>/dev/null \| grep '::' \| head -40` |
| TypeScript (jest) | `jest --listTests --testPathPattern=$(dirname {file}) 2>/dev/null` |
| Rust | `cargo test --list 2>/dev/null \| grep $(basename {file} .rs) \| head -20` |

### Pluggable memory provider

Runs load and save hooks around every agent turn.

```yaml
advanced:
  memory:
    provider: webhook
    webhook:
      loadUrl: https://memory.example.com/load
      saveUrl: https://memory.example.com/save
      headers:
        Authorization: "Bearer ${MEMORY_TOKEN}"
      timeoutSeconds: 10
      saveEveryNTurns: 10
```

`local` (the default) reads each agent's file-backed memory at `~/.fuseraft/memory/agents/{Name}/`. `webhook` POSTs `{"agent": "<name>"}` to `LoadUrl` and expects `{"block": "<text>"}`; it POSTs `{"agent", "history"}` to `SaveUrl` every `SaveEveryNTurns` turns, fire-and-forget.

Agent memories are stored as Markdown files with YAML frontmatter, ranked by relevance and injected before each turn without any config. See [Context Management — Layer 2](context-management.md#layer-2-persistent-memory-pipeline-injected).

### Skill curation

After a run finishes, one LLM call decides whether it produced portable knowledge and, if so, writes a `SKILL.md` to the library. At the start of later runs, the most relevant skills are injected by full-text search on the task.

```yaml
advanced:
  skillCuration:
    enabled: true
    libraryPath: ~/.fuseraft/skills
    indexTopN: 5
```

| Field | Default | Description |
|-------|---------|-------------|
| `enabled` | `false` | Turn curation on. |
| `libraryPath` | `~/.fuseraft/skills` | Where skills are written, one `{slug}/SKILL.md` each. |
| `model` | first agent's model | Model for the curation call; a cheaper one works well. |
| `minTurns` | `5` | Skip runs shorter than this. |
| `digestTurns` | `30` | Recent turns included in the curation prompt. |
| `indexPath` | `~/.fuseraft/skills/index.db` | SQLite FTS5 index, updated after each new skill. |
| `indexTopN` | `5` | Skills injected at the start of a run. `0` disables injection. |
| `logPath` | `~/.fuseraft/skill-curation.jsonl` | Append-only log of every attempt and its outcome (`created`, `updated`, `skipped`, `no_skill`, `failed`). |

For the REPL, use `fuseraft settings set skillCuration.enabled true`. See [Skills](skills.md).

### `telemetry`

Exports traces and metrics to any OpenTelemetry backend.

```yaml
advanced:
  telemetry:
    otlpEndpoint: http://localhost:4317
    serviceName: my-team
```

| Field | Default | Description |
|-------|---------|-------------|
| `otlpEndpoint` | `http://localhost:4317` | OTLP gRPC endpoint for traces and metrics. |
| `serviceName` | the `team` name | Service name on traces and metrics. |

A team with no `telemetry` inherits the global default set with `fuseraft settings set telemetry.otlpEndpoint <url>`.

*Traces:* one span per agent turn (`agent.turn/<AgentName>`) tagged with agent, model, turn index, tokens and duration; Agent Framework's own spans are forwarded too.

*Metrics:* `fuseraft.agent.turns`, `fuseraft.tokens.input`, `fuseraft.tokens.output`, `fuseraft.agent.duration_seconds`, `fuseraft.tool.calls`; per run `fuseraft.session.completed` and `fuseraft.session.duration_seconds`; and reliability counters `fuseraft.compaction.count`, `fuseraft.retry.attempts`, `fuseraft.retry.exhausted`, `fuseraft.circuit_breaker.opens`, `fuseraft.hitl.escalations`, `fuseraft.hitl.rejections`, `fuseraft.context_budget.warnings`, `fuseraft.context_budget.cutovers` and `fuseraft.session.max_turns_exceeded`.

Quick start: `docker run --rm -p 4317:4317 -p 16686:16686 jaegertracing/all-in-one`, then open `http://localhost:16686`.

### `checkpoint`

Checkpoints are saved as JSON under `~/.fuseraft/sessions/` by default, which is what `--resume` reads.

```yaml
advanced:
  checkpoint:
    mode: json
    path: .fuseraft/checkpoints
```

| Field | Default | Description |
|-------|---------|-------------|
| `mode` | `json` | `json` writes checkpoint files; `memory` keeps them in memory only, so `--resume` isn't available (useful in CI). |
| `path` | `~/.fuseraft/sessions/` | Directory for checkpoint files. A project-local path keeps them out of `fuseraft sessions`, which reads the global store. |

### `output`

```yaml
advanced:
  output:
    json: true
```

`json: true` has the same effect as passing `--json` on every run: no banner or panels, status on stderr, and one JSON summary object on stdout. The `--json` flag always wins. See [Scripting & Automation](scripting.md).

### `apiProfiles`

Named endpoints the `Http` plugin's `profile` parameter refers to, so agents can call authenticated APIs without credentials in their instructions.

```yaml
advanced:
  apiProfiles:
    github:
      baseUrl: "https://api.github.com"
      timeoutSeconds: 30
      defaultHeaders:
        Authorization: "token ${GITHUB_TOKEN}"
        Accept: "application/vnd.github+json"
```

| Field | Default | Description |
|-------|---------|-------------|
| `baseUrl` | `""` | Prepended to relative paths. |
| `defaultHeaders` | `{}` | Merged into every request; per-call headers win. Values support `${ENV_VAR}`. |
| `timeoutSeconds` | `30` | Request timeout. |

`${ENV_VAR}` tokens in `httpAllowedHosts` and profile values are expanded at startup and never written back to disk or logs; unset variables become empty strings. `httpAllowedHosts` is still enforced after a profile resolves. An agent calls `http_get(url="/repos/o/r", profile="github")`. See [Plugins → Http](plugins.md#http).

---

## What every run records

These are always on and have fixed locations; there is nothing to configure.

| Record | Location | Used by |
|--------|----------|---------|
| Change log | `~/.fuseraft/state/{project_slug}/changes.json` | Gates, the `Changes` plugin, compaction summaries. Every file write, shell command and git commit, per turn. |
| Intent log | `~/.fuseraft/sessions/{project_slug}/{session_id}/intents.json` | Resume and `intent` compaction. Each tracked operation before it runs, then `APPLIED` or `FAILED`. |
| Evidence graph | `~/.fuseraft/state/{project_slug}/evidence.json` | Gates and `lossless` compaction. Typed nodes (`FileWrite`, `FileDelete`, `CommandRun`, `GitCommit`, `SymbolDefinition`, `SymbolReference`) linked by `produced_by`, `verified_by` and `depends_on` edges. |
| Event stream | `~/.fuseraft/sessions/{project_slug}/{session_id}/events.jsonl` | Diagnostics, telemetry, `fuseraft sessions`. One JSON object per line. |
| App log | `~/.fuseraft/logs/{project_slug}/app.log` | Warnings and errors, with secrets masked. |

Each event line looks like:

```json
{ "ts": "2026-10-01T12:00:00Z", "session": "a3f92c1d", "agent": "Tester", "turn": 5, "event_type": "turn_end", "payload": { "input_tokens": 1200, "output_tokens": 340 } }
```

Event types include: `session_start`, `session_end`, `session_summary`, `compaction`, `session_error`; `turn_start`, `turn_end`, `reasoning`, `context_assembly`; `selection_evaluated`, `back_edge_escalation`, `correction_injected`; `validation_fail`, `hitl_escalation`; `context_budget_warn`, `context_budget_cutover`; `magentic_plan`, `magentic_replan`, `magentic_complete`; `tool_blocked`, `tool_call`, `circuit_breaker_open`; and `subagent_start`, `subagent_tool_call`, `subagent_end`.

`context_assembly` reports how each agent's context was built: `context_strategy` is `artifact_spec` for `handoff` agents and `shared_history_fallback` for agents that see the conversation, and `empty_sources` lists declared sources that resolved to nothing, such as an artifact nobody wrote.
