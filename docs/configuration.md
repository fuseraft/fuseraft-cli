# Configuration Reference

All orchestration settings live in a single config file (default: `.fuseraft/config/orchestration.yaml`). Both **YAML** and **JSON** formats are supported. The file must have a top-level `Orchestration` key.

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
fuseraft run --config .fuseraft/config/my-team.yaml
fuseraft validate .fuseraft/config/my-team.yaml
```

YAML is often more readable for configs with long agent instructions (block scalars avoid JSON escape sequences) or complex routing tables. Both formats bind to the same schema — every field documented below works identically in either format. See `config/examples/orchestration.yaml` for a complete YAML example.

---

## Top-level fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `SchemaVersion` | string | — | Optional config format version (e.g. `"2026-05"`). When set, fuseraft-cli validates that it recognizes this version and logs a warning if not. Useful for catching upgrades that silently change field semantics. Omit to skip version validation. |
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
| `ContextBudget` | object | — | Per-agent cumulative input-token budget. Warns and triggers compaction rather than terminating. Requires `Compaction` when `CutoverAt` is set. See [Context budget](#context-budget). |
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
| `SkillCuration` | object | — | Automatically author a reusable skill from each completed session. See [Skill curation](#skill-curation). |
| `Contracts` | array | — | Named evidence contracts reusable across routes and state machine transitions. See [Evidence contracts](#evidence-contracts). |
| `FailureHandling` | object | — | Per-failure-type policies (action + threshold) applied when routing validators or contracts block a handoff. See [Failure handling](#failure-handling). |
| `Verifier` | object | — | Self-verification meta-agent that audits the evidence graph for inconsistencies. See [Verifier](#verifier). |
| `Brownfield` | object | — | Brownfield-mode settings: recon phase support, change envelope seeding, and convention profile injection. See [Brownfield mode](#brownfield-mode). |
| `TestSelector` | object | — | Incremental test-selection settings. Exposes a shell command template for finding the minimal test set for a changed file. See [Test selector](#test-selector). |
| `Output` | object | — | Reporting settings for `fuseraft run`, for scripted/automated invocations. See [Output](#output). |

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

**Full injection order** — `OrchestratorBuilder` assembles each agent's final system prompt in this sequence before the session starts:

| # | Block | Source |
|---|-------|--------|
| 1 | Base prompt | `SystemPromptPath` → `SystemPrompt` → embedded `FUSERAFT.md` |
| 2 | Agent `Instructions` | Per-agent field in the config |
| 3 | `.fuseraft/` folder orientation | Auto-injected from `FuseraftPaths.BuildFolderOrientationBlock()` — gives every agent a compact manifest of the runtime directory so they never call `list_files` on `.fuseraft/` to discover it. See [Directory layout](design.md#3-directory-layout). |
| 4 | Context store summary | Appended when `.fuseraft/context/index.json` has entries (see [Context store](context-store.md)) |
| 5 | Convention profile | Appended when `Brownfield.ConventionProfilePath` exists (Brownfield mode) |
| 6 | Test selector hint | Appended when `TestSelector.FindRelatedCommand` is configured |

In **REPL mode** the same folder orientation is injected (blocks 3 onward), but the log-file entries are omitted from the manifest because the session section of the REPL system prompt already lists them and directs the agent to the `repl_session_*` tools for log access.

`SubAgentPlugin` (used by `sub_agent_explore` and `sub_agent_locate`) receives a single-line skip directive instead of the full manifest, since its system prompt is tightly budgeted.

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
| `AgentFile` | string | — | no | Path to a YAML file that provides the base agent definition. Relative paths resolve against the orchestration config's directory. Inline fields that differ from their defaults override the file; fields left at defaults are inherited. See [Agent files](#agent-files). |
| `Name` | string | — | yes | Unique name used in routing and logs. |
| `Instructions` | string | — | yes | System prompt defining the agent's persona and behavior. |
| `Description` | string | — | no | Short description used by LLM selection strategies. |
| `Model` | string or object | — | yes | Model to use. See [Models](models.md). |
| `Plugins` | array | `[]` | no | Built-in or MCP plugin names to load into this agent's kernel. See [Plugins](plugins.md). |
| `Capabilities` | object | `{}` | no | Per-plugin capability filter. Keys are plugin names; values are arrays of capability tags. Only tools covered by a listed tag are registered. Omitting a plugin allows all its tools. See [Capabilities](#capabilities). |
| `FunctionChoice` | string | `"auto"` | no | Tool-use enforcement: `auto`, `required`, or `none`. |
| `MaxToolCallsPerTurn` | int | `0` | no | Hard cap on tool calls per turn. `0` means no limit. When exceeded, the turn ends with an error injected into history. |
| `MaxInTurnContextTokens` | int | `0` | no | Soft cap (budget-reactive) on in-turn context tokens. `0` means no limit. Before each inner LLM call the oldest tool-result messages are replaced with placeholders until the total is under this budget. |
| `MaxInTurnToolPairs` | int | `0` | no | Hard sliding-window cap (deterministic) on the number of tool call/result pairs kept in full within a turn. Before every inner LLM call, all but the most-recent N pairs are replaced with placeholders unconditionally — regardless of total token count. `0` means no limit. Recommended: 8–16 for high-volume action agents. |
| `TrustScore` | number | `0.7` | no | Governance trust score (0.0–1.0) used to assign an execution ring. See [Governance](governance.md#execution-rings). |
| `ContextWindow` | object | — | no | Filters the conversation history before it reaches this agent. See [ContextWindow](#contextwindow). |
| `SubAgentModel` | string | — | no | Model ID override for the sub-agent spawned by the `SubAgent` plugin. Defaults to the parent agent's model when unset. Useful for running a cheaper model (e.g. Haiku) for `sub_agent_explore` / `sub_agent_locate` calls. |
| `SubAgentPlugins` | array | — | no | Explicit list of plugin names to load into the sub-agent. When unset the sub-agent receives the default read-only set: FileSystem read, Search, Shell read, Git read. Unknown names raise an error at session startup. |
| `SubAgentMaxToolCalls` | int | `0` | no | Maximum tool-call iterations for `sub_agent_explore`. `0` uses the built-in default of 20. `sub_agent_locate` always uses a hard cap of 5 regardless of this setting. |
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
| `Http` | `get` (http_get, http_head) · `post` · `put` · `patch` · `delete` — one tag per verb, except `http_head` which shares the `get` tag rather than having its own |
| `Json` | `read` · `write` (json_merge) |
| `Document` | `read` (document_extract_text, document_get_info, document_list_sheets, document_get_sheet) |
| `Search` | `read` |
| `Changes` | `read` |
| `Scratchpad` | `read` · `write` |
| `Chatroom` | `read` · `write` |
| `Probe` | `run` |
| `CodeExecution` | `read` (code_execution_check_docker) · `execute` (sandbox_run, repl_*) |
| `Decision` | `read` (decision_search, decision_read) · `write` (decision_create, decision_supersede) |
| `Graph` | `read` (graph_search, graph_refs, graph_dependents — all read-only) |

Tools not in the capability map (e.g. MCP-registered tools) always pass through unfiltered.

### Agent files

`AgentFile` lets you extract a reusable agent definition into a stand-alone YAML file and reference it from any number of orchestration configs. Orchestration configs stay small; agents can be independently versioned, diffed, and shared across projects. Multi-agent templates generated by `fuseraft init` use this layout by default, writing each agent to `agents/{name}.yaml` alongside the main config.

**Agent file format** — the file is a plain YAML object with the same fields as an inline agent entry. No top-level wrapper key is required, though `Agent:` is accepted for tooling compatibility:

```yaml
# agents/reviewer.yaml
Name: Reviewer
Instructions: |
  You are a senior engineer reviewing code changes for correctness,
  test coverage, and adherence to project conventions.
  ...
Model:
  ModelId: claude-opus-4-7
Plugins:
  - FileSystem
  - Git
  - Changes
Capabilities:
  FileSystem: [read]
  Git:        [read]
FunctionChoice: required
TrustScore: 0.85
```

**Referencing from an orchestration config:**

```yaml
Agents:
  - AgentFile: agents/reviewer.yaml   # loads the base definition above

  - AgentFile: agents/reviewer.yaml   # same base, different model for this project
    Model: claude-sonnet-4-6

  - AgentFile: agents/developer.yaml
    Name: LeadDeveloper               # rename the agent for this config's routing rules
    MaxInTurnContextTokens: 40000     # tighter context cap for this environment
    MaxInTurnToolPairs: 12            # deterministic sliding window: keep only last 12 tool results per turn
```

**Override semantics** — inline fields whose value differs from the field's default override the file; fields left at their defaults are inherited. The practical rules:

| Field type | Inline overrides base when… |
|------------|------------------------------|
| string | inline is non-empty |
| array / object | inline is non-empty |
| nullable | inline is non-null |
| int | inline is non-zero |
| `TrustScore` | inline differs from `0.7` |
| `FunctionChoice` | inline differs from `"auto"` |

This means: to inherit a field from the file, simply omit it in the inline config. To override, set it explicitly.

**Validation** — `fuseraft validate` resolves `AgentFile` paths and reports missing files as errors before the session starts.

### SubAgent

When the `SubAgent` plugin is listed in `Plugins`, the agent gains access to two tools:

- **`sub_agent_explore`** — multi-hop exploration loop (up to `SubAgentMaxToolCalls` iterations, default 20). Accepts an optional `format` parameter: `"prose"` (default) or `"file_list"` (bulleted path list).
- **`sub_agent_locate`** — single-target symbol/file lookup, hard-capped at 5 iterations and 512 output tokens.

Both tools inject the current working directory into the sub-agent's system prompt and link the parent's cancellation token so interrupts propagate immediately. The sub-agent does not share the parent's conversation history.

```yaml
- Name: Developer
  Plugins:
    - FileSystem
    - Shell
    - SubAgent
  SubAgentModel: claude-haiku-4-5-20251001   # cheaper model for sub-agent work
  SubAgentMaxToolCalls: 25                   # allow deeper explore loops
  SubAgentPlugins:
    - FileSystem
    - Search
    - Git
```

### RemoteAgent

Delegates an agent slot to a remote process that implements the [A2A protocol](https://a2a-protocol.org/). The agent card is fetched from `{Url}/.well-known/agent.json` at session startup and the agent participates in orchestration identically to locally-hosted agents.

> **Preview:** The A2A protocol integration depends on a pre-release SDK package (`1.0.0-preview2`). A `LogWarning` is emitted at session startup for every agent that uses `RemoteAgent`. The API may change in future releases — verify compatibility before upgrading in production-critical workflows.

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

**Fields that apply when `RemoteAgent` is set:** `Name`, `Instructions`, `TrustScore`, `ContextWindow`, `MaxToolCallsPerTurn`.

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
| `MaxToolResultChars` | int | `0` | Truncate `FunctionResultContent` strings in the replayed history slice to this many characters. A suffix noting the omitted count is appended. `0` disables truncation. See [context-management — Tool-result truncation](context-management.md#tool-result-truncation-maxtoolresultchars). |
| `ToolResultCharOverrides` | object | `{}` | Per-tool-name character cap overrides. Keys are tool function names (case-insensitive); values are the character limit for that tool's results, overriding `MaxToolResultChars`. A value of `0` disables truncation for that tool. Only meaningful when `MaxToolResultChars` is also set. |
| `MaxReplayChars` | int | `0` | Truncate non-summary assistant messages in the replayed history to this many characters. `0` uses the global 2,000-character fallback. Compaction summaries are never truncated. |

**`TextOnly: true`** is the primary lever for context reduction. A Reviewer that independently re-reads files and re-runs commands gains nothing from hundreds of tool results produced by the Developer — stripping them can reduce input tokens by 90%+ in typical sessions.

**`ExcludeAgents`** goes further: removes an entire agent's contribution from the history. Use when one agent's output is irrelevant to another (e.g. a Planner's analysis is not useful to a code Reviewer).

**`MaxTurnAge`** is a semantic alternative to `MaxTailMessages`. Rather than counting raw messages, it counts *agent turns* backward and discards everything before the cut-point. Set it on agents that only need to understand the most recent phase of a session (e.g. a Reviewer that should see the last 5 turns, regardless of how many tool frames each turn produced).

**`MaxTailMessages`** provides a hard message count cap after the other filters. Useful when even filtered text history is still too long for a terminal agent.

---

## Memory

Every agent's persistent memory store is loaded and ranked by relevance before each turn, then
injected into its system prompt automatically by the context assembly pipeline — no per-agent
config is required. See [Context Management — Layer 2](context-management.md#layer-2-persistent-memory-pipeline-injected)
for ranking and injection format details.

**How it works**

Memories are stored as Markdown files with YAML frontmatter in `~/.fuseraft/memory/agents/{Name}/`. An index file (`MEMORY.md`) maintains a one-line-per-entry listing in injection order.

**Memory storage location**

| Context | Path |
|---------|------|
| REPL sessions | `~/.fuseraft/memory/repl/memory_{guid}.md` |
| Orchestration agents | `~/.fuseraft/memory/agents/{AgentName}/` |

The `{AgentName}` component is sanitized so it is safe as a directory name. Agent memories persist across all sessions and are carried into every future run for that agent.

**REPL auto-memory**

The REPL always loads and saves memories automatically — no config flag is needed. Each REPL memory entry is identified by a UUID (stored in the file's frontmatter and used as its filename).

Memories are **scoped to the working directory** where they were created. A file at `~/.fuseraft/sessions/{project_slug}/{session_id}/memory_refs.json` records the GUIDs of memories saved in that session. On session start the REPL loads only the entries listed in that file:

- Directories with a `.fuseraft/` folder but no refs file start with an empty memory set.
- Directories without a `.fuseraft/` folder fall back to loading all global memories (useful outside a project context).

When the session ends, the model is asked to extract new memories from the conversation. Each saved entry is written to the global store and its GUID is registered in the local refs file. Use `/memory` commands to manage them. See [CLI Reference — `/memory`](cli-reference.md#memory-commands).

---

## Pluggable memory provider

The `Memory` top-level key activates a live memory provider that runs pre- and post-turn hooks around every agent turn. The provider fetches fresh context before each turn and can persist the full accumulated history after each turn.

### Providers

#### `local` (default)

Reads from the file-backed per-agent `MemoryStore` at `~/.fuseraft/memory/agents/{Name}/` before every turn. Save is a no-op — the REPL's memory extractor handles writing new facts in interactive sessions.

```yaml
Memory:
  Provider: local
```

#### `webhook`

Delegates load and save to an HTTP endpoint you control. Useful for integrating external vector stores, knowledge graphs, or managed memory services.

```yaml
Memory:
  Provider: webhook
  Webhook:
    LoadUrl: https://memory.example.com/load
    SaveUrl: https://memory.example.com/save
    Headers:
      Authorization: "Bearer ${MEMORY_TOKEN}"
    TimeoutSeconds: 10
    SaveEveryNTurns: 10
```

**Load request** — POST `{"agent": "<name>"}`, expected response `{"block": "<text>"}`. When `block` is null or empty, nothing is prepended.

**Save request** — POST `{"agent": "<name>", "history": [{"role": "...", "content": "..."}]}`. Fire-and-forget; errors are logged but do not interrupt the session.

`SaveEveryNTurns` throttles save calls (default 10) to avoid flooding the endpoint on long sessions.

### WebhookMemoryConfig fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `LoadUrl` | string | — | URL for the pre-turn load POST. Omit to skip loading. |
| `SaveUrl` | string | — | URL for the post-turn save POST. Omit to skip saving. |
| `Headers` | object | `{}` | HTTP headers merged into every request. Values support `${ENV_VAR}` expansion. |
| `TimeoutSeconds` | int | `10` | Per-request HTTP timeout. |
| `SaveEveryNTurns` | int | `10` | Save only every Nth turn; 1 = every turn. |

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
| `Type` | string | `"sequential"` | `sequential`, `roundrobin`, `keyword`, `llm`, `structured`, `statemachine`, `magentic`, `graph`, `adversarial`, `mapreduce`, or `scattergather`. |
| `Routes` | array | — | Required for `keyword`. List of keyword → agent mappings. |
| `StructuredRoutes` | array | — | Required for `structured`. List of condition → agent mappings. See [Strategies](strategies.md#structured). |
| `DefaultAgent` | string | first agent | Fallback agent when no keyword/condition matches (`keyword` and `structured` only). |
| `Prompt` | string | — | Custom prompt template for `llm` selection. |
| `Model` | object | — | Required for `llm` selection. |
| `Magentic` | object | — | Required for `magentic` selection. See [MagenticManagerConfig](#magenticmanagerconfig) below. |
| `Graph` | object | — | Required for `graph` selection. See [Strategies — graph](strategies.md#graph) for `GraphConfig`, `GraphNodeConfig`, and `GraphEdgeConfig` field references. |
| `MapReduce` | object | — | Required for `mapreduce` selection. See [Strategies — mapreduce](strategies.md#mapreduce) for `MapReduceConfig` field reference. |
| `ScatterGather` | object | — | Required for `scattergather` selection. See [Strategies — scattergather](strategies.md#scattergather) for `ScatterGatherConfig` field reference. |

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
| `MaxIterations` | int | `0` (uncapped) | Hard cap on agent turns (applies to all types as a safety net). `0` means no cap — set explicitly, since nothing else stops a session that never emits a terminating keyword. |
| `AgentNames` | array | all agents | Optional: restrict regex check to these agents only. |
| `Strategies` | array | — | Required for `composite`. Stops when any child fires. |

See [Strategies](strategies.md) for full detail.

---

## Security constraints

```yaml
Security:
  FileSystemSandboxPath: /home/user/projects/myapp
  FileSystemPermissions:
    Read:  [src/**, docs/**]
    Write: [tests/**, docs/**]
    Deny:  [secrets/**, infra/prod/**]
  ShellPolicy:
    Allow: ["go test", "npm test"]
    Deny:  ["rm -rf", "curl | bash"]
  HttpAllowedHosts:
    - api.github.com
    - registry.npmjs.org
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `FileSystemSandboxPath` | string | — | Restricts FileSystem and Shell plugins to this directory tree. |
| `FileSystemPermissions` | object | — | Granular read/write/deny glob rules applied within the sandbox. Requires `FileSystemSandboxPath`. See [Security → Filesystem permissions](security.md#filesystem-permissions-read-write-deny-globs). |
| `FileSystemPermissions.Read` | array | `[]` | When non-empty, read operations are restricted to matching paths. |
| `FileSystemPermissions.Write` | array | `[]` | When non-empty, write operations are restricted to matching paths. Evaluated alongside `ChangeEnvelope`; both must match when both are set. |
| `FileSystemPermissions.Deny` | array | `[]` | Paths matching these globs are hard-denied for all operations (read and write). Checked before `Read`/`Write`. |
| `ShellPolicy` | object | — | Allow/deny substring policy for shell commands. Works without `FileSystemSandboxPath`. See [Security → Shell policy](security.md#shell-policy). |
| `ShellPolicy.Allow` | array | `[]` | When non-empty, commands must contain at least one pattern to proceed. |
| `ShellPolicy.Deny` | array | `[]` | Commands containing any of these patterns are blocked (checked before `Allow`). |
| `ChangeEnvelope` | array | — | Glob patterns (relative to sandbox root) restricting write operations (`write_file`, `patch_file`, `delete_file`). Reads are unaffected. Auto-populated from the brownfield discovery brief when `Brownfield.SeedEnvelopeFromBrief` is true. See [Security → Change envelope](security.md#change-envelope). |
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

## Context budget

Per-agent cumulative input-token budget enforcement. Unlike `MaxTotalTokens` (which counts combined input + output across all agents and terminates the session), `ContextBudget` counts input tokens per agent independently and responds with automatic compaction rather than termination — keeping long sessions alive indefinitely.

```yaml
ContextBudget:
  WarnAt: 60000                  # warn when any agent accumulates this many input tokens
  CutoverAt: 100000              # compact when cumulative input tokens reach this value
  MaxSingleTurnInputTokens: 200000  # compact before next turn if a single turn exceeded this
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `WarnAt` | int | `0` | Cumulative input-token threshold per agent that triggers a warning. When an agent's accumulated input tokens since the last compaction reach this value, a `⚠` warning is printed to the console and a `context_budget_warn` event is emitted. Fires at most once per agent per compaction cycle. `0` disables the warning. |
| `CutoverAt` | int | `0` | Cumulative input-token threshold per agent that triggers automatic compaction. When reached, compaction runs before the next agent turn and the per-agent counters reset so the next window starts clean. **Requires `Compaction` to be configured.** `WarnAt`, when set, must be less than `CutoverAt`. `0` disables token-based cutover. |
| `MaxSingleTurnInputTokens` | int | `0` | Per-turn input-token ceiling. When a completed turn's input-token count exceeds this value, compaction fires before the *next* turn begins — independently of the cumulative `CutoverAt` counter. Guards against single-turn explosions (an agent reading many large files at once) that exhaust the cumulative budget in one shot and would leave the next turn with an already-bloated history. **Requires `Compaction` to be configured.** `0` disables per-turn enforcement. |
| `MaxToolResultTokens` | int | `0` | Maximum estimated tokens that tool-result messages may contribute to the context slice sent on any single agent invocation. When exceeded, the oldest tool results beyond `InTurnToolWindow` are replaced with one-line tombstones before the LLM call — keeping the model aware of what was done without replaying raw content. The full results remain in the shared history for compaction and audit; only the model's view is trimmed. `0` disables the tool-result window. |
| `InTurnToolWindow` | int | `20` | Number of most-recent tool results to always retain verbatim when `MaxToolResultTokens` is exceeded. Older results beyond this count are tombstoned. Only meaningful when `MaxToolResultTokens > 0`. |

**Threshold alignment:** set `WarnTurnTokens` (the per-turn warning) below `CutoverAt` so the warning fires before compaction is forced. If `WarnTurnTokens >= CutoverAt`, both fire in the same turn, making the warning redundant — `fuseraft validate` emits a warning when this condition is detected.

**How it differs from `MaxTotalTokens`**

| | `MaxTotalTokens` | `ContextBudget` |
|---|---|---|
| Counts | input + output, all agents combined | input tokens per agent independently |
| Response | terminates the session | triggers compaction, session continues |
| Resets | never | after each compaction cycle |

**Counter reset and post-compaction grace:** after each compaction cycle, all per-agent cumulative-input-token counters reset to zero. The first turn after compaction is granted a grace period — `CutoverAt` and `MaxSingleTurnInputTokens` are not enforced on that turn — preventing a thrash loop where the compacted history itself is expensive enough to trigger another immediate compaction.

**Validation:** `fuseraft validate` reports an error if `CutoverAt > 0` or `MaxSingleTurnInputTokens > 0` without a `Compaction` section, or if `WarnAt >= CutoverAt` when both are non-zero.

**Events emitted:**

| Event | When |
|-------|------|
| `context_budget_warn` | Agent's cumulative input tokens ≥ `WarnAt` (once per agent per cycle) |
| `context_budget_cutover` | Cumulative tokens ≥ `CutoverAt`, or single-turn input > `MaxSingleTurnInputTokens` (payload includes `reason: "single_turn_limit"` for the latter) |

**Omit** `ContextBudget` entirely to disable per-agent token tracking. Use `MaxTotalTokens` instead when you want a hard stop rather than transparent recovery.

---

## Change tracking

```yaml
ChangeTracking:
  Path: ~/.fuseraft/state/{project_slug}/changes.json
```

When present, the orchestrator attaches a `ChangeTracker` to every agent's kernel. After each agent text turn it flushes a structured JSON entry recording exactly which tool calls completed: files written or deleted, shell commands run (with pass/fail status), and git commits made.

The change log is consumed in two ways:

- **Agents** — add `"Changes"` to a Tester or Reviewer agent's `Plugins` list and call `changes_read_latest` to see what the previous agent did. See [Plugins](plugins.md#changes).
- **Validators** — set `Validation.ChangeLogPath` to the same path to enable check 8 in `TestReportValid` (cross-referencing report commands against actually-run commands) and to allow `RequireAllFilesWritten` to count files written in prior turns.

**Intent log** — Alongside `changes.json`, the orchestrator also writes `~/.fuseraft/sessions/{project_slug}/{session_id}/intents.json`. Unlike the change log (which records what happened *after* a tool call returns), the intent log records what is *about to happen* before the call executes, then updates the entry `APPLIED` or `FAILED` when it completes. On session resume, any `PENDING` entries represent operations that were in-flight at the time of interruption and can be replayed or skipped. The intent log also backs the `"intent"` compaction mode. See [Conversation compaction](#conversation-compaction).

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Path` | string | `~/.fuseraft/state/{project_slug}/changes.json` | Path to write the change log. Relative paths resolve against the current working directory. |
| `IntentLogPath` | string | `~/.fuseraft/sessions/{project_slug}/{session_id}/intents.json` | Path to write the intent log. This is a fixed default independent of `Path` — it is *not* derived from `Path`'s directory. Set explicitly to relocate it. |

**Omit** `ChangeTracking` entirely if you don't need cross-agent observability or the command cross-reference check.

---

## Events

Emit a structured JSONL stream of session events to a file on disk:

```yaml
Events:
  Path: ~/.fuseraft/logs/sessions/{project_slug}/{session_id}/events.jsonl
```

> **App log** — In addition to this configurable event stream, fuseraft-cli always writes `Warning`-level and higher diagnostic messages to `~/.fuseraft/logs/{project_slug}/app.log` via an always-on Serilog file sink (5 MB per file, 3 retained). This includes store-corruption warnings from `ChangeTracker`, `IntentLog`, `EvidenceStore`, and `FileVersionStore`, as well as structured retry and model-fallover events from the HTTP layer. No configuration needed.
>
> **Secret masking** — All log output (console, `app.log`, and debug sidecar) passes through a secret-masking formatter that redacts API key–like values (`sk-…` keys, `Bearer …` tokens, `api_key=…` query strings) before they are written. Secrets are never visible in logs regardless of verbosity level.

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
| `event_type` | Event identifier. Session lifecycle: `session_start`, `session_end`, `session_summary`, `phase_start`, `phase_end`, `compaction`, `compaction_resume_candidate`, `session_error`. Per-turn: `turn_start`, `turn_end`, `turn_timeout`, `reasoning`, `context_assembly`. Routing: `keyword_detected`, `multi_keyword`, `no_keyword`, `keyword_not_found`, `agent_routed`, `state_advanced`, `back_edge_escalation`, `context_cap_warning`, `correction_injected`. Validation: `validation_fail`, `hitl_escalation`. Context budget: `context_budget_warn`, `context_budget_cutover`. Saga: `saga_compensating`, `saga_compensated`. Magentic: `magentic_plan`, `magentic_replan`, `magentic_complete`. Infrastructure: `tool_blocked`, `tool_call`, `circuit_breaker_open`, `http_reasoning`. Sub-agent: `sub_agent_start`, `sub_agent_tool_call`, `sub_agent_end`. |
| `payload` | Event-specific JSON object |

**`session_start` payload:** `{ task, start_node, resume }` — `task` is the raw task string passed to the session (inline `--task` value or full contents of `--task-file`); `start_node` is the initial graph node; `resume` is true when replaying prior history.

**`turn_end` payload:** `{ input_tokens, output_tokens }` — accumulated across all API calls within the turn.

**`validation_fail` payload:** `{ validator, consecutive }` — name of the blocking validator and how many times in a row it has fired for this agent.

**`hitl_escalation` payload:** `{ message }` — the error message surfaced to the user when the session stalls: with `Selection.Type: graph`, after `Selection.Graph.MaxRetries` (default 4) consecutive failures of any kind; with `keyword`/`statemachine`, after a validator or contract failure reaches its type's `FailureHandling.<Type>.Threshold` (default 3, or 2 for `ConflictingEvidence`). See [Validators — Stuck detection](validators.md#stuck-detection).

**`context_assembly` payload:** `{ knowledge_retrieved, knowledge_included, memory_loaded, memory_included, artifacts, context_chars, system_prompt_chars, assembly_ms, context_strategy, declared_sources, empty_sources }` (sequential-agent turns add `context_chars_breakdown`, `tool_count`, `tool_schema_est_tokens`). `context_strategy` is `"artifact_spec"` when the agent's `Context:` block drove assembly or `"shared_history_fallback"` when it fell back to `ContextWindow`-filtered shared history — the field to alert on if you expect every Reviewer/Tester/Critic-style agent to be running isolated and want to catch one that silently isn't. `declared_sources` lists the `Context:` sources requested (empty under the fallback strategy); `empty_sources` is the subset that resolved to no content at assembly time — e.g. a `brief_field:` naming a field the Planner never wrote — distinguishing "the spec omitted a needed source" (visible by reading the config) from "the spec named a source that was never produced" (only visible at runtime, via this field).

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
| `IncludeReasoning` | bool | `true` | Prepends a `[REASONING EXCERPTS]` block to the compaction summary. Each excerpt is truncated to ~500 tokens so agents resuming after compaction can see the WHY behind prior decisions. Reads `reasoning` events from the session events log (`Events.Path`). Omitted silently when `Events` is not configured or contains no reasoning events. Set to `false` to suppress. |
| `IncludeSymbolGraph` | bool | `true` | Prepends a `[SYMBOL DEPENDENCY GRAPH]` block to the summary (before `[REASONING EXCERPTS]` when both are enabled). Lists every `SymbolDefinition` and `SymbolReference` node in the evidence graph for files written during the session. Omitted silently when no evidence store is wired or no symbol nodes are found. Requires `EvidenceStore` and `ChangeTracking` to be configured. Set to `false` to suppress. |
| `MaxCharsPerHistoryMessage` | int | `8000` | Maximum characters to include from any single message when building the history text passed to the LLM summarizer. Messages that exceed this limit are truncated and annotated with a `[TRUNCATED]` marker; any tool calls recorded for that turn are appended as a compact one-line list so the summarizer still knows what happened. Set to `0` to disable truncation. |
| `AntiThrashMinSavingsRatio` | float | `0.10` | Minimum savings ratio (0–1) a compaction must achieve to count as effective. If the last `AntiThrashWindow` compactions all saved less than this fraction of the conversation, `ShouldCompact` returns `false` until the history grows past the trigger again. Prevents repeated LLM calls that reduce size by less than 10%. Set to `0` to disable. |
| `AntiThrashWindow` | int | `10` | Number of recent compaction outcomes to examine for the anti-thrash guard. The guard only suppresses compaction once this many outcomes have been recorded. Set to `0` to disable. |
| `SummaryTemplate` | string | built-in | Custom Liquid-style template for the LLM summary prompt. Supports `{{$task}}`, `{{$turn_count}}`, `{{$change_log}}`, and `{{$history}}` substitutions. When omitted, the built-in structured template is used — see [Compaction summary template](#compaction-summary-template). |

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

### Compaction summary template

The default summary prompt instructs the LLM to produce four sections that agents can reliably parse after a compaction boundary:

| Section | Content |
|---------|---------|
| `## Completed` | Exact file paths written, shell commands with exit codes, git commits — ground truth derived from the change log. |
| `## Open Questions` | Unresolved ambiguities the agent should address next. `"None."` when none exist. |
| `## Remaining Work` | In-progress and not-yet-started items from the original task. `"None."` when the task is done. |
| `## Key Findings` | Discoveries, constraints, and workarounds surfaced during the session. `"None."` when none exist. |

This structure ensures agents always recover open threads and pending work after a compaction boundary — a common failure mode with free-form summary prompts.

To override with a custom prompt:

```yaml
Compaction:
  TriggerTurnCount: 30
  KeepRecentTurns: 8
  SummaryTemplate: |
    Summarize the following {{$turn_count}} turns for task: {{$task}}

    Change log:
    {{$change_log}}

    History:
    {{$history}}

    Produce: a short narrative paragraph followed by a bullet list of open items.
```

The substitution tokens are:

| Token | Replaced with |
|-------|---------------|
| `{{$task}}` | The session task description |
| `{{$turn_count}}` | Number of turns being compacted |
| `{{$change_log}}` | Contents of `changes.json` (if `ChangeTracking` is configured), otherwise empty |
| `{{$history}}` | Formatted conversation history being compacted |

---

## Skill curation

Automatically authors a reusable `SKILL.md` from each completed session. When enabled, fuseraft makes one LLM call after the session ends to evaluate whether the session produced learnable, portable knowledge, and writes a skill to the configured library path if it did.

Curation is available in both `fuseraft run` sessions (configured in the orchestration YAML) and interactive REPL sessions (configured in `~/.fuseraft/config`).

**`fuseraft run` (YAML):**

```yaml
SkillCuration:
  Enabled: true
  LibraryPath: ~/.fuseraft/skills    # default
  IndexTopN: 5
```

**REPL (`~/.fuseraft/config`):**

```json
{
  "modelId": "claude-sonnet-4-6",
  "skillCuration": {
    "enabled": true
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Enabled` | bool | `false` | Enable post-session skill curation. |
| `LibraryPath` | string | `~/.fuseraft/skills` | Directory where curated skills are written. Each skill gets its own `{slug}/SKILL.md` subdirectory. |
| `Model` | string or object | first agent's model | Model used for the curation LLM call. A faster, cheaper model is recommended (e.g. `gpt-4o-mini`). |
| `MinTurns` | int | `5` | Minimum number of session turns required before curation runs. Short sessions are skipped. |
| `DigestTurns` | int | `30` | Maximum number of recent turns included in the curation prompt. Limits token cost for very long sessions. |
| `IndexPath` | string | `~/.fuseraft/skills/index.db` | Path to the SQLite FTS5 skill index. Updated automatically after each new skill is written. |
| `IndexTopN` | int | `5` | Number of skills injected into the session context at startup (retrieved by full-text search against the task description). `0` disables injection. |
| `LogPath` | string | `~/.fuseraft/skill-curation.jsonl` | Path to the append-only curation log. Every attempt — success or failure — is recorded here. |

**How curation works**

1. After the session completes, the curator reads the last `DigestTurns` turns and the session evidence.
2. It makes one LLM call with a system prompt that instructs the model to output a `<SKILL>…</SKILL>` block in SKILL.md format, or nothing if no portable skill is warranted.
3. If a skill block is detected, the `name:` field from the frontmatter is converted to a slug and the file is written to `{LibraryPath}/{slug}/SKILL.md`.
4. The SQLite FTS5 skill index at `IndexPath` is updated automatically.
5. At the start of the **next** session, fuseraft searches the index with keywords from the task description and injects the top-N matching skills as a context message.

Curation is best-effort: any failure (LLM error, write failure, index error) is logged and swallowed without affecting the session result.

**Curation log**

Every curation attempt appends one JSON line to `~/.fuseraft/skill-curation.jsonl` (override with `LogPath`). Each line records the outcome, session ID, source (`run` or `repl`), slug, model, turn count, and any failure reason:

```jsonl
{"ts":"2026-05-24T10:00:00Z","session":"abc123","source":"repl","outcome":"created","slug":"debug-dotnet-sqlite","path":"/home/user/.fuseraft/skills/debug-dotnet-sqlite/SKILL.md","turns_digested":12,"model":"claude-sonnet-4-6"}
{"ts":"2026-05-24T11:30:00Z","session":"def456","source":"run","outcome":"no_skill","turns_digested":6,"model":"gpt-4o-mini"}
{"ts":"2026-05-24T12:15:00Z","session":"ghi789","source":"repl","outcome":"skipped","failure_reason":"Only 3 assistant turns (min 5)."}
{"ts":"2026-05-24T13:00:00Z","session":"xyz012","source":"run","outcome":"failed","failure_reason":"LLM returned an empty response.","turns_digested":9,"model":"gpt-4o-mini"}
```

Possible `outcome` values:

| Outcome | Meaning |
|---------|---------|
| `created` | A new SKILL.md was written. |
| `updated` | An existing skill was refined in place. |
| `skipped` | Session had fewer turns than `MinTurns` — no LLM call was made. |
| `no_skill` | The LLM reviewed the session and determined no portable skill is warranted. |
| `failed` | An error occurred (empty LLM response, malformed output, write failure). Check `failure_reason`. |

`skill_curation_start` and `skill_curation_complete` events are also emitted to the session event log (`~/.fuseraft/logs/sessions/{project_slug}/{session_id}/events.jsonl` for `fuseraft run`, `.fuseraft/logs/repl_events.jsonl` for REPL) so you can correlate curation with the rest of the session timeline. Use `--verbose` to see debug-level output including the LLM response preview.

**Skill injection at session start (`fuseraft run` only)**

When `IndexTopN > 0` and the index contains skills, fuseraft searches for skills relevant to the current task before the first agent turn. Matching skill bodies are injected as a system context message:

```
## Relevant Skills

### my-skill
…SKILL.md body…
```

This makes accumulated cross-session knowledge available to agents without requiring them to call any skill tools themselves. Injection is not available in REPL sessions because there is no upfront task description to query against.

See [Skills](skills.md) for the full `SKILL.md` format reference and the skill index details.

---

## Routing validators

```yaml
Validation:
  BriefPath: ~/.fuseraft/sessions/{project_slug}/{session_id}/brief.json
  TestReportPath: .fuseraft/artifacts/test-report.json
  TestAssertionPatterns:
    - tester::assert
    - "if .+ throw"
    - \bassert\b
    - \bexpect\b
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `BriefPath` | string | `~/.fuseraft/sessions/{project_slug}/{session_id}/brief.json` | Canonical path for the project brief. Required by `RequireBrief` and `TestReportValid`. |
| `TestReportPath` | string | `.fuseraft/artifacts/test-report.json` | Canonical path for the test report. Required by `TestReportValid`. |
| `ChangeLogPath` | string | `~/.fuseraft/state/{project_slug}/changes.json` | Path to `changes.json` produced by `ChangeTracking` (must match `ChangeTracking.Path`). Enables check 8 in `TestReportValid` and prior-turn file detection in `RequireAllFilesWritten`. |
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

## Output

Controls how `fuseraft run` reports results, for orchestrations that are always invoked non-interactively — CI, cron, event-driven scripts — rather than run by hand.

```yaml
Output:
  Json: true
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Json` | bool | `false` | Same effect as passing `--json` on every invocation: suppresses the banner, turn panels, and spinner; sends all human-readable status to stderr; and prints one JSON summary object to stdout when the session ends. The `--json` CLI flag always takes precedence when passed, so this is purely a default for configs that are always run by scripts. |

See [`fuseraft run` → `--json`](cli-reference.md#fuseraft-run) for the summary object's fields and the stdout/stderr contract, and [Scripting & Automation](scripting.md) for a worked event-driven pipeline example.

**Omit** `Output` entirely for normal interactive rendering; pass `--json` per-invocation instead if only some runs of a config need it.

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
  Path: ~/.fuseraft/state/{project_slug}/evidence.json
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Path` | string | `~/.fuseraft/state/{project_slug}/evidence.json` | File path for the evidence graph JSON. The directory is created automatically. |

**Node types recorded:**

| NodeType | Recorded when |
|----------|--------------|
| `FileWrite` | Agent calls `write_file` or `patch_file` |
| `FileDelete` | Agent calls `delete_file` |
| `CommandRun` | Agent calls `shell_run` (exit code captured) |
| `GitCommit` | Agent calls `git_commit` |
| `TestResult` | Future: emitted by test report plugins |
| `SymbolDefinition` | Agent calls `search_symbol` (auto-populated from results; one node per unique file found) |
| `SymbolReference` | Agent calls `search_callers` (auto-populated from results; `TargetFile` resolved from existing `SymbolDefinition` nodes) |

Nodes are linked by typed edges (`produced_by`, `verified_by`, `depends_on`) so contracts can express causal relationships rather than just presence checks.

Omit `EvidenceStore` if you don't use evidence contracts or lossless compaction. The flat `changes.json` from `ChangeTracking` is sufficient for the built-in validators.

---

## Evidence contracts

Named, composable transition gates that check what must be true on disk before a route or state machine transition fires. Contracts replace or supplement individual validators with reusable, YAML-declared predicates.

```yaml
Contracts:
  - Name: ImplementationComplete
    Requires:
      - CommandSucceeded:
          PatternField: "verify_command"   # reads the verify command from brief.json

  - Name: TestsValid
    Requires:
      - FileExists:
          Path: .fuseraft/artifacts/test-report.json
      - TestReport:
          NoFailures: true
          HasAssertions: true
```

Contracts are referenced by name from keyword route `Contracts` lists or from state machine transition `Contract`/`Contracts` fields.

**Predicate types**

| Type | Fields | Passes when |
|------|--------|-------------|
| `FilesWritten` | `Source`, `Field` | Every path listed in the `Field` array of the `Source` JSON file has been written to disk (current session). |
| `ChecklistComplete` | `Source`, `Field` | Every file-write step in the `Field` array of the `Source` JSON file (heuristic: items containing `/` or a known extension) has been written to disk (current session). Use this alongside `FilesWritten` to catch paths referenced only in `execution_checklist` that were never mirrored into `files_to_change`. |
| `CommandSucceeded` | `Pattern` or `PatternField` | At least one shell command whose text matches any pipe-separated alternative in `Pattern` (literal string) or in the value of the field named by `PatternField` inside `PatternSource` (defaults to brief.json) exited 0 this session. Use `PatternField: "verify_command"` to read the pattern from the brief, making the predicate language-agnostic. `Pattern` and `PatternField` are mutually exclusive. |
| `FileExists` | `Path` | The file at `Path` exists on disk. |
| `TestReport` | `NoFailures`, `HasAssertions` | `test-report.json` exists, has results, and satisfies the declared checks. |
| `RelatedTestsPass` | _(none)_ | Resolves changed files for the session from `ChangeTracking`, discovers related test targets via `TestSelector.FindRelatedCommand`, runs them (falling back to `TestSelector.FullSuiteCommand`), and passes only when the test command exits 0. Requires `TestSelector` and `ChangeTracking` to be configured. |

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
  # Global backstops (apply across all failure types and states):
  MaxConsecutiveContractFailures: 6   # escalate after N contract failures on any transition
  MaxConsecutiveTurnsWithoutSignal: 8 # escalate after N turns with no routing signal emitted
```

The per-type values shown are the defaults — omitting `FailureHandling` entirely produces identical per-type behaviour. The two global backstops default to `0` (disabled) and must be set explicitly.

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

**Global backstops** plug two gaps that per-type thresholds cannot close:

- `MaxConsecutiveContractFailures` — a hard cap across all failure types on a single transition. When any transition accumulates this many consecutive contract failures — regardless of the per-type `Action` — the orchestrator escalates to HITL. This prevents a `Reinstruct` policy from looping indefinitely when a contract cannot be satisfied: the Reinstruct action has no built-in exit condition, so without this cap a broken contract traps the session until `MaxIterations` kills it.

- `MaxConsecutiveTurnsWithoutSignal` — escalates when a state machine agent runs this many consecutive turns without emitting any routing signal. This is the *silent stuck* case: the agent completed its work but never called `handoff()`. Unlike the loop-warning injection (which scans live history and resets after compaction), this counter lives in strategy state and accumulates correctly across compaction boundaries. It resets when the agent emits any valid signal or when a transition succeeds. `0` (default) disables this guard.

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

## Brownfield mode

Enables structured recon-phase support for large existing codebases. When configured, fuseraft-cli seeds the change envelope from the Archaeologist agent's discovery brief and injects a detected convention profile into every agent's system prompt — both at session startup, before any agents run.

```yaml
Brownfield:
  EntryPoints:
    - src/cmd/server/main.go
    - src/internal/billing/charge.go
  DiscoveryBriefPath: ~/.fuseraft/sessions/{project_slug}/{session_id}/brief.brownfield.json
  ConventionProfilePath: ~/.fuseraft/sessions/{project_slug}/{session_id}/conventions.json
  SeedEnvelopeFromBrief: true
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `EntryPoints` | array | `[]` | Files or directories that seed the Archaeologist agent's dependency walk. Referenced in agent instructions; not automatically injected into prompts. Relative paths resolve against the sandbox root. |
| `DiscoveryBriefPath` | string | `~/.fuseraft/sessions/{project_slug}/{session_id}/brief.brownfield.json` | Path where the Archaeologist writes the discovery brief JSON. When `SeedEnvelopeFromBrief` is true and this file exists at startup, its `in_scope_files` list is merged into `Security.ChangeEnvelope`. |
| `ConventionProfilePath` | string | `~/.fuseraft/sessions/{project_slug}/{session_id}/conventions.json` | Path where the Archaeologist writes the convention profile JSON. When this file exists at session startup, its contents are formatted and prepended to every agent's system prompt. |
| `SeedEnvelopeFromBrief` | bool | `true` | When true and `DiscoveryBriefPath` exists, the `in_scope_files` list from the discovery brief is merged into `Security.ChangeEnvelope` at startup. Requires `Security.FileSystemSandboxPath` to be set for enforcement to take effect. |

### Brownfield discovery brief

The Archaeologist agent writes a JSON file to `DiscoveryBriefPath` at the end of the recon phase:

```json
{
  "entry_points": ["src/cmd/server/main.go"],
  "in_scope_files": ["src/internal/billing/charge.go", "src/internal/billing/charge_test.go"],
  "fragility_signals": [
    { "file": "src/internal/billing/charge.go", "reason": "12 TODO markers, high churn" }
  ],
  "test_coverage_gaps": ["src/internal/billing/retry.go"],
  "summary": "Billing module depends on the payment gateway client. No retries currently wired."
}
```

On the next session start (and on all subsequent runs), `in_scope_files` is automatically merged into `Security.ChangeEnvelope` so the Developer cannot write files outside the declared scope.

### Convention profile

The Archaeologist writes a JSON file to `ConventionProfilePath`:

```json
{
  "language": "go",
  "naming_patterns": ["test files match *_test.go", "exported functions are PascalCase"],
  "error_handling": ["wrap errors with fmt.Errorf(\"%w\", err)", "never swallow errors silently"],
  "forbidden_patterns": ["no panic() outside main", "no global mutable state"],
  "test_patterns": ["table-driven tests with testify/require", "sub-tests via t.Run"],
  "structural_notes": ["each package has a README.md", "internal/ packages are not public API"],
  "build_command": "go build ./...",
  "test_command": "go test ./..."
}
```

When this file exists at session startup, the orchestrator formats its contents into a `PROJECT CONVENTIONS` block and appends it to every agent's system prompt. Agents then follow naming rules, error-handling idioms, and forbidden patterns without re-deriving them each session.

### Typical brownfield workflow

1. **First run** — the recon state runs the Archaeologist agent. It surveys the codebase from `EntryPoints`, writes `brief.brownfield.json` and `conventions.json`, then signals `RECON COMPLETE`.
2. **Subsequent runs** — on startup, the orchestrator seeds `Security.ChangeEnvelope` from `in_scope_files` and injects the convention profile into all agent prompts. The recon state skips immediately (the Archaeologist checks that both files exist and calls `RECON COMPLETE` without re-doing the survey).
3. **Write attempts outside the envelope** — blocked by `SandboxEnforcementFilter` with a `[DENIED]` tool result. The Developer must call `REPLAN REQUIRED` to request scope expansion.

A complete runnable example is in `config/examples/brownfield.yaml`. See [Examples → Brownfield pipeline](examples.md#brownfield-pipeline).

---

## Test selector

Exposes the shell command template used by agents to discover the minimal test set for a changed file. Providing this in config — rather than hardcoding it in agent instructions — makes it easy to swap language-specific test runners across projects.

```yaml
TestSelector:
  FindRelatedCommand: "go test -list . $(go list -f '{{.Dir}}' {file}) 2>/dev/null | head -40"
  FullSuiteCommand: "go test ./..."
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `FindRelatedCommand` | string | `""` | Shell command template to discover tests covering a specific changed file. `{file}` is replaced with the file path at runtime. The agent runs this command for each changed file and uses the output to select a targeted test run. |
| `FullSuiteCommand` | string | — | Fallback command run when `FindRelatedCommand` returns no results. When null, the agent falls back to the convention profile's `test_command`. |

**Language examples**

| Language | `FindRelatedCommand` |
|----------|---------------------|
| Go | `go test -list . $(go list -f '{{.Dir}}' {file}) 2>/dev/null \| head -40` |
| Python (pytest) | `pytest --collect-only -q {file} 2>/dev/null \| grep '::' \| head -40` |
| TypeScript (jest) | `jest --listTests --testPathPattern=$(dirname {file}) 2>/dev/null` |
| Rust | `cargo test --list 2>/dev/null \| grep $(basename {file} .rs) \| head -20` |

The `TestSelector` block is used in two ways:

- **Agent instructions** — referenced by Tester and Developer agents so they know which command to run.
- **`RelatedTestsPass` contract predicate** — `TestSelector.FindRelatedCommand` and `FullSuiteCommand` are read automatically by the `RelatedTestsPass` predicate to discover and run targeted tests at handoff time. This is the recommended way to enforce incremental test coverage on brownfield projects without running the full suite on every turn.

For simple "did any test pass" checks without test selection, use `RequireShellPass` with `RequiredCommandPattern` pointing at your test runner.

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
