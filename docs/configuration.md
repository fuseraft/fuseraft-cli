# Configuration Reference

All orchestration settings live in a single config file (default: `config/orchestration.yaml`). Both **YAML** and **JSON** formats are supported. The file must have a top-level `Orchestration` key.

**JSON**:
```json
{
  "Orchestration": {
    "Name": "MyTeam",
    ...
  }
}
```

**YAML** (`.yaml` or `.yml`):
```yaml
Orchestration:
  Name: MyTeam
  ...
```

Use any file extension and pass the path explicitly:

```bash
fuseraft run --config config/my-team.yaml
fuseraft validate config/my-team.yaml
```

YAML is often more readable for configs with long agent instructions (block scalars avoid JSON escape sequences) or complex routing tables. Both formats bind to the same schema — every field documented below works identically in either format. See `config/examples/orchestration.yaml` for a complete YAML example.

---

## Top-level fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Name` | string | `""` | Human-readable name displayed at startup. |
| `Description` | string | — | Optional description shown at startup. |
| `SystemPromptPath` | string | — | Path to a Markdown file that replaces the embedded FUSERAFT.md base prompt prepended to every agent. Relative paths resolve from the config file's directory. Takes precedence over `SystemPrompt`. |
| `SystemPrompt` | string | — | Inline text that replaces the embedded FUSERAFT.md base prompt. Ignored when `SystemPromptPath` is also set. |
| `Models` | object | `{}` | Named model aliases reusable across agents. See [Models](models.md). |
| `Agents` | array | `[]` | Ordered list of agents. At least one is required. |
| `Selection` | object | sequential | Controls which agent speaks next. See [Strategies](strategies.md). |
| `Termination` | object | 10 iterations | Controls when the run ends. See [Strategies](strategies.md). |
| `Security` | object | unrestricted | Sandbox constraints for plugins. See [Security](security.md). |
| `MaxTotalTokens` | integer | — | Token budget (input + output combined). Run stops before the next turn if exceeded. |
| `McpServers` | array | `[]` | External MCP servers to connect at startup. See [MCP](mcp.md). |
| `Compaction` | object | — | Automatic history summarization. See [Sessions](sessions.md). |
| `ChangeTracking` | object | — | Cross-agent change log. Enables the `Changes` plugin and validator cross-reference checks. See [Change tracking](#change-tracking). |
| `Events` | object | — | Structured JSONL event stream. See [Events](#events). |
| `Validation` | object | — | Routing validator settings. See [Validators](validators.md). |
| `Telemetry` | object | — | OpenTelemetry export settings. See [Telemetry](#telemetry). |
| `Checkpoint` | object | — | Checkpoint storage settings. See [Checkpoint](#checkpoint). |
| `ApiProfiles` | object | `{}` | Named API endpoint profiles (base URL, auth headers, timeout) for the `Http` plugin. See [ApiProfiles](#apiprofiles). |
| `Saga` | object | — | Compensating rollback settings. When `Enabled: true`, wraps execution with `SagaOrchestrator` for automatic compensation on failure. |
| `EvidenceStore` | object | — | Structured evidence graph alongside `changes.json`. Required for evidence contracts and lossless compaction. See [Evidence store](#evidence-store). |
| `Contracts` | array | — | Named evidence contracts reusable across routes and state machine transitions. See [Evidence contracts](#evidence-contracts). |
| `FailureHandling` | object | — | Per-failure-type policies (action + threshold) applied when routing validators or contracts block a handoff. See [Failure handling](#failure-handling). |
| `Verifier` | object | — | Self-verification meta-agent that audits the evidence graph for inconsistencies. See [Verifier](#verifier). |

---

## Base system prompt

Every agent's instructions are prefixed with a shared base prompt before the session starts. By default this is the embedded `FUSERAFT.md` harness document that establishes tool-use discipline, format rules, and safety constraints. You can replace it per-config via `SystemPromptPath` or `SystemPrompt`.

**Load from a file (recommended for multi-config repos):**

```yaml
Orchestration:
  SystemPromptPath: ./prompts/my-harness.md
```

The path is resolved relative to the config file's directory, so it works regardless of where `fuseraft run` is invoked. Absolute paths are also accepted.

**Inline override:**

```yaml
Orchestration:
  SystemPrompt: |
    You are part of a specialized data-engineering team.
    Always produce deterministic, idempotent SQL.
    Never truncate tables without an explicit instruction.
```

**Precedence:** `SystemPromptPath` → `SystemPrompt` → embedded `FUSERAFT.md`.

`fuseraft validate` reports an error if `SystemPromptPath` is set but the file does not exist.

---

## Agent configuration

Each entry in `Agents` configures one participant in the group chat.

```yaml
- Name: Developer
  Description: Senior engineer who implements features.
  Instructions: You are a software engineer...
  Model:
    ModelId: gpt-4o
  Plugins:
    - FileSystem
    - Shell
    - Git
  FunctionChoice: required
```

| Field | Type | Default | Required | Description |
|-------|------|---------|----------|-------------|
| `Name` | string | — | yes | Unique name used in routing and logs. |
| `Instructions` | string | — | yes | System prompt defining the agent's persona and behavior. |
| `Description` | string | — | no | Short description used by LLM selection strategies. |
| `Model` | string or object | — | yes | Model to use. See [Models](models.md). |
| `Plugins` | array | `[]` | no | Built-in or MCP plugin names to load into this agent's kernel. See [Plugins](plugins.md). |
| `Capabilities` | object | `{}` | no | Per-plugin capability filter. Keys are plugin names; values are arrays of capability tags. Only tools covered by a listed tag are registered. Omitting a plugin allows all its tools. See [Capabilities](#capabilities). |
| `FunctionChoice` | string | `"auto"` | no | Tool-use enforcement: `auto`, `required`, or `none`. |
| `MaxToolCallsPerTurn` | int | `0` | no | Hard cap on tool calls per turn. `0` means no limit. When exceeded, the turn ends with an error injected into history. |
| `MaxInTurnContextTokens` | int | `0` | no | Soft cap on in-turn context tokens. `0` means no limit. A `context_cap_warning` event is emitted when exceeded. |
| `TrustScore` | number | `0.7` | no | Governance trust score (0.0–1.0) used to assign an execution ring. See [Governance](governance.md#execution-rings). |
| `ContextWindow` | object | — | no | Filters the conversation history before it reaches this agent. See [ContextWindow](#contextwindow). |
| `EnableMemory` | bool | `false` | no | When `true`, persistent memories from `~/.fuseraft/memory/agents/{Name}/` are prepended to the agent's instructions at session start. See [Memory](#memory). |
| `SubAgentModel` | string | — | no | Model ID override for the sub-agent spawned by the `SubAgent` plugin. Defaults to the parent agent's model when unset. Useful for running a cheaper model (e.g. Haiku) for exploratory `sub_agent_explore` calls. |
| `SubAgentPlugins` | array | — | no | Explicit list of plugin names to load into the sub-agent. When unset the sub-agent receives the default read-only set: FileSystem read, Search, Shell read, Git read. |
| `RemoteAgent` | object | — | no | Delegates this agent slot to a remote A2A agent. When set, `Model`, `Plugins`, `FunctionChoice`, and `Capabilities` are ignored. See [RemoteAgent](#remoteagent). |

### Capabilities

Per-plugin tool filter. Keys are plugin names; values are arrays of capability tags. Only tools whose tag appears in the list are registered on the agent. Omitting a plugin grants all its tools.

```yaml
- Name: Reviewer
  Plugins:
    - FileSystem
    - Git
  Capabilities:
    FileSystem: [read]
    Git:        [read]
```

| Plugin | Capability tags |
|--------|----------------|
| `FileSystem` | `read` (read_file, grep_file, get_file_summary, get_file_info, list_files) · `write` (write_file, patch_file, save_file_summary, create_directory, copy_file, move_file, set_permissions) · `delete` (delete_file, delete_directory) |
| `Shell` | `read` (shell_get_env, shell_get_job_status, shell_get_job_output, shell_which, shell_get_working_directory) · `run` (shell_run, shell_run_script, shell_run_background, shell_set_env, shell_kill_job) |
| `Git` | `read` (git_status, git_diff, git_log, git_show, git_branch_list, git_stash_list) · `write` (git_add, git_commit, git_checkout, git_create_branch, git_init, git_push, git_pull, git_stash, git_stash_pop, git_reset) |
| `Http` | `get` · `head` · `post` · `put` · `patch` · `delete` — one per HTTP verb |
| `Json` | `read` · `write` (json_merge) |
| `Search` | `read` |
| `Changes` | `read` |
| `Scratchpad` | `read` · `write` |
| `Chatroom` | `read` · `write` |
| `Probe` | `run` |
| `CodeExecution` | `read` (code_execution_check_docker) · `execute` (sandbox_run, repl_*) |

Tools not in the capability map (e.g. MCP-registered tools) always pass through unfiltered.

### SubAgent

When the `SubAgent` plugin is listed in `Plugins`, the agent gains access to `sub_agent_explore`. By default the sub-agent uses the same model as its parent and receives a read-only tool set. Override either with:

```yaml
- Name: Developer
  Plugins:
    - FileSystem
    - Shell
    - SubAgent
  SubAgentModel: claude-haiku-4-5-20251001
  SubAgentPlugins:
    - FileSystem
    - Search
    - Git
```

The sub-agent runs to completion and returns a prose summary to the calling agent. It does not share the parent's conversation history.

### RemoteAgent

Delegates an agent slot to a remote process that implements the [A2A protocol](https://google.github.io/A2A/). The agent card is fetched from `{Url}/.well-known/agent.json` at session startup and the agent participates in orchestration identically to locally-hosted agents.

```yaml
- Name: RemoteReviewer
  Instructions: You are a code reviewer. Be thorough.
  TrustScore: 0.65
  RemoteAgent:
    Url: https://reviewer.internal
    TimeoutSeconds: 60
```

| Field | Type | Default | Required | Description |
|-------|------|---------|----------|-------------|
| `Url` | string | — | yes | Base URL of the remote A2A agent. Card is resolved from `{Url}/.well-known/agent.json`. |
| `TimeoutSeconds` | int | `120` | no | HTTP timeout for card resolution and per-turn calls. |

**Fields that apply when `RemoteAgent` is set:** `Name`, `Instructions`, `TrustScore`, `ContextWindow`, `MaxToolCallsPerTurn`, `EnableMemory`.

**Fields that are ignored when `RemoteAgent` is set:** `Model`, `Plugins`, `FunctionChoice`, `Capabilities`, `SubAgentModel`, `SubAgentPlugins` — those are properties of the remote agent.

### FunctionChoice

| Value | Behavior |
|-------|----------|
| `auto` | The model may call tools or respond with text. Good for planning agents. |
| `required` | The model must call at least one tool every turn. Prevents fabricated tool output. Use for action agents (Developer, Tester). |
| `none` | Tools are registered but the model is not allowed to call them. |

### ContextWindow

Filters the conversation history before it is passed to this agent each turn. Useful for late-stage agents (e.g. a Reviewer) that only need the final text output — not hundreds of tool-call frames accumulated by earlier agents.

```yaml
- Name: Reviewer
  ContextWindow:
    TextOnly: true
```

Filters are applied in order: `TextOnly` / `ExcludeAgents` first, then `MaxTurnAge`, then `MaxTailMessages`. The shared history is never mutated — only the slice passed to this agent's turn is affected.

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `TextOnly` | bool | `false` | Strip all tool-call frames and tool-result messages. Keeps only text-bearing assistant messages and user messages. |
| `ExcludeAgents` | array | `[]` | Remove all messages (text and tool frames) authored by these agents. Tool-result messages are also stripped when this list is non-empty. |
| `MaxTurnAge` | int | `0` | Keep only messages from the last N agent turns (each turn ends at an assistant reply). Applied after `TextOnly`/`ExcludeAgents` and before `MaxTailMessages`. Semantic alternative to a raw message count — discards entire early-session phases rather than an arbitrary number of messages. `0` means no limit. |
| `MaxTailMessages` | int | `0` | After the above filters, keep only the last N messages. `0` means no limit. |
| `ContextCapFraction` | double | `0.0` | Soft-cap threshold expressed as a fraction of `MaxTailMessages` (e.g. `0.8` = 80%). When the filtered count exceeds this threshold a `context_cap_warning` event is emitted. Does not change trim behavior — use `MaxTailMessages` to hard-cap. `0.0` disables the warning. |

**`TextOnly: true`** is the primary lever for context reduction. A Reviewer that independently re-reads files and re-runs commands gains nothing from hundreds of tool results produced by the Developer — stripping them can reduce input tokens by 90%+ in typical sessions.

**`ExcludeAgents`** goes further: removes an entire agent's contribution from the history. Use when one agent's output is irrelevant to another (e.g. a Planner's analysis is not useful to a code Reviewer).

**`MaxTurnAge`** is a semantic alternative to `MaxTailMessages`. Rather than counting raw messages, it counts *agent turns* backward and discards everything before the cut-point. Set it on agents that only need to understand the most recent phase of a session (e.g. a Reviewer that should see the last 5 turns, regardless of how many tool frames each turn produced).

**`MaxTailMessages`** provides a hard message count cap after the other filters. Useful when even filtered text history is still too long for a terminal agent.

---

## Memory

When `EnableMemory: true` is set on an agent, fuseraft loads that agent's persistent memory store at session start and prepends a structured block to its instructions:

```yaml
- Name: Developer
  EnableMemory: true
  Instructions: You are a software engineer...
```

**How it works**

Memories are stored as Markdown files with YAML frontmatter in `~/.fuseraft/memory/agents/{Name}/`. An index file (`MEMORY.md`) maintains a one-line-per-entry listing in injection order.

At session start, each memory entry is rendered into the agent's instructions as:

```
## Persistent Memory

- [memory-name] (type): One-line description of the memory
```

When `EnableMemory: false` (the default), no memory is loaded and the directory is not read.

**Memory storage location**

| Context | Path |
|---------|------|
| REPL sessions | `~/.fuseraft/memory/repl/memory_{guid}.md` |
| Orchestration agents | `~/.fuseraft/memory/agents/{AgentName}/` |

The `{AgentName}` component is sanitized so it is safe as a directory name. Agent memories persist across all sessions and are carried into every future run for that agent.

**REPL auto-memory**

The REPL always loads and saves memories automatically — no config flag is needed. Each REPL memory entry is identified by a UUID (stored in the file's frontmatter and used as its filename).

Memories are **scoped to the working directory** where they were created. A file at `.fuseraft/memory_refs.json` in the current directory records the GUIDs of memories saved there. On session start the REPL loads only the entries listed in that file:

- Directories with a `.fuseraft/` folder but no refs file start with an empty memory set.
- Directories without a `.fuseraft/` folder fall back to loading all global memories (useful outside a project context).

When the session ends, the model is asked to extract new memories from the conversation. Each saved entry is written to the global store and its GUID is registered in the local refs file. Use `/memory` commands to manage them. See [CLI Reference — `/memory`](cli-reference.md#memory-commands).

---

## Selection strategy

```yaml
Selection:
  Type: keyword
  DefaultAgent: Planner
  Routes:
    - Keyword: "HANDOFF TO DEVELOPER"
      Agent: Developer
      Validator: RequireBrief
      SourceAgents:
        - Planner
    - Keyword: "HANDOFF TO TESTER"
      Agent: Tester
      Validators:
        - RequireWriteFile
        - RequireShellPass
      RequiredCommandPattern: "go build|go test"
      SourceAgents:
        - Developer
    - Keyword: "HANDOFF TO REVIEWER"
      Agent: Reviewer
      Validator: TestReportValid
      SourceAgents:
        - Tester
    - Keyword: BUGS FOUND
      Agent: Developer
      SourceAgents:
        - Tester
    - Keyword: REVISION REQUIRED
      Agent: Developer
      SourceAgents:
        - Reviewer
    - Keyword: REPLAN REQUIRED
      Agent: Planner
      SourceAgents:
        - Reviewer
    - Keyword: APPROVED
      Agent: Reviewer
      Validators:
        - RequireShellPass
        - RequireReviewJudgement
      SourceAgents:
        - Reviewer
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Type` | string | `"sequential"` | `sequential`, `keyword`, `llm`, `structured`, or `magentic`. |
| `Routes` | array | — | Required for `keyword`. List of keyword → agent mappings. |
| `StructuredRoutes` | array | — | Required for `structured`. List of condition → agent mappings. See [Strategies](strategies.md#structured). |
| `DefaultAgent` | string | first agent | Fallback agent when no keyword/condition matches (`keyword` and `structured` only). |
| `Prompt` | string | — | Custom prompt template for `llm` selection. |
| `Model` | object | — | Required for `llm` selection. |
| `Magentic` | object | — | Required for `magentic` selection. See [MagenticManagerConfig](#magenticmanagerconfig) below. |

### KeywordRoute

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Keyword` | string | — | Case-insensitive. Must appear **alone on its own line** in the response (after stripping `*`/`_` markdown). A keyword embedded in a sentence or used as a prose section header does not match. |
| `Agent` | string | — | Agent to activate when the keyword fires. When `Agent` matches one of `SourceAgents`, the route is **terminal** — the session ends. |
| `Validator` | string | — | Optional single validator name. Blocks the route until validation passes. Built-in: `RequireBrief`, `RequireWriteFile`, `RequireAllFilesWritten`, `RequireShellPass`, `TestReportValid`, `RequireReviewJudgement`. See [Validators](validators.md). |
| `Validators` | array | — | Optional multiple validators (AND semantics). Use instead of `Validator` when chaining checks (e.g. `["RequireWriteFile", "RequireShellPass"]`). |
| `SourceAgents` | array | any | Optional. When set, the route only fires if the message author is in this list. Prevents agents from triggering routes that belong to other roles. Also determines terminal behavior — see `Agent` above. |
| `RequiredCommandPattern` | string | — | Optional. Used with `RequireShellPass`. The passing command must contain at least one pipe-separated substring (e.g. `"go build\|go test"`). |
| `RequireHumanApproval` | bool | `false` | When `true`, the operator must explicitly approve (`y`) before the route fires. If rejected, the source agent is re-invoked with a "route blocked" message. See [Human-in-the-loop](cli-reference.md#human-in-the-loop-controls). |

### MagenticManagerConfig

Required when `Selection.Type` is `magentic`. Configures the manager LLM that drives the two-level planning and coordination loop.

```yaml
Selection:
  Type: magentic
  Magentic:
    Model:
      ModelId: gpt-4o
    MaxRoundCount: 20
    MaxStallCount: 3
    MaxResetCount: 2
    EnablePlanReview: false
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Model` | string or object | — | **Required.** Model for the manager LLM. A reasoning-capable model is strongly recommended. |
| `Instructions` | string | built-in | Optional system instructions. A sensible default is used when omitted. |
| `MaxRoundCount` | int | `20` | Hard cap on inner-loop coordination rounds before the session terminates. |
| `MaxStallCount` | int | `3` | Consecutive rounds without forward progress before a replan is triggered. |
| `MaxResetCount` | int | `2` | Maximum number of replanning cycles. After this limit the session terminates. |
| `EnablePlanReview` | bool | `false` | When `true`, pauses after the initial plan and waits for HITL review before starting coordination. |

**Note:** for `magentic` selection, the `Termination` section is ignored. Session end is controlled entirely by the three count fields above. See [Strategies — magentic](strategies.md#magentic) for full detail.

See [Strategies](strategies.md) for full detail.

---

## Termination strategy

```yaml
Termination:
  Type: composite
  MaxIterations: 40
  Strategies:
    - Type: regex
      Pattern: \bAPPROVED\b
      AgentNames:
        - Reviewer
    - Type: maxiterations
      MaxIterations: 40
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Type` | string | `"composite"` | `regex`, `maxiterations`, or `composite`. |
| `Pattern` | string | — | Required for `regex`. Regex applied to message content. |
| `MaxIterations` | int | `10` | Hard cap on agent turns (applies to all types as a safety net). |
| `AgentNames` | array | all agents | Optional: restrict regex check to these agents only. |
| `Strategies` | array | — | Required for `composite`. Stops when any child fires. |

See [Strategies](strategies.md) for full detail.

---

## Security constraints

```yaml
Security:
  FileSystemSandboxPath: /home/user/projects/myapp
  HttpAllowedHosts:
    - api.github.com
    - registry.npmjs.org
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `FileSystemSandboxPath` | string | — | Restricts FileSystem and Shell plugins to this directory tree. |
| `HttpAllowedHosts` | array | `[]` | Hostname allowlist for the Http plugin. Empty = unrestricted (private IPs always blocked). |
| `AllowPrivateHosts` | bool | `false` | Bypass the private/loopback IP check. For local dev and sandbox environments only — **do not set in production**. |
| `ReadFileSizeLimit` | int | `20000` | Max characters returned by a single `read_file` call (~5k tokens at default). Raise for large-file workloads; lower for agents with small context windows. |

See [Security](security.md) for full detail.

---

## Token budget

```yaml
MaxTotalTokens: 200000
```

The run stops before the next agent turn if the cumulative token count (input + output across all turns) exceeds this value. The session is saved and can be resumed. Token counts are always exact — reported directly by the provider API.

---

## Change tracking

```yaml
ChangeTracking:
  Path: .fuseraft/changes.json
```

When present, the orchestrator attaches a `ChangeTracker` to every agent's kernel. After each agent text turn it flushes a structured JSON entry recording exactly which tool calls completed: files written or deleted, shell commands run (with pass/fail status), and git commits made.

The change log is consumed in two ways:

- **Agents** — add `"Changes"` to a Tester or Reviewer agent's `Plugins` list and call `changes_read_latest` to see what the previous agent did. See [Plugins](plugins.md#changes).
- **Validators** — set `Validation.ChangeLogPath` to the same path to enable check 8 in `TestReportValid` (cross-referencing report commands against actually-run commands) and to allow `RequireAllFilesWritten` to count files written in prior turns.

**Intent log** — Alongside `changes.json`, the orchestrator also writes `.fuseraft/intents.json`. Unlike the change log (which records what happened *after* a tool call returns), the intent log records what is *about to happen* before the call executes, then updates the entry `APPLIED` or `FAILED` when it completes. On session resume, any `PENDING` entries represent operations that were in-flight at the time of interruption and can be replayed or skipped. The intent log also backs the `"intent"` compaction mode. See [Conversation compaction](#conversation-compaction).

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Path` | string | `.fuseraft/changes.json` | Path to write the change log. Relative paths resolve against the current working directory. |
| `IntentLogPath` | string | _(derived)_ | Path to write the intent log. When omitted, the path is derived from `Path` by replacing the filename with `intents.json` in the same directory. |

**Omit** `ChangeTracking` entirely if you don't need cross-agent observability or the command cross-reference check.

---

## Events

Emit a structured JSONL stream of session events to a file on disk:

```yaml
Events:
  Path: .fuseraft/events.jsonl
```

Each line is a JSON object:

```json
{ "ts": "2025-10-01T12:00:00Z", "session": "a3f92c1d", "agent": "Tester", "turn": 5, "event_type": "turn_end", "payload": { "input_tokens": 1200, "output_tokens": 340 } }
{ "ts": "2025-10-01T12:00:02Z", "session": "a3f92c1d", "agent": "Developer", "turn": 6, "event_type": "validation_fail", "payload": { "validator": "RequireWriteFile", "consecutive": 1 } }
```

| Field | Description |
|-------|-------------|
| `ts` | ISO-8601 UTC timestamp |
| `session` | Session ID |
| `agent` | Agent name (null for session-level events) |
| `turn` | 1-based turn counter |
| `event_type` | Event identifier. Session lifecycle: `session_start`, `session_end`, `phase_start`, `phase_end`, `compaction`, `session_error`. Per-turn: `turn_start`, `turn_end`, `turn_timeout`, `reasoning`. Routing: `keyword_detected`, `multi_keyword`, `no_keyword`, `keyword_not_found`, `agent_routed`, `state_advanced`, `context_cap_warning`, `correction_injected`. Validation: `validation_fail`, `hitl_escalation`. Saga: `saga_compensating`, `saga_compensated`. Magentic: `magentic_plan`, `magentic_replan`, `magentic_complete`. Infrastructure: `tool_blocked`, `tool_call`, `circuit_breaker_open`, `http_reasoning`. Sub-agent: `sub_agent_start`, `sub_agent_end`. |
| `payload` | Event-specific JSON object |

**`turn_end` payload:** `{ input_tokens, output_tokens }` — accumulated across all API calls within the turn.

**`validation_fail` payload:** `{ validator, consecutive }` — name of the blocking validator and how many times in a row it has fired for this agent.

**`hitl_escalation` payload:** `{ message }` — the error message surfaced to the user when a validator fires 3 consecutive times and the session stalls.

**Omit** `Events` if you don't need the event stream.

---

## Conversation compaction

```yaml
Compaction:
  TriggerTurnCount: 30
  KeepRecentTurns: 8
  Mode: lossless
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `TriggerTurnCount` | int | `50` | Compaction fires when the assistant-turn count reaches this value. Ignored in `window` mode (token-budget trigger is used instead). Must be greater than `KeepRecentTurns`. |
| `KeepRecentTurns` | int | `10` | Number of most-recent turns preserved verbatim after compaction. Ignored in `window` mode. |
| `Model` | object | first agent's model | Model used for generating the summary (`llm` and `hybrid` modes only). |
| `Mode` | string | `"llm"` | Compaction mode. See below. |
| `TokenBudget` | int | `80000` | Estimated token budget for `window` mode. Oldest message pairs are dropped until the total estimated token count (characters ÷ 4) falls within this limit. Ignored by all other modes. |

**Compaction modes**

| Mode | Behavior |
|------|----------|
| `llm` | Default. An LLM call summarises the compacted turns. Requires a model. |
| `lossless` | Reconstructs context entirely from the evidence graph, contract evaluations, and state machine position — no LLM call, no hallucination risk. Falls back to `llm` when no state machine strategy is active. |
| `hybrid` | Prepends the lossless reconstruction before the LLM summary, giving agents both authoritative ground-truth and the narrative context of what happened. |
| `window` | Sliding window: drops the oldest user+assistant pairs until the estimated token count is within `TokenBudget`. No LLM call; no summary message is injected. Trigger is token-budget based rather than turn-count based, so `TriggerTurnCount` and `KeepRecentTurns` are ignored. |
| `intent` | Reconstructs context from the intent log (`intents.json`). Produces a deterministic `✓`/`✗` per-operation block for every tool call in the compacted range — no LLM call, no hallucination. Requires `ChangeTracking` to be configured. Falls back to `lossless` then `llm` when unavailable. |

`lossless` and `hybrid` require an active `statemachine` selection strategy with an `EvidenceStore` configured. When the snapshotter is unavailable, the compactor falls back to `llm` mode automatically and logs a warning.

`intent` mode requires `ChangeTracking` to be configured. It reads from the intent log which records every tracked tool call before and after execution. Unlike `lossless`, it works with any selection strategy, not just state machines.

When `ChangeTracking` or `Validation.ChangeLogPath` is also configured, the `llm` and `hybrid` compactors automatically read `changes.json` and include it in the summary prompt as authoritative ground truth. See [Sessions](sessions.md) for full detail.

---

## Routing validators

```yaml
Validation:
  BriefPath: .fuseraft/brief.json
  TestReportPath: .fuseraft/test-report.json
  TestAssertionPatterns:
    - tester::assert
    - "if .+ throw"
    - \bassert\b
    - \bexpect\b
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `BriefPath` | string | `.fuseraft/brief.json` | Canonical path for the project brief. Required by `RequireBrief` and `TestReportValid`. |
| `TestReportPath` | string | `.fuseraft/test-report.json` | Canonical path for the test report. Required by `TestReportValid`. |
| `ChangeLogPath` | string | — | Path to `changes.json` produced by `ChangeTracking` (must match `ChangeTracking.Path`). Enables check 8 in `TestReportValid` and prior-turn file detection in `RequireAllFilesWritten`. |
| `TestAssertionPatterns` | array | see above | Regex patterns that identify real assertion calls in test files. |

See [Validators](validators.md) for full detail.

---

## Telemetry

Export traces and metrics to any OpenTelemetry-compatible backend (Jaeger, Grafana Tempo, Honeycomb, Datadog, etc.):

```yaml
Telemetry:
  OtlpEndpoint: http://localhost:4317
  ServiceName: my-team
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `OtlpEndpoint` | string | `"http://localhost:4317"` | OTLP gRPC endpoint for both traces and metrics. |
| `ServiceName` | string | orchestration `Name` | Service name reported in trace/metric attributes. |

**What is exported**

*Traces* — one span per agent turn (`agent.turn/<AgentName>`), tagged with `agent.name`, `model.id`, `turn.index`, `tokens.input`, `tokens.output`, and `duration_seconds`. MAF internal AI spans are forwarded automatically via `Microsoft.Agents.AI*`.

*Metrics*

| Instrument | Type | Unit | Description |
|------------|------|------|-------------|
| `fuseraft.agent.turns` | counter | — | Agent turns completed |
| `fuseraft.tokens.input` | counter | — | Total input tokens consumed |
| `fuseraft.tokens.output` | counter | — | Total output tokens produced |
| `fuseraft.agent.duration_seconds` | histogram | s | Wall-clock seconds per turn |

All instruments carry `agent.name` and `model.id` attributes so you can slice by agent or model.

**Quick start with Jaeger**

```bash
docker run --rm -p 4317:4317 -p 16686:16686 jaegertracing/all-in-one
```

Then add `"Telemetry": { "OtlpEndpoint": "http://localhost:4317" }` to your config and open `http://localhost:16686`.

**Omit** `Telemetry` entirely if you don't need OTel export.

---

## Checkpoint

Controls where session checkpoints are stored. Checkpoints enable `--resume` and protect against losing progress if a session is interrupted.

```yaml
Checkpoint:
  Mode: json
  Path: .fuseraft/checkpoints
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Mode` | string | `"json"` | `"json"` — write each checkpoint as an individual JSON file. `"memory"` — keep checkpoints in memory only; nothing is written to disk and `--resume` is unavailable. |
| `Path` | string | `~/.fuseraft/sessions/` | Directory for checkpoint files (`json` mode only). Relative paths resolve against the working directory. Use a project-local path (e.g. `.fuseraft/checkpoints`) to keep checkpoints alongside the project instead of in the global user store. |

**When to use a project-local path**

By default, all sessions land in `~/.fuseraft/sessions/` regardless of which project they belong to. Setting `Path` to `.fuseraft/checkpoints` scopes sessions to the project directory:

- `fuseraft sessions` (which always reads the global store) will no longer show these sessions unless you also set the path there — use `fuseraft run --resume` from the project directory instead
- If you add `.fuseraft/checkpoints/` to `.gitignore`, checkpoints stay local and off version control

**When to use `memory` mode**

Use `"Mode": "memory"` for short-lived or automated runs where persistence is not needed (e.g. CI pipelines, integration tests). The session runs normally but leaves no files behind.

**Omit** `Checkpoint` entirely to use the default (`json` mode, global `~/.fuseraft/sessions/` path).

---

## ApiProfiles

Named API endpoint profiles that agents can reference via the `profile` parameter of any `Http` plugin function. A profile bundles a base URL, default headers, and a timeout so agents can make authenticated API calls without embedding credentials in their instructions.

```yaml
ApiProfiles:
  snow:
    BaseUrl: "https://mycompany.service-now.com/api/now"
    TimeoutSeconds: 60
    DefaultHeaders:
      Authorization: "Bearer ${SNOW_API_TOKEN}"
      Accept: "application/json"
      Content-Type: "application/json"
  github:
    BaseUrl: "https://api.github.com"
    TimeoutSeconds: 30
    DefaultHeaders:
      Authorization: "token ${GITHUB_TOKEN}"
      Accept: "application/vnd.github+json"
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `BaseUrl` | string | `""` | Base URL prepended to relative paths passed by agents. Absolute URLs passed by agents are used as-is. |
| `DefaultHeaders` | object | `{}` | Headers merged into every request that uses this profile. Per-call headers override these on conflicts. Values support `${ENV_VAR}` expansion (see below). |
| `TimeoutSeconds` | int | `30` | Request timeout for this profile. Used when the agent does not supply an explicit timeout. |

### `${ENV_VAR}` token expansion

Both `HttpAllowedHosts` entries and `ApiProfiles` values (base URL and all header values) support `${VARIABLE_NAME}` tokens. Tokens are expanded at startup using the process environment — the resolved values are never written back to disk or shown in logs.

```yaml
Security:
  HttpAllowedHosts:
    - "${SNOW_HOST}"          # expanded once at startup

ApiProfiles:
  snow:
    BaseUrl: "https://${SNOW_HOST}/api/now"
    DefaultHeaders:
      Authorization: "Bearer ${SNOW_API_TOKEN}"
```

Tokens that reference unset variables are replaced with an empty string (matching shell behaviour). Profile header keys are **not** expanded — only values are.

### How profiles are used by agents

An agent with the `Http` plugin can reference a profile by name:

```
# Agent instruction or tool call:
http_get(url="/table/incident?state=1&limit=10", profile="snow")
```

This resolves to:
```
GET https://mycompany.service-now.com/api/now/table/incident?state=1&limit=10
Authorization: Bearer <value of $SNOW_API_TOKEN>
Accept: application/json
```

The allowlist (`HttpAllowedHosts`) is still enforced after profile resolution — a profile whose resolved host is not on the list is denied.

See [Plugins → Http](plugins.md#http) for the full function reference including the `profile` parameter.

---

## Evidence store

Enables a structured, queryable evidence graph alongside `changes.json`. When configured, every `write_file`, shell command, and git commit is recorded as a typed node with richer metadata. Evidence contracts query the graph for more accurate results than scanning the flat log.

```yaml
EvidenceStore:
  Path: .fuseraft/evidence.json
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Path` | string | `.fuseraft/evidence.json` | File path for the evidence graph JSON. The directory is created automatically. |

**Node types recorded:**

| NodeType | Recorded when |
|----------|--------------|
| `FileWrite` | Agent calls `write_file` or `patch_file` |
| `FileDelete` | Agent calls `delete_file` |
| `CommandRun` | Agent calls `shell_run` (exit code captured) |
| `GitCommit` | Agent calls `git_commit` |
| `TestResult` | Future: emitted by test report plugins |

Nodes are linked by typed edges (`produced_by`, `verified_by`, `depends_on`) so contracts can express causal relationships rather than just presence checks.

Omit `EvidenceStore` if you don't use evidence contracts or lossless compaction. The flat `changes.json` from `ChangeTracking` is sufficient for the built-in validators.

---

## Evidence contracts

Named, composable transition gates that check what must be true on disk before a route or state machine transition fires. Contracts replace or supplement individual validators with reusable, YAML-declared predicates.

```yaml
Contracts:
  - Name: ImplementationComplete
    Requires:
      - FilesWritten:
          Source: .fuseraft/brief.json
          Field: files_to_change
      - CommandSucceeded:
          Pattern: "build|compile|go build|cargo build"

  - Name: TestsValid
    Requires:
      - FileExists:
          Path: .fuseraft/test-report.json
      - TestReport:
          NoFailures: true
          HasAssertions: true
```

Contracts are referenced by name from keyword route `Contracts` lists or from state machine transition `Contract`/`Contracts` fields.

**Predicate types**

| Type | Fields | Passes when |
|------|--------|-------------|
| `FilesWritten` | `Source`, `Field` | Every path listed in the `Field` array of the `Source` JSON file has been written to disk (current session). |
| `CommandSucceeded` | `Pattern` | At least one shell command whose text matches any pipe-separated alternative in `Pattern` exited 0 this session. |
| `FileExists` | `Path` | The file at `Path` exists on disk. |
| `TestReport` | `NoFailures`, `HasAssertions` | `test-report.json` exists, has results, and satisfies the declared checks. |

All predicates within a contract use AND semantics — every predicate must pass. All contracts on a route also use AND semantics.

**Query source precedence:** contracts query the `EvidenceStore` graph when configured; they fall back to reading `changes.json` directly when the evidence store is absent.

**Example: keyword route with contracts**

```yaml
Selection:
  Type: keyword
  Routes:
    - Keyword: "HANDOFF TO TESTER"
      Agent: Tester
      Contracts:
        - ImplementationComplete
      SourceAgents:
        - Developer
```

**Example: state machine transition with contracts**

```yaml
Selection:
  Type: statemachine
  StateMachine:
    Initial: Implementation
    States:
      Implementation:
        Agent: Developer
        Transitions:
          - To: Testing
            Signal: "HANDOFF TO TESTER"
            Contract: ImplementationComplete
```

See [Strategies — statemachine](strategies.md#statemachine) for the full state machine reference.

---

## Failure handling

Classifies routing validator and contract failures into typed failure modes and applies a targeted response instead of the uniform "N failures → escalate" behaviour.

```yaml
FailureHandling:
  MissingEvidence:
    Action: Reinstruct
    Threshold: 3
  InvalidTransition:
    Action: Reinstruct
    Threshold: 3
  ConflictingEvidence:
    Action: Reinstruct
    Threshold: 2
  NoProgress:
    Action: Abort
    Threshold: 3
```

The values shown are the defaults — omitting `FailureHandling` entirely produces identical behaviour.

**Failure types**

| Type | Detected when |
|------|--------------|
| `MissingEvidence` | Error message contains phrases like "not found", "does not exist", "missing" |
| `InvalidTransition` | Agent emitted a handoff without completing prerequisites |
| `ConflictingEvidence` | Error message contains phrases like "fake", "hallucin", "inconsistent", "never ran" |
| `NoProgress` | Agent re-emitted the handoff without calling any tools since the last correction (requires `SetHistory` wired — automatic for state machine and keyword strategies) |

**Actions**

| Action | Behaviour |
|--------|-----------|
| `Reinstruct` | Inject a targeted correction message and re-invoke the source agent. The message is tailored to the failure type: missing artifact, invalid transition, or conflicting evidence. |
| `ActivateRecovery` | Immediately activate the route's `RecoveryAgent` (if configured) without waiting for the threshold. |
| `EscalateToHuman` | Immediately throw `ValidatorStuckException` regardless of `Threshold`. |
| `Abort` | Continue injecting corrections until `Threshold` consecutive failures are reached, then escalate to HITL. |

**Threshold** controls how many consecutive failures of that type trigger escalation (for `Abort`). `EscalateToHuman` and `ActivateRecovery` ignore the threshold and fire immediately.

---

## Verifier

Configures a self-verification meta-agent that periodically audits the evidence graph for inconsistencies and challenges unverified claims. The verifier is a regular agent in your `Agents` list with a special role: it reads evidence and reports findings rather than doing primary work.

```yaml
Verifier:
  AgentName: Verifier
  EveryNTurns: 5
  TriggerOnSuspiciousTransition: true
  FindingsKeyword: INCONSISTENCY
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `AgentName` | string | — | **Required.** Must match an agent name in `Agents` exactly. |
| `EveryNTurns` | int | `0` | Run the verifier every N agent turns. `0` disables periodic verification. |
| `TriggerOnSuspiciousTransition` | bool | `true` | When using `statemachine` routing, automatically invoke the verifier on the next turn after a `ConflictingEvidence` or `NoProgress` contract failure. |
| `FindingsKeyword` | string | `"INCONSISTENCY"` | If this word appears (case-insensitive) in the verifier's output, a correction message is injected into history for the next agent. |

**How it works**

- **Periodic:** after every N-th primary agent turn, the verifier agent runs for one turn. Its output is added to history and visible to subsequent agents.
- **Suspicious transition:** when the state machine detects `ConflictingEvidence` or `NoProgress`, it schedules a verifier turn before re-invoking the primary agent. The verifier audits the evidence and its findings are in history when the primary agent retries.
- **Findings injection:** when the verifier's response contains `FindingsKeyword`, a `VERIFICATION FINDING` message is appended to history attributing the finding to the verifier. The next primary agent sees this and must reconcile before continuing.

**Verifier agent instructions (example)**

```yaml
- Name: Verifier
  Instructions: |
    You are an evidence auditor. Your job is to detect inconsistencies between
    what agents claim and what is recorded in the evidence graph.

    Steps:
    1. Call changes_read_latest to read what was actually done.
    2. Compare recorded file writes, commands, and exit codes against any claims
       made in the conversation.
    3. If everything is consistent, write "Evidence verified — no inconsistencies found."
    4. If you find a discrepancy, write "INCONSISTENCY DETECTED:" followed by a concise
       description of what was claimed vs. what the evidence shows.
  Model:
    ModelId: gpt-4o-mini
  Plugins:
    - Changes
```

Omit `Verifier` entirely to disable self-verification. The verifier adds LLM calls (one per periodic trigger or suspicious transition), so tune `EveryNTurns` to balance audit frequency against cost.

---

## Minimal config

The smallest valid config that does something useful:

```yaml
Orchestration:
  Name: SingleAgent
  Agents:
    - Name: Assistant
      Instructions: You are a helpful assistant with filesystem access.
      Model:
        ModelId: gpt-4o
      Plugins:
        - FileSystem
  Selection:
    Type: sequential
  Termination:
    Type: maxiterations
    MaxIterations: 5
```

JSON is also supported (same schema, `Orchestration:` becomes `"Orchestration": {}`). See the intro at the top of this page.
