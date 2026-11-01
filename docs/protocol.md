---
title: "Wire Protocol"
description: "The versioned JSON contract that tools built on fuseraft — the VS Code extension, fuseraft-web, CI scripts — use to drive and query the CLI without linking its code."
---

Tools built on fuseraft talk to it **out of process**: they spawn the CLI or connect to a running `fuseraft serve` daemon, and exchange JSON. They never reference fuseraft's assemblies. This page is that contract. If you build on fuseraft, depend on what's written here, not on internal types or the layout of files under `~/.fuseraft/`, which can change in any release.

There are two parts:

- **[Resource queries](#resource-queries)** — one-shot `--json` flags on the list commands (sessions, memory, context, schedules, plugins, models).
- **[Live sessions](#live-sessions)** — a JSONL event stream for driving a chat (`fuseraft repl --vscode`), an orchestration run (`fuseraft run --bridge`), or a daemon (the `fuseraft serve` Unix socket).

## Versioning

The current protocol version is **1**. Every JSON document fuseraft emits for this protocol carries it as `protocolVersion`: the envelope of every `--json` command, the `ready` event of `repl --vscode` and `run --bridge`, and the `hello` event on the serve socket and WebSocket.

- **Additive changes don't bump the version.** New fields, new event types, new `approval_request` kinds and new commands can arrive in any release. Clients must ignore fields and event types they don't recognize.
- **Breaking changes do.** Removing or renaming a field or event, or changing what one means, increments `protocolVersion`. A client should check it and refuse (or degrade) on a version it wasn't built for.

Field names are camelCase. Timestamps are ISO 8601 strings in UTC. A field documented as nullable is present with `null`, not omitted.

## Resource queries

Each of these commands accepts `--json` and then prints **exactly one line** to stdout. Without `--json`, the human-readable output is unchanged.

```json
{"protocolVersion":1,"items":[ ... ]}
```

On failure the command exits with status `1` and prints an error envelope instead:

```json
{"protocolVersion":1,"error":"fuseraft is not configured. Run 'fuseraft setup' to set an API key."}
```

A command that returns one thing uses `"item":{ ... }` in place of `items`. An empty result is `"items":[]`, never a missing field. Diagnostics may still appear on stderr, so parse stdout only.

| Command | Item type | Notes |
|---------|-----------|-------|
| `fuseraft sessions --json` | [Session](#session) | Incomplete sessions only; add `--all` for completed ones. `--project` filters. `--json` can't be combined with `--prune`, `--cleanup` or `--delete`. |
| `fuseraft sessions --show <id> --json` | [SessionDetail](#sessiondetail) (as `item`) | An unknown or malformed ID is an error envelope. |
| `fuseraft sessions --repl --json` | [ReplSession](#replsession) | REPL chats, newest first. `--project` filters on the chat's directory. |
| `fuseraft memory list --json` | [Memory](#memory) | `--agent <name>` targets an agent's store. |
| `fuseraft memory agents --json` | [MemoryAgent](#memoryagent) | Agents with at least one memory. |
| `fuseraft context list --json` | [ContextItem](#contextitem) | `--dir` picks the project. |
| `fuseraft schedule list --json` | [ScheduledJob](#scheduledjob) | Ordered by next run. |
| `fuseraft plugins --json` | [Plugin](#plugin) | `--plugin <name>` filters; no match is an error envelope. |
| `fuseraft models --json` | [Model](#model) | Queries the configured provider. Never starts the setup wizard; unconfigured is an error envelope. |
| `fuseraft config --list --json` | [ConfigSummary](#configsummary) | `--dir` picks the directory (default `.fuseraft/config`). Files in an `agents/` folder are skipped. |
| `fuseraft validate <path> --json` | [Validation](#validation) (as `item`) | Exits `1` when the config has errors, but still prints the item, not an error envelope. Can't be combined with `--diagram` or `--show-paths`. |
| `fuseraft init --list-templates --json` | [Template](#template) | |
| `fuseraft init [path] --json` | [InitResult](#initresult) (as `item`) | Writes the config, like `--no-interactive`. Run it in the project directory, since it also sets up `.fuseraft/` and `.gitignore` there. An unknown template, or files that exist without `--force`, is an error envelope. |

Commands that only change something report success by exit status: `memory delete`, `context add` / `remove`, `schedule add` / `remove` / `enable` / `disable`, `sessions --delete`.

### Session

An orchestration session (`fuseraft run`).

| Field | Type | Description |
|-------|------|-------------|
| `sessionId` | string | Pass to `fuseraft run --resume <id>`. |
| `task` | string | First line of the task, up to 120 characters. |
| `workingDirectory` | string \| null | Directory the session ran in. Null for very old sessions. |
| `configPath` | string \| null | The orchestration config it ran with. |
| `startedAt`, `lastUpdatedAt` | timestamp | |
| `isComplete` | bool | |
| `turnCount` | int | |

### SessionDetail

A [Session](#session)'s `sessionId`, `task`, `workingDirectory`, `configPath`, `startedAt`, `lastUpdatedAt` and `isComplete`, plus `messages`: an array of [AgentMessage](#agentmessage).

### AgentMessage

One message in an orchestration session. It's used both in [SessionDetail](#sessiondetail) and in `run --bridge`'s `agent_message` event.

| Field | Type | Description |
|-------|------|-------------|
| `agentName` | string | The agent; `Human` for a human's message, `Orchestrator` for an injected correction. |
| `role` | string | `assistant` for an agent's turn, `user` for injected text. |
| `content` | string | |
| `turnIndex` | int | |
| `timestamp` | timestamp | |
| `inputTokens`, `outputTokens` | int \| null | Null when the provider didn't report usage. |
| `isCompactionSummary` | bool | The message is a summary that replaced earlier turns. |
| `toolCalls` | array | `{name, argsSummary, succeeded}` for each tool call in the turn. |

### ReplSession

A REPL chat.

| Field | Type | Description |
|-------|------|-------------|
| `sessionId` | string | Pass to `fuseraft repl --resume <id>`. |
| `modelId` | string | |
| `cwd` | string | Directory the chat runs in. |
| `title` | string \| null | Set with `/title`. |
| `autoTitle` | string \| null | Generated from the first exchange. Show it when `title` is null. |
| `startedAt`, `lastUpdatedAt` | timestamp | |
| `turnIndex` | int | |
| `firstUserMessage` | string | Preview of the first message, up to 500 characters. Empty if there isn't one yet. |

### Memory

| Field | Type | Description |
|-------|------|-------------|
| `guid` | string | |
| `name` | string | The name `fuseraft memory delete` takes. |
| `type` | string | `user`, `feedback`, `project` or `reference`. |
| `description` | string | One-line summary. |
| `body` | string | The memory's content. |

### MemoryAgent

| Field | Type | Description |
|-------|------|-------------|
| `agent` | string | Pass to `fuseraft memory list --agent <agent>`. |
| `count` | int | |

### ContextItem

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | The name `fuseraft context remove` takes. |
| `description` | string \| null | |
| `sourcePath` | string | Where it was imported from. |
| `importedAt` | timestamp | |
| `sizeBytes` | int | Total of all files. |
| `files` | array | `{relativePath, sizeBytes}` for each file. |

### ScheduledJob

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | The name `fuseraft schedule remove` takes. |
| `description` | string \| null | |
| `cron` | string | 5-field cron expression, UTC. |
| `task` | string | |
| `config`, `workDir` | string \| null | |
| `enabled` | bool | |
| `due` | bool | Enabled and its next run time has passed. |
| `createdAt` | timestamp | |
| `lastRun`, `nextRun` | timestamp \| null | |

### ConfigSummary

| Field | Type | Description |
|-------|------|-------------|
| `path` | string | Absolute. |
| `name`, `description` | string \| null | Null when the file didn't load. |
| `agents` | array | Agent names. |
| `selectionType` | string \| null | `Selection.Type`. |
| `error` | string \| null | Why the file didn't load; null when it did. |

### Validation

| Field | Type | Description |
|-------|------|-------------|
| `path` | string | Absolute. |
| `valid` | bool | No issue has level `error`. |
| `issues` | array | `{level, message}`; `level` is `error` or `warning`. |
| `config` | [ConfigSummary](#configsummary) \| null | Null when the file didn't parse. |

### Template

| Field | Type | Description |
|-------|------|-------------|
| `key` | string | Pass to `fuseraft init --template <key>`. |
| `label`, `description` | string | |

### InitResult

| Field | Type | Description |
|-------|------|-------------|
| `template`, `model` | string | |
| `endpoint` | string \| null | |
| `configPath` | string | Absolute path of the config written. |
| `agentFiles` | array | Absolute paths of the agent files written alongside it. |

### Plugin

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | |
| `source` | string | `builtin` or `mcp`. |
| `functions` | array | `{name, description}` for each tool the plugin provides. |

### Model

| Field | Type | Description |
|-------|------|-------------|
| `id` | string | Pass to `fuseraft repl --model <id>`. |
| `current` | bool | The configured default. |

## Live sessions

A live session is a stream of JSONL: one JSON object per line, each with a `type`. There are two transports. Both use the same event shapes and the same input lines.

| Transport | How to connect | First event |
|-----------|----------------|-------------|
| **stdio** | Spawn `fuseraft repl --vscode --no-banner` with piped stdin/stdout. One process is one chat. | `ready` |
| **run bridge** | Spawn `fuseraft run --bridge -c <config> "<task>"` (or `--resume <id>`) with piped stdin/stdout. One process is one orchestration session, and it exits when the session ends. | `ready` |
| **serve socket** | Connect to the Unix socket of a running `fuseraft serve` (the path `fuseraft attach` uses). Many clients can attach to one daemon. | `hello` |
| **serve WebSocket** | Open a WebSocket to `ws://<host>:<port>/attach` (`wss://` when the daemon has `--tls-cert`) on a running `fuseraft serve` (the MCP port). Same events and inputs as the serve socket; each text message is one line. Send `Authorization: Bearer <token>` when the daemon has `FUSERAFT_SERVE_TOKEN` set. A handshake with an `Origin` header is refused. Send `Fuseraft-Client-Id: <id>` (1–64 of `A-Z a-z 0-9 - _`, the same every time) to keep your tasks across reconnects; see `hello`. | `hello` |

Ignore any stdout line that doesn't parse as JSON. Slash-command output can occasionally leak through as plain text.

### Handshake events

| Event | Fields | |
|-------|--------|---|
| `ready` | `protocolVersion`, `sessionId`, `model`, `title`, `autoTitle`, `midTurnCommands[]` | `repl --vscode`. The chat is ready for input. `midTurnCommands` lists the slash commands that may be sent while a reply is still streaming. |
| `ready` | `protocolVersion`, `sessionId`, `task`, `config`, `resumed`, `agents[]` | `run --bridge`. The session is about to start. |
| `hello` | `protocolVersion`, `clientId`, `readOnly`, `inFlight`, `pendingResults[]` | Serve socket and WebSocket. Sent as soon as you connect. `clientId` echoes your `Fuseraft-Client-Id` (null without one). `readOnly` is true when your bearer token is a `read` token: the daemon answers any `user_input` with an `error` event instead of queuing it. `inFlight` is your task that's still queued or running (`{sessionId, task, status, position}`), or null; its pending approval, if any, is asked again right after. `pendingResults` lists the session IDs of your tasks that finished while you were away; their `result` events follow the `hello`. |

### Input (client → fuseraft)

| Line | Fields | |
|------|--------|---|
| `user_input` | `text`, `images?[]` (`{name, data}` with `data` a base64 data URL) | A message, or a slash command. On the serve socket, each message is dispatched as a new task; a plain text line works too, and images aren't supported there. |
| `approval_response` | `approved` (bool) | Answers the pending `approval_request`. Anything malformed counts as **denied**. |
| `question_response` | `answer` (string, or `null` to skip) | Answers the pending `question`. |
| `interrupt` | — | Cancels the running turn. Use it on Windows; elsewhere, sending `SIGINT` to the process does the same. |

There are no request IDs. A response is simply the next matching line on stdin, so send exactly one response per `approval_request` or `question`.

### Turn events (stdio)

| Event | Fields | |
|-------|--------|---|
| `turn_start` | `text` | A turn began; `text` is a message carried over from mid-turn input, if any. |
| `token` | `text` | A streamed chunk of the reply. |
| `thinking` | `text` | Model reasoning, when the provider exposes it. |
| `tool_call` | `id`, `name`, `summary`, `args?`, `agent?` | `agent` is set when a subagent made the call. |
| `tool_result` | `id`, `name`, `ok`, `output` | `output` is capped at 100,000 characters. |
| `file_changed` | `path`, `sigil`, `added`, `removed` | One file changed, with line counts. |
| `file_changes` | `changes[]` (`{sigil, path}`) | Every file the turn changed, sent at the end. |
| `message_end` | `turnIndex`, `toolCalls[]`, `inputTokens?`, `outputTokens?`, `cachedInputTokens?`, `cumulativeCacheReadTokens?` | The turn finished. |
| `cancelled` | — | The turn was interrupted. |
| `retrying` | `attempt`, `max` | The stream dropped and is being retried. |
| `interjection` | `status` (`joined`, `carried`, `dropped`), `count` | What happened to messages sent while a reply was streaming. |
| `status` | `model`, `turn`, `context` (0–1), `level`, `modes[]`, `input`, `output`, `cached` | Status-line numbers. |
| `compacted` | — | History was replaced with a summary. |
| `plan` | `steps[]` | |
| `step_status` | `step`, `total`, `status`, `stepsLeft` | |
| `shell_start` / `shell_output` / `shell_end` | `command` / `text` / — | A `!command` shell escape. |
| `replay` | `total`, `compacted`, `turns[]` (`{user, assistant, toolCalls[]}`) | Earlier turns, re-sent after a resume. |
| `title` | `sessionId`, `title`, `autoTitle` | The chat's name changed. |
| `applied` | `command`, `message` | A mid-turn command took effect. |
| `copy` | `text` | Text the user asked to copy to the clipboard. |
| `text` / `info` / `warning` / `error` | `text` | Messages for the user. `text` is pre-rendered Markdown. |
| `session_end` | — | The process is about to exit. |

### Run events (run bridge)

| Event | Fields | |
|-------|--------|---|
| `agent_starting` | `agent` | |
| `tool_calling` | `agent`, `tool`, `argsSummary` | |
| `token_budget_warning` | `agent`, `inputTokens`, `warnThreshold` | |
| `agent_message` | `message` ([AgentMessage](#agentmessage)), `elapsedMs` | One completed turn. Messages with `agentName: "Orchestrator"` are routing corrections; most UIs hide them. |
| `result` | `sessionId`, `task`, `config`, `succeeded`, `errorMessage`, `exitCode`, `turns`, `elapsedSeconds`, `inputTokens`, `outputTokens` | The last event. The process exits with `exitCode`. A run that fails before it starts sends only `result`, with `sessionId` null. |

Input is `approval_response` and `interrupt`; `user_input` isn't read. An interrupt (or `SIGINT`) cancels the session, which then sends `result` with `succeeded: false`. A cancelled session can be continued with `run --bridge --resume <id>`.

`--bridge` gates shell commands, file writes and other mutating tool actions with `approval_request`, the same way `fuseraft serve` does. It never pauses after each turn, so it can't be combined with `--hitl`.

### Prompts

| Event | Fields | Answer with |
|-------|--------|-------------|
| `approval_request` | `kind` plus fields per kind (below) | `approval_response` |
| `question` | `question`, `options[]`, `allowOther` | `question_response` |

`approval_request` kinds:

| `kind` | Fields |
|--------|--------|
| `shell_command` | `command` |
| `tool_action` | `plugin`, `action`, `detail` |
| `file_write` | `plugin`, `action`, `path`, `oldContent`, `newContent`. Render it as a diff. |
| `route_approval` | `keyword`, `sourceAgent`, `targetAgent` (serve and run bridge: an agent handoff that needs a human) |

New kinds may be added. If a client doesn't recognize a kind, it should show the raw event and ask, or deny it. It must never auto-approve.

On stdio, `question` is only sent when the client spawned the CLI with `FUSERAFT_VSCODE_QUESTIONS=1`. Without that, the agent isn't offered the `ask_user` tool, so a client that can't answer questions never stalls on one.

### Daemon events (serve socket)

| Event | Fields | |
|-------|--------|---|
| `queued` | `sessionId`, `position` | Your task was accepted; `position` is how many are ahead of it. |
| `auto_dispatched` | `sessionId`, `objectiveId`, `task` | The daemon started a task on its own (`--auto-objective`). |
| `agent_starting` | `agent` | |
| `tool_calling` | `agent`, `tool`, `argsSummary` | |
| `token_budget_warning` | `agent`, `inputTokens`, `warnThreshold` | |
| `a2a_call` | `agent`, `sessionId` | An A2A caller started a call to a hosted agent (`--a2a`). |
| `task_resumed` | `sessionId`, `task` | The daemon restarted while this task ran, and is resuming it from its saved session. Its approvals, and its `result`, still go to the client that sent it. |
| `result` | `sessionId`, `task`, `succeeded`, `errorMessage`, `lastAgent`, `lastMessage` | Your task finished. |

Progress events are broadcast to every attached client. Approval prompts go only to the client that dispatched the task. With a client id, that client is every connection with the id: when a connection drops mid-approval, the daemon waits `--reconnect-grace` for the next one and asks it instead. A newer connection with the same id closes the older one. Client ids are scoped to the bearer token the connection authenticated with (see `--tokens-file`), so the same id under another token is another client. A daemon restart keeps clients' unfinished tasks and uncollected results, so a client reconnecting afterwards gets them in its `hello` as usual.

The daemon also exposes `dispatch_task`, `get_status` and `get_result` as MCP tools over HTTP, and can host an A2A endpoint. See [Serve](serve.md).
