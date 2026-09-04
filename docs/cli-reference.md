# CLI Reference

## `fuseraft run`

Run a task against the orchestration team.

```
fuseraft run [task] [options]
```

**Arguments**

| Argument | Description |
|----------|-------------|
| `[task]` | Task description. If omitted, you are prompted interactively. |

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `-c, --config <path>` | `.fuseraft/config/orchestration.yaml` | Path to the orchestration config file. YAML (`.yaml` / `.yml`) and JSON (`.json`) are both accepted. |
| `-f, --task-file <path>` | — | Read the task from a plain-text file instead of the command line. Useful for long or multi-line tasks. Ignored when resuming. |
| `-r, --resume [sessionId]` | — | Resume an incomplete session. Omit the ID to choose from a list. |
| `--hitl` | off | Human-in-the-loop mode. Pauses after every agent turn; you can inject a message, press Enter to continue, or type `q` to quit. |
| `-o, --output <path>` | — | Write the full session transcript to a Markdown file. |
| `--verbose` | off | Enable debug logging, including token counts per turn. |
| `--tools` | off | Show tool calls made by each agent inline in the turn panel. |
| `--no-banner` | off | Skip the ASCII banner. Useful for CI or piped output. |
| `--ci` | off | CI mode. After the session ends, reads `.fuseraft/artifacts/test-report.json` and exits with code `2` if any criterion has `status: FAIL`. Exits `0` if the report is absent or all criteria pass. |
| `--devui` | off | Start a local web server and print a URL for real-time session visualization. See [DevUI](#devui) below. |
| `--work-dir <path>` | — | Set the working directory for the session. Priority: flag > `Security.FileSystemSandboxPath` in the config > current directory. |
| `--context-file <path>` | — | Attach a file as context. Its content is appended to the task. PDF, DOCX, PPTX, and XLSX files are extracted to plain text automatically; other files are read as UTF-8. Repeatable — specify once per file. Ignored when resuming. |
| `--spec <path>` | — | Path to a spec file (Markdown, plain text, or JSON) that anchors all agents to an agreed specification. The spec is injected into every agent's system prompt as the authoritative source of truth and appended to the task at turn 0. Ignored when resuming. See [Spec-Driven Development](spec-driven.md). |
| `--snapshot` | off | Capture per-turn postmortem snapshots to `~/.fuseraft/snapshots/<project>/<session>/`. Writes `turns.jsonl` (one record per agent turn: content, tool calls, token usage) and `manifest.json` (run summary: task, success/failure, elapsed). Useful for debugging and postmortem analysis. |
| `--json` | off | Suppress the banner, turn panels, and spinner; send all human-readable status to stderr; print one JSON summary object to stdout when the session ends. For scripted/automated invocations. Same effect as `Output.Json: true` in the config (this flag always wins). See [`--json` output](#-json-output) below. |
| `--vscode` | off | VS Code mode. Reads the API key from the `FUSERAFT_API_KEY` environment variable (injected by the fuseraft VS Code extension) instead of the OS keychain. Automatically passed by the extension — not intended for manual use. |

**Examples**

```bash
# Run with default config and inline task
fuseraft run "Add pagination to the user list endpoint"

# Read the task from a file (useful for long or multi-line tasks)
fuseraft run -f task.md
fuseraft run -f task.md -c my-team.json -o transcript.md

# Use a custom config — JSON or YAML both work
fuseraft run -c configs/research-team.json "Summarise recent papers on diffusion models"
fuseraft run -c configs/my-team.yaml "Refactor the auth module"

# Resume a specific session
fuseraft run --resume a3f92c1d

# Resume interactively (shows a selection list)
fuseraft run --resume

# Human-in-the-loop: review each turn before it continues
fuseraft run --hitl "Refactor the payment module"

# Show tool calls inline in the turn panel
fuseraft run --tools "Refactor the auth module"
fuseraft run --tools --hitl "Build a REST API in Go with JWT auth"

# Open a real-time visualization in the browser while the session runs
fuseraft run --devui "Build a REST API in Go with JWT auth"
fuseraft run --devui -c my-team.yaml "Migrate the database schema"

# Save a transcript
fuseraft run -o transcript.md "Build a REST API"

# Set the working directory explicitly (useful when the config has no sandbox path)
fuseraft run --work-dir ~/github/fuseraft/kiwi -c kiwi-dev.yaml "Add a string interpolation function to lib/string.kiwi"

# Work-dir is also inferred automatically from Security.FileSystemSandboxPath in the config
fuseraft run -c kiwi-dev.yaml "Add a string interpolation function to lib/string.kiwi"

# Attach a source file as context — content is appended to the task
fuseraft run --context-file src/Button.tsx "Fix the button accessibility issues"

# Attach multiple files (repeat the flag once per file)
fuseraft run --context-file schema.sql --context-file openapi.yaml "Add a /users endpoint"

# Binary documents are extracted to plain text automatically
fuseraft run --context-file requirements.pdf "Implement the auth flow described in the requirements"
fuseraft run --context-file design.docx --context-file data-model.xlsx "Generate the API layer"

# Spec-driven development — spec anchors every agent and drives the Planner brief
fuseraft run --spec spec.md
fuseraft run --spec spec.md "Add authentication to the API"
fuseraft run --spec spec.json -c swe.yaml

# Capture postmortem snapshots for debugging or failure analysis
fuseraft run --snapshot "Refactor the auth module"
fuseraft run --snapshot -c my-team.yaml "Add integration tests"

# Scripted invocation — stdout is one JSON summary line, everything else is on stderr
fuseraft run -c pipeline.yaml -f task.md --json --ci --no-banner
```

**Task input priority**

When multiple task inputs are provided, the following order applies:

1. **Session checkpoint** — when resuming, the original task is always used; `[task]`, `--task-file`, `--context-file`, and `--spec` are ignored with a warning
2. **`--task-file`** — if supplied, the file contents are used as the task
3. **`[task]`** — the positional argument
4. **Interactive prompt** — if nothing is supplied, you are asked to type a task
5. **`--spec` default** — if `--spec` is provided with no task, the task defaults to `"Implement the specification."` instead of prompting
6. **Built-in demo** — if the prompt is left blank and no spec is set, a default demo task runs

The task file is read as plain UTF-8 text. Leading and trailing whitespace is trimmed. The file can contain any content — Markdown, plain prose, bullet lists, structured specs.

`--context-file` is a modifier on top of whatever task source is used: after the task text is resolved, each context file's content is appended as a fenced code block under an `--- Attached files:` section. PDF, DOCX, PPTX, and XLSX files are automatically extracted to plain text; all other files are appended as UTF-8. Files that cannot be found or read emit a warning and are skipped without aborting the run.

`--spec` differs from `--context-file` in two ways: (1) the spec is injected into every agent's system prompt — not just the task message — so it remains visible after context compaction, and (2) the spec is framed as the single authoritative source of truth that `brief.json` must derive from. Use `--spec` to run a [spec-driven development](spec-driven.md) workflow; use `--context-file` for supplementary reference material.

### Human-in-the-loop controls

There are two ways human approval can pause a session:

**1. `--hitl` mode — after every agent turn**

When `--hitl` is active, after each agent turn you see a prompt:

```
  ↩ Enter to continue  ·  type a message to redirect  ·  q to stop:
```

- **Enter** — continue to the next agent turn
- **Any text** — inject that text as a user message into the conversation, then restart the stream
- **q** — save the checkpoint and exit cleanly

After the termination condition fires (e.g. `TASK_COMPLETE`), the session does not exit silently. A distinct post-session prompt appears:

```
  Session complete.
  Type a follow-up message to continue  ·  press Enter to exit:
```

- **Enter** — end the session cleanly
- **Any text** — inject a follow-up message and continue the session (the termination condition resets for the next run of the orchestrator)

This lets you keep interacting with the agent after a task completes without needing to restart or resume.

**Shell command approval in `--hitl` mode**

When `--hitl` is active, every `shell_run` and `shell_run_script` call pauses for approval before executing:

```
⏸ Shell command requested:
  python google_search.py "test"
Allow? (y/N):
```

- **y / yes** — the command runs normally
- **Enter / anything else** — the command is blocked; the agent receives `[DENIED]` and can try an alternative or ask you what to do

Shell command approval only applies in `--hitl` mode. In normal runs, shell commands execute without prompting.

**2. Per-route approval gates — before a specific route fires**

Individual routes can require explicit approval by setting `RequireHumanApproval: true` in the route config. This works independently of `--hitl` — approval gates fire even in normal (non-HITL) mode.

```yaml
- Keyword: "HANDOFF TO REVIEWER"
  Agent: Reviewer
  Validator: TestReportValid
  RequireHumanApproval: true
```

When the keyword fires and all validators pass, the session pauses:

```
⏸ Route approval required.
  From:    Tester
  To:      Reviewer
  Keyword: HANDOFF TO REVIEWER

Approve? (y/N):
```

- **y / yes** — the route fires and the target agent is invoked
- **Enter / anything else** — the route is blocked; a "route blocked by operator" message is injected and the source agent is re-invoked so it can continue working or await further instructions

**Stuck-agent escalation**

If an agent fails the same validator enough consecutive times — the configured `FailureHandling` threshold for `keyword`/`statemachine` sessions (default 3, or `Selection.Graph.MaxRetries` for `graph` sessions, default 4) — the session pauses regardless of `--hitl` mode:

```
⚠ HITL intervention required.
  Agent:    Developer
  Blocked:  RequireWriteFile (3 consecutive failures)
  Last error: ...

Redirect Developer (Enter to abort session):
```

- **Any text** — inject a redirect message and restart the stream
- **Enter** — abort the session (checkpoint is saved for `--resume`)

### `--json` output

For scripts and event-driven invocations that need a structured result instead of parsing the transcript or console output. Enable it per-invocation with `--json`, or set `Output.Json: true` in the config to make it the default for every run of that orchestration (see [Output](configuration.md#output)) — the CLI flag always takes precedence. See [Scripting & Automation](scripting.md) for a full walkthrough with wrapper-script examples.

**Stream contract:** stdout carries *only* the final JSON summary — no banner, no turn panels, no spinner. Every human-readable status line, including setup diagnostics (bad config, missing work dir, spec file not found) is written to stderr instead, regardless of whether JSON mode is even active — this makes `stdout` safe to pipe straight into `jq` or `json.loads()` in every case, never just the happy path.

**Early failures** (before a session starts — bad `--work-dir`, unresolvable `--resume`, missing `--spec` file, or the config itself failing to load) also emit a JSON summary (`succeeded: false`, `error_message` set) whenever `--json` was passed, since the flag makes JSON mode known from the first line of the command. The one case that can't produce a JSON summary: JSON mode enabled *only* via `Output.Json` (not `--json`), failing *before* the config finishes loading — `Output.Json` genuinely cannot be read from a config that hasn't loaded yet. That case still guarantees stdout stays completely empty (never wrong, never mixed with plain text); the failure is reported via exit code and a stderr message only. Scripts should treat "stdout didn't parse as JSON" as its own failure case, not just check the JSON body — pass `--json` explicitly if you want a JSON summary for every outcome, not just successful ones.

**Example:**

```bash
$ fuseraft run -c pipeline.yaml -f task.md --json --ci --no-banner
{"session_id":"a3f92c1d","task":"...","config":"/abs/path/pipeline.yaml","succeeded":true,"error_message":null,"exit_code":0,"turns":4,"elapsed_seconds":38.12,"tokens":{"input":41203,"output":1877},"transcript_path":null,"ci":{"passed":true,"skipped":false,"failed_criteria":[]}}
```

**Summary fields** (snake_case, matching the rest of fuseraft's JSON output — event log, `--snapshot` manifest):

| Field | Type | Description |
|-------|------|-------------|
| `session_id` | string | The session's ID — pass to `--resume` if the caller wants to continue it later. |
| `task` | string | The resolved task text (after `--task-file` / `--context-file` / `--spec` expansion). |
| `config` | string | Absolute path to the config file used. |
| `succeeded` | bool | Whether the orchestration session itself completed successfully. |
| `error_message` | string \| null | Set when `succeeded` is `false`. |
| `exit_code` | int | The process's own exit code (`0`, `1`, or `2` — same meaning as [`--ci`](#fuseraft-run) and the non-JSON path). Redundant with the shell's `$?`, included so the summary is self-contained when captured by a caller that only sees stdout. |
| `turns` | int | Number of assistant turns in the session. |
| `elapsed_seconds` | number | Wall-clock duration of the session. |
| `tokens.input` / `tokens.output` | int | Summed input/output tokens across all turns. |
| `transcript_path` | string \| null | Set when `-o/--output` was also passed. |
| `ci` | object \| null | Present only when `--ci` was passed and the session succeeded. `passed` (bool), `skipped` (bool, true if `test-report.json` was absent or unparseable), `failed_criteria` (array of criterion names with `status: FAIL`). |

### DevUI

`--devui` starts a lightweight ASP.NET Core server on a randomly assigned port and prints the URL before the session begins:

```
DevUI → http://localhost:54321
```

Open the URL in any browser. The page connects via Server-Sent Events and shows agent turns in real time as the session runs.

**What the page shows**

- Session ID and task in the header
- A card per agent turn, with agent name (consistently colour-coded), turn number, elapsed time, and token usage (input / output)
- Fenced code blocks (` ``` `) rendered as formatted `<pre>` elements
- A spinning "thinking" indicator when an agent is selected but has not yet responded
- Status chip in the header: **live** while running, **✓ complete** or **✗ failed** when the session ends

**Replay on refresh**

Refreshing the page replays the complete event history — all cards from the start of the session are reconstructed instantly. This means you can open the browser after the session has already started (or even after it finishes) and still see everything.

**Combining with other flags**

`--devui` is independent of `--hitl`, `--verbose`, and `--ci` and can be used alongside any of them:

```bash
fuseraft run --devui --hitl "Refactor the auth module"
fuseraft run --devui --ci -c my-team.yaml "Add integration tests"
```

---

## `fuseraft repl`

Start an interactive chat session with a single model. No config file needed. Includes built-in tools for filesystem access, shell execution, code search, git, and HTTP.

Running `fuseraft` with no subcommand is equivalent to `fuseraft repl`.

The assistant identifies itself as the fuseraft assistant and knows which model it is running on, so asking "who are you?" or "what model are you?" will give an accurate answer.

```
fuseraft [options]
fuseraft repl [options]
```

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `-m, --model <id>` | see below | Model ID to use (e.g. `gpt-4o`, `claude-sonnet-4-6`). Overrides `~/.fuseraft/config` when set. |
| `--save` | off | Persist `--model` as the new default in `~/.fuseraft/config`. No effect without `--model`. |
| `-s, --system <prompt>` | — | System prompt. Defaults to a coding/research prompt when tools are enabled. |
| `--resume <id>` | — | Resume a previous REPL session by its session ID. Use `/sessions` inside the REPL to list resumable sessions. |
| `--no-banner` | off | Skip the ASCII banner. |
| `--no-tools` | off | Disable all built-in tools and start a plain chat session. |
| `--verbose` | off | Enable debug logging: prints per-turn detail (token estimate, tool-round count, total tool calls) and shows the event log path at startup. |
| `--vscode` | off | VS Code mode. When stdin is also redirected (the process is spawned by the fuseraft VS Code extension's REPL panel), switches to JSON bridge mode: all output is emitted as JSONL events to stdout and input is read as JSONL from stdin. In this mode the ASCII banner, ANSI prompts, spinner, and status lines are suppressed; the API key is read from `FUSERAFT_API_KEY` instead of the OS keychain. Automatically passed by the extension — not intended for manual use. |

**Startup display**

On launch a compact header shows the model name, a single info line listing active tool categories, loaded context (agents/memory/skills), and available sub-agent commands, and the session ID:

```
── claude-sonnet-4-6 ─────────────────────────────────────
  FileSystem  Shell  Search  Git  ·  memory  ·  3 skills  ·  /help
  session: a87569bcd7b0
```

The session ID is shown on every startup so you can note it down for later resumption with `--resume`. The event log path is only shown with `--verbose`.

> **VS Code webview panel** — When the fuseraft VS Code extension opens a REPL panel it spawns the CLI with `--vscode --no-banner` and piped stdin/stdout. The CLI detects the redirected stdin and switches to JSON bridge mode automatically. In this mode the startup header and all ANSI output are suppressed; the session communicates over a JSONL protocol instead:
>
> | Direction | Event | Payload fields |
> |-----------|-------|----------------|
> | CLI → VS Code | `ready` | `sessionId`, `model` |
> | CLI → VS Code | `token` | `text` (streaming chunk) |
> | CLI → VS Code | `tool_call` | `name` |
> | CLI → VS Code | `message_end` | `turnIndex`, `toolCalls[]` |
> | CLI → VS Code | `cancelled` | — |
> | CLI → VS Code | `error` | `text` |
> | CLI → VS Code | `plan` | `steps[]` |
> | CLI → VS Code | `step_status` | `step`, `total`, `status`, `stepsLeft` |
> | CLI → VS Code | `session_end` | — |
> | VS Code → CLI | `user_input` | `text` |
>
> Non-JSON lines emitted by the CLI (e.g. from slash-command output) are silently ignored by the extension.

**First-time setup**

If `~/.fuseraft/config` is missing or incomplete, `fuseraft repl` runs an interactive setup wizard before starting the session. It prompts for a provider URL and API key (leave the key blank for Ollama), tests the endpoint's model listing (`GET {endpoint}/models`, falling back to Ollama's `GET {endpoint}/api/tags`), and lets you pick a model from the live results — falling back to a free-typed model ID if neither endpoint responds. Settings are saved after the first successful reply — the config file stores model, endpoint, and provider only; the API key goes into the OS keychain. Use `/provider setup` to reconfigure at any time.

**Custom and enterprise providers** — the wizard accepts any OpenAI-compatible endpoint. Supply the full base URL (e.g. `https://chat.mycompany.com/openai/`); if the endpoint exposes a models listing you can pick from the live results, otherwise type the model ID manually, including non-standard formats such as AWS Bedrock deployment IDs (`anthropic.claude-sonnet-4-6-20250929-v1:0`). When both a custom endpoint and an API key are provided, auto-detection is skipped entirely and the endpoint is treated as OpenAI-compatible.

See [Getting Started — Set your API key](getting-started.md#set-your-api-key) and [Security — API key storage](security.md#api-key-storage) for more detail.

**Model resolution order**

1. `--model` flag (if passed)
2. `modelId` in `~/.fuseraft/config` (if the config is complete)
3. First provider with an API key in the environment (fallback auto-detection):

| Environment variable | Default model |
|---------------------|---------------|
| `ANTHROPIC_API_KEY` | `claude-sonnet-4-6` |
| `OPENAI_API_KEY` | `gpt-4o-mini` |
| `XAI_API_KEY` | `grok-4.3` |
| `GOOGLE_AI_API_KEY` | `gemini-2.0-flash` |
| `MISTRAL_API_KEY` | `mistral-small-latest` |
| `DEEPSEEK_API_KEY` | `deepseek-chat` |

`--model` alone only overrides the model for that session. Add `--save` to also write it to `~/.fuseraft/config` as the new default (e.g. `fuseraft repl --model claude-sonnet-4-6 --save`).

**Built-in tools**

Unless `--no-tools` is passed, the REPL gives the model access to a curated core set — the
common, low-risk operations that cover a typical session (read, edit, search, status, commit):

| Plugin | Tools |
|--------|-------|
| FileSystem | `read_file`, `write_file`, `patch_file`, `list_files`, `grep_file`, `get_file_info`, `create_directory` |
| Shell | `shell_run`, `shell_run_script`, `shell_get_env`, `shell_set_env`, `shell_which`, `shell_get_working_directory` |
| Search | `search_content`, `search_symbol`, `search_callers` |
| Git | `git_status`, `git_diff`, `git_log`, `git_show`, `git_branch_list`, `git_add`, `git_commit`, `git_stash_list` |
| Todo | `todo_write`, `todo_read` — self-directed checklist the model uses to plan and track multi-step work within the session (in-memory only, not persisted). |
| SubAgent | `sub_agent_explore`, `sub_agent_locate` — the same tools behind `/explore` and `/locate` (see below), now also callable by the model directly mid-turn. Built from the full, unfiltered FileSystem/Shell/Git read tools regardless of whether `Extended` is enabled. |
| Session | `repl_session_current`, `repl_session_list`, `repl_session_read_event_log`, `repl_session_read_log`, `compact_context`, `get_context_status` |
| Skills | `load_skill`, `run_skill_script` (only when skills are installed — see [Skills](skills.md)) |

**Optional plugins** — not loaded by default; pass `--plugins <name>,<name>` (comma-separated) to enable them. Kept opt-in because every registered tool adds its schema to every request — a smaller default tool surface means smaller, faster requests and less chance of tripping a provider's tool-schema limits.

| Plugin | Tools |
|--------|-------|
| `Extended` | The rarer/destructive half of FileSystem, Shell, and Git: `delete_file`, `delete_directory`, `copy_file`, `move_file`, `set_permissions`, `get_file_summary`, `save_file_summary`, `list_directory`; `shell_get_session_temp_dir`, `shell_run_background`, `shell_get_job_status`, `shell_get_job_output`, `shell_kill_job`; `git_checkout`, `git_create_branch`, `git_init`, `git_is_inside_work_tree`, `git_is_repo_root`, `git_push`, `git_pull`, `git_stash`, `git_stash_pop`, `git_reset`, `git_rebase`. |
| `Http` | `http_get`, `http_post`, ... |
| `Changes` | `changes_read`, `changes_read_latest` |
| `Chatroom` | `chatroom_send`, `chatroom_read` |
| `SessionContext` | `session_context_read`, `session_context_write` |
| `Scratchpad` | `scratchpad_write`, `scratchpad_read`, `scratchpad_read_all`, `scratchpad_search`, `scratchpad_delete` |

**Forced evidence collection** — when a message looks like an identify/locate/find-style question ("locate X", "where is Y", "which file...", "does Z exist"), the REPL forces at least one tool call before the model may answer, instead of letting it answer from memory. This applies only to that one turn; it does not affect unrelated questions.

When the model invokes tools, the spinner label updates live to show the accumulating chain:

```
⠋ conjuring…  read_file → grep_file → write_file
```

Once the model begins streaming its response, the spinner clears and a compact summary of all tools called this turn is printed before the reply:

```
  ⚙  read_file → grep_file → write_file
assistant:
…
```

Use `/tools` to see the full list at runtime.

**Slash commands**

| Command | Description |
|---------|-------------|
| `/help` | Show all slash commands |
| `/sessions` | List resumable REPL sessions with their IDs, model, turn count, and age. Resume with `fuseraft repl --resume <id>`. |
| `/fork` | Snapshot the current session to a new ID. The snapshot is saved immediately; the current session continues unchanged. Use `fuseraft repl --resume <id>` to open the fork later. |
| `/fork switch` | Fork and immediately become the fork. The original session is already checkpointed on disk; the live session continues under the new ID. |
| `/switch <id>` | Save the current session and load another saved session in its place. History, turn counter, model (if different), and plan state are all restored. Use `/sessions` to find IDs. |
| `/conversation` | List all turns in memory with 1-based turn numbers and a one-line preview of each user message and assistant response. Use this to find the right turn number before running `/rewind`. |
| `/rewind <n>` | Keep turns 1…n and discard all later turns. Turn count is the number of User messages currently in memory. Clamps safely — passing a number larger than the current turn count is a no-op. |
| `/rewind -<n>` | Step back n turns from the current position (relative rewind). `/rewind -1` drops the last turn; `/rewind -99` clamps to 0 and clears all turns. |
| `/clear` | Clear conversation history (system prompt is kept). Also clears the terminal and redraws the startup banner, unless `--no-banner` was passed at launch (in which case it just prints a confirmation line). |
| `/compact` | Ask the model to summarise the session into a handoff document, then replace history with that summary. The system prompt and tools/skills catalog are kept; everything else is discarded. Facts the assistant stated without a backing tool call are tombstoned as `[UNVERIFIED ASSUMPTION: ...]` rather than carried forward as established facts. Use this when context is filling up but you want to continue in the same session. |
| `/compact <focus>` | Same as `/compact`, but passes a focus hint to the model so the summary is tailored toward the next task (e.g. `/compact fix the auth bug next`) |
| `/history` | Show a condensed view of the conversation (role + preview of each message) |
| `/system` | Print the current system prompt |
| `/system <prompt>` | Replace the system prompt for the rest of the session |
| `/tools` | List active tools grouped by category, with enabled/disabled status |
| `/tools disable <category>` | Disable a tool category for the rest of the session (`FileSystem`, `Shell`, `Search`, `Git`, `Http`, `Skills`) |
| `/tools enable <category>` | Re-enable a previously disabled tool category |
| `/plan <task>` | Ask the model to produce a structured JSON plan (no tool calls). Each step has a description, an expected tool name, and an optional expected artifact path. |
| `/plan` | Show the currently stored plan |
| `/execute` | Run each plan step as a separate turn. After each step the REPL verifies postconditions (tool called, artifact created) and halts with a warning if a step fails. |
| `/resume` | Retry the halted step and continue the remaining steps as-is. Use this after manually fixing the issue. |
| `/recover` | Inject a failure context hint into the step prompt and retry from the halted step. The agent is told which tool was expected, which tools were actually called, and why the step failed — giving it a better chance of self-correcting. |
| `/assist` | Diagnose a stalled or broken conversation. A sub-agent reads the history, identifies the root cause, and injects a corrective instruction to redirect the REPL agent. |
| `/memory` | List all stored memories (name, type, description) |
| `/memory list` | Same as `/memory` |
| `/memory show <name>` | Show the full body of a stored memory |
| `/memory delete <name>` | Delete a stored memory by name |
| `/memory save` | Extract memories from the current session and save them now (also runs automatically on `/exit`) |
| `/paste` | Enter multi-line paste mode; type `EOF` on its own line to finish |
| `/save` | Save a Markdown transcript to `repl-<sessionId>.md` in the current directory |
| `/save <file>` | Save the transcript to a specific file |
| `/snapshot` | Write a full debug snapshot of the current session state — metadata, active modes, context stats, tool inventory, plan state, and full message history — to a timestamped JSON file in `/tmp/fuseraft/`. Prints the file path on completion. |
| `/context` | Show context window usage: token count vs. budget, explicit budget label, completed turn count, per-role message counts, per-category breakdown, delta since last check, and projected turns remaining after 2+ turns. The headline token count uses the real size the provider reported for the most recently completed turn's opening request when available, falling back to a char-based estimate before the first turn or when the provider never reports usage (e.g. Ollama); the per-category breakdown always stays estimated. Also shows cumulative session usage — actual input/output tokens reported by the provider across every LLM call so far, summed across tool-call round trips (not reset by `/clear`, `/rewind`, or `/compact`) |
| `/events` | Show event stats for the current session: turns, total tool calls, per-turn tool breakdown, top tools by frequency, and total plus per-turn actual input/output tokens (real provider-reported usage, shown only for turns where the provider reported it) |
| `/events stats` | Same as `/events` |
| `/explore <query>` | Run a sub-agent exploration loop over the codebase and return a prose summary. The sub-agent uses read-only tools and runs in an isolated context with no shared history from the main session. |
| `/locate <symbol>` | Run a sub-agent symbol lookup and return a `path:line` result. Faster and more targeted than `/explore` for single-symbol lookups. |
| `/safe-mode` | Show current safe mode status |
| `/safe-mode on` | Disable Shell, Git, and Http tool categories to prevent mutations |
| `/safe-mode off` | Restore tool categories to their state before safe mode was enabled |
| `/adversarial` | Show adversarial mode status |
| `/adversarial on` | Enable a critic agent that reviews each `/execute` step after postconditions pass, and every free-form response. The critic judges whether the response was correct, grounded in actual tool output, and complete — halting the plan on a step rejection, or injecting one correction turn on a free-form rejection. |
| `/adversarial off` | Disable the critic agent |
| `/provider` | Show the current model, endpoint, and API key store |
| `/provider setup` | Reconfigure provider URL and API key, then pick a model from the live provider list; saves immediately |
| `/model` | Show current model and reasoning effort |
| `/model <id>` | Switch to a different model without clearing history |
| `/model <id> <effort>` | Switch model and set reasoning effort in one step (e.g. `/model grok-4.3 low`) |
| `/models` | List all models available from the current provider. Highlights the active model. |
| `/reasoning` | Show current reasoning effort |
| `/reasoning <effort>` | Set reasoning effort for the current model. Accepted values are provider-specific (common: `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`) — fuseraft passes the value through as-is rather than validating against a fixed list. Injected as `"reasoning": {"effort": "..."}` in the request. |
| `/max-tokens <n>` | Cap the model's output to `n` tokens per response |
| `/max-tokens reset` | Restore the provider's default max output tokens |
| `/exit` | End the session |

**Switching models and reasoning effort**

`/model <id>` switches the LLM mid-session without clearing history. `/reasoning <effort>` adjusts the reasoning depth of the current model without switching it. Both can be combined: `/model grok-4.3 high` switches to grok-4.3 and sets high reasoning effort in a single command.

Reasoning effort support and accepted values vary by provider and model — e.g. xAI `grok-4.3` accepts `none` / `low` / `medium` / `high`, and some newer models add finer tiers like `minimal` or `xhigh`/`max` for the low and high ends. `none` disables thinking tokens entirely for fast structured output; the highest tier a model supports uses maximum reasoning for complex tasks. The level is injected at the HTTP layer — no provider-specific SDK support is required, so the same mechanism works for any model that accepts a top-level `reasoning` object. fuseraft does not validate the value against a fixed list, so new provider tiers work without a CLI update; an unsupported value is rejected by the provider's API.

**Prompt format**

The prompt displays the current turn number followed by `>`:

```
1> your message here
```

When safe mode is active it gains a `[safe]` prefix:

```
[safe] 1> your message here
```

After each response a compact status line is printed showing the turn number, estimated token usage, and the number of tool calls made:

```
  ── turn 1 · ~3,200 tok · 2 tools
```

**Input and line editing**

The REPL prompt supports history navigation and in-line editing without any external dependencies:

| Key | Action |
|-----|--------|
| Up / Down arrow | Navigate through input history for the current session |
| Left / Right arrow | Move cursor one character |
| Ctrl+Left / Ctrl+Right | Jump one word left or right |
| Home / Ctrl+A | Move to the beginning of the line |
| End / Ctrl+E | Move to the end of the line |
| Backspace | Delete the character before the cursor |
| Delete / Ctrl+D | Delete the character under the cursor (Ctrl+D on an empty line exits) |
| Ctrl+U | Kill (delete) from the cursor to the start of the line |
| Ctrl+K | Kill from the cursor to the end of the line |
| Ctrl+W | Kill the word before the cursor |
| Ctrl+C | Cancel the current line and exit the session |

**Plan / execute workflow**

`/plan` and `/execute` give you explicit control over when the model thinks versus when it acts.

```
1> /plan create a Hello World C# console app in ./hello
  planning…
  Plan (3 steps). Review, then run /execute.

  1. Create the project directory
     tool: CreateDirectory  creates: hello/
  2. Write Program.cs with a Hello World entry point
     tool: WriteFile  creates: hello/Program.cs
  3. Write hello.csproj targeting net10.0
     tool: WriteFile  creates: hello/hello.csproj

2> /execute
  Executing 3-step plan…

  Execute step 1 of 3: Create the project directory
  ⠋ conjuring…  create_directory
  ⚙  create_directory
  assistant: Directory created.
  ✓ Step 1 complete.  2 steps remaining.

  Execute step 2 of 3: Write Program.cs …
  …
  ✓ Step 3 complete.  Plan finished.
```

`/plan <task>` sends the task to the model with tools disabled and instructs it to output a machine-readable JSON array. Each element carries a `step` number, a `description` of the action, an optional `tool` (the tool name the step is expected to call), and an optional `creates` (a path that should exist after the step completes).

`/execute` loads the steps into an execution queue. The REPL drives each step as its own turn — there is no single "run everything" prompt. After each turn it verifies postconditions: if a `tool` was declared the REPL checks that tool was actually called; if a `creates` path was declared it checks the file or directory exists. A postcondition failure halts the queue immediately with a warning so you can investigate before anything else is touched.

**Recovering from a halted plan**

When a step fails the REPL preserves the halted step and all remaining steps. You can interact with the agent freely before deciding how to continue.

`/resume` re-queues the halted step verbatim. Use it when you have already fixed the underlying issue yourself and just want execution to continue.

`/recover` re-queues the halted step but prepends a recovery context block to the step prompt. The block tells the agent which tool was expected, which tools were actually called, and what the step was trying to accomplish. Use it when you want the agent to diagnose and self-correct without manual intervention.

```
> /execute
  Executing 9-step plan…

  ⚠ Step 2: expected tool 'search_content' was not called.
  Plan halted. Run /recover to let the agent diagnose and retry, or /resume to retry directly.

> /recover
  Halted step: 2 of 9 — Search codebase for .NET 9 references
  Expected tool: search_content
  Tools called:  patch_file, patch_file, patch_file
  Recovery context set. Retrying from step 2…

  ✓ Step 2 complete.  7 steps remaining.
  …
```

If the retry fails again the plan halts a second time and both `/recover` and `/resume` remain available. `/clear` discards halted state along with the rest of the session.

**Branching and rewinding**

`/fork`, `/fork switch`, `/conversation`, and `/rewind` give you git-like control over conversation history without leaving the REPL.

**/fork — save a branch point**

`/fork` writes a complete snapshot of the current session — history, plan state, halted-step state — to a new session ID and saves it to disk. The current session keeps running unchanged.

```
5> /fork
Forked to: a3f1c9de  (5 turns copied)
Resume with: fuseraft repl --resume a3f1c9de
Or: /fork switch to branch and continue as the fork right now.
```

Open the fork later in a separate terminal:

```bash
fuseraft repl --resume a3f1c9de
```

**/fork switch — branch and continue**

`/fork switch` does the same thing but immediately becomes the fork. The original session is already checkpointed from the last turn's auto-save; the live session continues under the new ID. All subsequent auto-saves, events, and turn tracking use the fork's ID.

```
5> /fork switch
Switched to fork: a3f1c9de  (was b8fe12c0)
```

This is the recommended flow when you want to explore a different direction from the current point without losing the original thread.

**/switch — jump between sessions**

`/switch <id>` saves the current session and loads another one in its place — no exit required. History, turn counter, plan state, and model (if different) are all restored from the snapshot. Use `/sessions` to find IDs.

```
8> /sessions
  a3f1c9de  claude-sonnet-4-6  5 turns  2m ago  fuseraft-cli
  b8fe12c0  claude-sonnet-4-6  8 turns  now     fuseraft-cli

8> /switch a3f1c9de
Switched to: a3f1c9de  (was b8fe12c0)
Model: claude-sonnet-4-6
5 turns · started 2026-05-25 14:32

6>
```

If the target session used a different model, `fuseraft` rebuilds the chat client automatically. If the model can't be loaded (missing key, unavailable endpoint), it warns and keeps the current model.

**/conversation — see what's in memory**

`/conversation` lists all turns currently in memory with their 1-based indices — use it to find a turn number before running `/rewind`.

```
5> /conversation
5 turns:

    1  you: "can you help me refactor this module?"
       asst: "Sure — here's a plan. First we'll extract the interface, then…"
    2  you: "looks good, let's do it"
       asst: "Done. I've updated Foo.cs and Bar.cs with the new interface…"
    3  you: "actually let's try a different approach"
       asst: "Of course. What direction did you have in mind?"
    4  you: "use a strategy pattern instead"
       asst: "Good call. Here's the revised design…"
    5  you: "write the code"
       asst: "Here it is…"

  /rewind <n>   — keep turns 1…n, discard the rest
  /rewind -<n>  — step back n turns from current
```

If `TrimHistory` has evicted early turns to fit the context window, a note is shown and numbering starts from the oldest turn still in memory.

**/rewind — go back**

`/rewind` truncates history to a chosen point, updates the turn counter, resets plan state, and adjusts token tracking to match. The model picks up from the new tail of the conversation as if the discarded turns never happened.

| Command | Effect |
|---------|--------|
| `/rewind 2` | Keep turns 1–2, discard turns 3 and beyond |
| `/rewind -1` | Drop the most recent turn |
| `/rewind -3` | Drop the last 3 turns |
| `/rewind 0` | Drop all turns (equivalent to `/clear`) |
| `/rewind -99` | Clamped to 0 — always safe |
| `/rewind 99` | Clamped to current end — no-op with message |

**Typical workflows**

*Try two approaches from the same starting point:*
```
3> /fork switch          # branch; original is saved at turn 3
4> take the strategy pattern approach
…
8> /switch b8fe12c0      # jump back to the original without exiting
4> take the adapter pattern approach instead
```

*Undo the last turn and try again:*
```
5> /rewind -1
Rewound to after turn 4 — 1 turn removed.
5> let's try that differently…
```

*Rewind to a specific decision point:*
```
8> /conversation         # find the right turn number
8> /rewind 3             # discard turns 4–8
4> here's a better approach…
```

*Flip between two parallel threads of work:*
```
/sessions                # note the IDs of both sessions
/switch <id-a>           # work on thread A
…
/switch <id-b>           # work on thread B
…
```

**Adversarial mode**

Enable adversarial mode with `/adversarial on` to add a critic agent as an extra gate on both `/execute` steps and ordinary chat turns.

For `/execute` steps: after the deterministic postcondition check passes (tool called, file created), the critic receives an isolated view of the step — its description, the tools called, and the agent's response — and judges whether the step was actually completed correctly. If it approves, execution continues. If it rejects, the plan halts just as a postcondition failure would, with the critic's reason stored as a recovery hint. Running `/recover` then injects that reason into the retry prompt so the agent knows exactly what the critic found wrong.

```
> /adversarial on
  Adversarial mode on: critic agent will review every /execute step and free-form response.

> /execute
  Executing 4-step plan…

  ⚙  patch_file
  assistant: Updated the handler.
  ✗ Critic rejected step 2: The patch changed `HandleRequest(HttpContext)` but the
    interface expects `HandleRequest(HttpContext, CancellationToken)`.
  Plan halted. Run /recover to let the agent diagnose and retry, or /resume to retry directly.

> /recover
  Recovery context set. Retrying from step 2…
  ✓ Step 2 complete.  2 steps remaining.
```

For ordinary chat turns (outside `/execute`): the critic reviews the question, the tools called, and the response after every free-form reply, checking that claims are grounded in actual tool output rather than fabricated. On rejection, fuseraft injects one correction turn telling the agent what the critic found wrong and asking it to verify with a tool call; the correction turn itself is not re-reviewed, so a second rejection just stands.

```
> Where is the retry limit for streaming errors defined?
  assistant: It's set to 5 in ReplTurn.cs.
  ✗ Critic: No tool was called to verify this — MaxStreamRetries is unconfirmed and the value is
    likely wrong.
  ↺ (correction turn) assistant: grep_file → MaxStreamRetries = 2 in ReplTurn.cs:20.
```

The critic runs in an isolated context with no shared history from the main session — the same sub-agent infrastructure used by `/explore` and `/locate`. It requires tools to be active; `/adversarial on` will warn if `--no-tools` was set at startup. On timeout or error the critic degrades to approved so a transient failure never blocks execution. Every free-form turn under adversarial mode costs one extra LLM call for the critic review.

**Getting unstuck with /assist**

When a session has stalled — the agent keeps making the same mistake, misunderstood the task early on, or is caught in a loop — run `/assist`. A sub-agent reads the conversation history, identifies the root cause, and writes a corrective instruction addressed to the REPL agent. That instruction is shown to you and then injected into the conversation as a user message, redirecting the main agent without requiring you to diagnose the problem yourself.

```
> /assist
  diagnosing…
  assist →
  You have been repeatedly patching src/Auth/Handler.cs but the interface mismatch is
  in src/Auth/IHandler.cs. Update the interface definition first, then re-patch the
  implementation to match.

  assistant: You're right — I missed the interface. Let me fix IHandler.cs first...
```

`/assist` does not modify the plan queue or halted state. It injects one message and then the session continues normally. Use it at any point — during plan execution, after a halt, or in a free-form conversation that has drifted off track.

### Memory commands

The REPL automatically maintains a persistent memory store at `~/.fuseraft/memory/repl/`. Each entry is identified by a UUID and stored as `memory_{guid}.md`. Memories are **scoped to the working directory** where they were created:

- If the current directory contains a `.fuseraft/` folder, the REPL loads only memories whose GUIDs are listed in `~/.fuseraft/sessions/{project_slug}/{session_id}/memory_refs.json`. Directories with a `.fuseraft/` folder but no refs file start with an empty memory set (no cross-project bleed).
- Directories without a `.fuseraft/` folder fall back to loading all global memories (legacy behaviour, useful outside of a project context).

When a memory is saved, the REPL writes the entry to the global store and registers its GUID in `~/.fuseraft/sessions/{project_slug}/{session_id}/memory_refs.json` for the current session. Repeated saves of the same-named memory reuse the existing GUID, so the entry is updated in-place rather than duplicated.

At session start, scoped memories are injected into the system prompt. When the session ends (via `/exit` or Ctrl+C), the model is prompted to extract key facts and they are saved automatically.

```
> /memory
  [user_role] (user): Senior engineer working on fuseraft-cli
  [feedback_terse] (feedback): Prefers concise responses without trailing summaries

> /memory show user_role
  ---
  guid: 3f1a8c2e...
  name: user_role
  type: user
  ---
  Senior software engineer working on the fuseraft-cli codebase. Expert in C# and
  distributed systems. Prefers direct answers over lengthy explanations.

> /memory delete feedback_terse
  Deleted memory 'feedback_terse'.

> /memory save
  saving memory…  2 entries saved.
```

Each memory file lives at `~/.fuseraft/memory/repl/memory_{guid}.md`. Use `/memory save` mid-session if you want to capture facts before the session ends naturally.

**Agent reliability guardrails**

The REPL harness applies several layers of runtime checking to catch common model failure modes before they propagate.

*Mutation-claim correction* — After each free-form turn, the harness checks whether the assistant claimed a write action (e.g. "I updated the file", "I created the directory") without having called a write tool in that same turn. When this is detected it auto-injects a correction turn:

```
You described changes above but did not call any write tool.
Please call write_file or patch_file now to actually apply the changes.
Do not re-describe the changes — just call the tool.
```

If the agent still does not call a write tool on the correction turn, a warning is printed to the terminal so you can verify the result manually.

*Completion checklist* — The agent's system prompt includes a structured self-verification checklist that fires before every response:

- **Tools & verification:** every action was performed with a tool call — not described as if done; tool calls succeeded (no errors, exit code 0 for shell)
- **Files:** for file writes, re-read the file to confirm content is correct
- **Shell:** shell output is shown and confirms the goal was met
- **Completeness:** every part of the request was addressed; nothing was deferred or skipped without explaining why

*Unverified assumption tombstoning* — Covered in the `/compact` section below.

**Compacting a session**

As a conversation grows, token usage climbs and the model's effective context window shrinks. Use `/compact` to reset history without losing continuity:

1. The model summarises the entire conversation into a handoff document — what was being worked on, key decisions, current state, and what comes next.
2. The full history is discarded and replaced with that single summary message. The system prompt, tools, and skills catalog are kept intact.
3. The session continues as if it had just started, but with the summary as its opening context.

**Unverified assumption tombstoning**

During compaction, the summarizing model scans for turns where the assistant stated facts about files, code, or system state without a corresponding tool call in that same turn. Those claims are not carried forward as established facts — instead they become compact tombstone markers:

```
[UNVERIFIED ASSUMPTION: claimed src/api/users.go defines a CreateUser function]
```

Facts confirmed by actual tool output (`read_file`, `shell_run`, `grep_file`, etc.) are summarised normally. The REPL agent is instructed to treat any `[UNVERIFIED ASSUMPTION: ...]` marker it encounters as an unconfirmed claim that requires tool verification before acting on it. This prevents bad early claims from silently propagating across a compaction boundary.

Pass an optional focus hint to steer the summary toward the next task:

```
> /compact fix the auth middleware next
  compacting…
  Session compacted — history replaced with handoff summary.

> What was the last thing we did?
  assistant: Based on the compacted context: we finished wiring the JWT validation
  middleware and left off on ...
```

Use `/context` before compacting to see how full the window is. `/compact` is additive with the [handoff skill](skills.md) — the skill writes a doc to disk for handing off to a *different* session, while `/compact` resets the *current* session in place.

**Event log**

Every session appends structured JSONL events to its own `~/.fuseraft/logs/{project_slug}/repl_events/{session_id}.jsonl` (created automatically) — one file per session, so no single log grows unbounded across sessions. Each record is tagged with a UTC timestamp, session ID, and turn index. `fuseraft log repl` reads every session's log by default; pass `--session <id or prefix>` to view just one. The full set of event types:

| Event type | When emitted |
|------------|-------------|
| `session_start` | Session begins |
| `session_end` | Session exits cleanly |
| `user_input` | Each user message submitted |
| `turn_start` | Model starts processing a turn |
| `turn_end` | Model finishes a turn — includes `elapsed_ms`, `estimated_tokens`, `tool_rounds`, `tool_count` |
| `assistant_response` | Final assistant message for a turn |
| `tool_call` | Each individual tool invocation |
| `compaction` | `/compact` or `compact_context` fires — includes `before_tokens`, `after_tokens`, `source`, `focus` |
| `cancelled` | Turn cancelled by Ctrl+C |
| `context_warning` | Context window exceeds 75% of the 80k token budget — includes `estimated_tokens`, `budget`, `pct` |
| `correction_injected` | Harness injects a write-tool correction after a mutation claim with no tool call |
| `plan_captured` | `/plan` stores a new plan — includes `step_count` |
| `step_complete` | `/execute` step passes postconditions — includes `step`, `total`, `steps_left` |
| `step_halted` | `/execute` step fails postconditions — includes `step`, `total`, `expected_tool`, `tool_calls` |
| `command` | Slash command issued |

Use `/events` to view a summary of the current session without leaving the REPL.

**Examples**

```bash
# Start a REPL with auto-detected model and built-in tools (both forms are equivalent)
fuseraft
fuseraft repl

# Use a specific model
fuseraft repl --model grok-4-1-fast-reasoning

# Plain chat — no tools
fuseraft repl --model grok-4-1-fast-reasoning --no-tools

# Set a system prompt at startup
fuseraft repl --model grok-code-fast-1 --system "You are a Rust expert."

# Switch models and make it the new default
fuseraft repl --model claude-sonnet-4-6 --save
```

Press Ctrl+C during a streaming response to cancel that request and return to the prompt. Press Ctrl+C at the prompt or type `/exit` to end the session. The readline layer intercepts Ctrl+C at the prompt so the process exits cleanly rather than abruptly.

---

## `fuseraft sessions`

Manage session checkpoints.

```
fuseraft sessions [options]
```

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `-a, --all` | off | Include completed sessions (default shows only incomplete). |
| `-d, --delete <target>` | — | Delete session by ID, or `all` to delete all completed sessions. |
| `--prune` | off | Delete sessions whose config file no longer exists on disk. |
| `--project <fragment>` | — | Filter by working directory fragment (e.g. `brewer` or `fuseraft-cli`). |
| `--cleanup` | off | Delete sessions older than `--older-than`, removing both index entries and session artifact directories. |
| `--older-than <age>` | `30d` | Age threshold for `--cleanup`. Accepts `Nd` (days), `Nw` (weeks), `Nh` (hours). |

The listing is read from `~/.fuseraft/sessions/index.json` — a lightweight per-session metadata file kept in sync by the session store. No checkpoint files are opened, so listing is fast regardless of message history size.

**`--cleanup` and `.fuseraftignore`**

When `.fuseraft/.fuseraftignore` is present, `--cleanup` deletes only the files within each session directory that are marked ephemeral by the ignore rules — preserving handoff artifacts such as `brief.json`, `conventions.json`, `context_summary.md`, and `intents.json`. Empty directories are removed after the file sweep. When `.fuseraftignore` is absent, the entire session directory is deleted.

**Examples**

```bash
# List incomplete sessions
fuseraft sessions

# List all sessions including completed
fuseraft sessions --all

# List only sessions for a specific project
fuseraft sessions --all --project brewer

# Delete a specific session
fuseraft sessions --delete a3f92c1d

# Purge all completed sessions
fuseraft sessions --delete all

# Remove sessions whose config file is gone
fuseraft sessions --prune

# Delete sessions older than 30 days (default threshold)
fuseraft sessions --cleanup

# Delete sessions older than 2 weeks, scoped to one project
fuseraft sessions --cleanup --older-than 2w --project brewer
```

Session files are stored in `~/.fuseraft/sessions/` with owner-only permissions.

---

## `fuseraft plugins`

List all registered plugins and their functions.

```
fuseraft plugins [options]
```

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `-p, --plugin <name>` | — | Filter to plugins whose name contains this substring (case-insensitive). |

**Examples**

```bash
# Show all plugins
fuseraft plugins

# Show only the Shell plugin
fuseraft plugins --plugin shell

# Show only MCP-sourced plugins (by partial name match)
fuseraft plugins --plugin demo
```

Output shows built-in plugins (C# methods decorated with `[Description]`) and MCP-sourced plugins (tools from connected servers) separately.

---

## `fuseraft validate`

Validate a config file without starting a session.

```
fuseraft validate <path> [options]
```

**Arguments**

| Argument | Description |
|----------|-------------|
| `<path>` | Path to the orchestration config file. JSON (`.json`) and YAML (`.yaml` / `.yml`) are both accepted. |

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `--strict` | off | Fail if any agent references a plugin not in the default built-in registry. |
| `--diagram` | off | Print a Mermaid flowchart of the workflow after validation. |
| `--check-connectivity` / `-c` | off | Make a minimal live test call to each unique provider endpoint to verify the API key is valid and the endpoint is reachable. See below. |

**Checks performed**

1. File exists and contains valid JSON or YAML (syntax checked based on extension)
2. Top-level `Orchestration` key is present
3. Every agent has a non-empty `Name`, `Instructions`, and `ModelId`
4. Agent names are unique within the config
5. `FunctionChoice` values are `auto`, `required`, or `none`
6. Selection strategy type is `sequential`, `llm`, `keyword`, `structured`, or `magentic`
7. If LLM selection: `Selection.Model` is configured
8. If keyword selection: `Routes` array is non-empty
9. If magentic selection: `Selection.Magentic.Model` is configured; warns if a non-default `Termination` section is present (it is ignored for Magentic)
10. Termination strategy type is `regex`, `structured`, `tokenbudget`, `maxiterations`, or `composite`
11. Regex termination: `Pattern` is non-empty
12. Structured termination: `Condition` is present and its `Field` is non-empty
13. Token budget termination: `MaxTokens` is positive; warns if it is not lower than the top-level `MaxTotalTokens`
14. Agent names referenced in termination strategies exist in the agents list
15. If `Telemetry` is set: `OtlpEndpoint` is a valid absolute URI
16. With `--strict`: every plugin name in any agent's `Plugins` list is registered
17. For every `ApiKeyEnvVar` referenced: the environment variable is set in the current shell (warning if missing). Note: agents that rely on the OS keychain rather than an env var skip this check — keychain auth is verified only when `--check-connectivity` is used.

**Exit codes**

- `0` — validation passed (warnings may still be printed)
- `1` — one or more errors found

**`--check-connectivity` / `-c`**

After all static checks, makes a 1-token chat request to each unique provider endpoint. Providers are deduplicated by `(endpoint, modelId, apiKey)` so a config with five agents on the same Claude model only hits Anthropic once. Covers all model slots: agent models, LLM/Magentic selection models, and the compaction model.

```bash
fuseraft validate .fuseraft/config/orchestration.yaml --check-connectivity
fuseraft validate .fuseraft/config/orchestration.yaml -c
```

Sample output:

```
✓ claude-opus-4-5 (api.anthropic.com) — key valid   agents: Planner, Developer, Tester
✗ gpt-4o (api.openai.com)             — invalid API key (HTTP 401)   agents: Reviewer
```

Failures are counted as errors and reflected in the exit code. The check has a 15-second timeout per endpoint. **Each call costs approximately 1 input + 1 output token** — negligible, but non-zero.

Models with no resolvable API key (env var unset and no literal `ApiKey`) are skipped; the missing-key warning from the static checks already covers them.

**`--diagram` output**

Prints a Mermaid `flowchart LR` to stdout after the validation result. Each keyword route becomes a labelled edge; validators appear as additional lines in the label. Terminal routes (the self-routing convention, e.g. `APPROVED` from Reviewer → Reviewer) point to a `Done` node rather than looping back.

```bash
fuseraft validate .fuseraft/config/orchestration.yaml --diagram
fuseraft validate config/examples/orchestration.yaml --diagram
```

```
flowchart LR

  Task([Task])
  Planner["Planner"]
  Developer["Developer"]
  Tester["Tester"]
  Reviewer["Reviewer"]
  Done(["✓ Done"])

  Task --> Planner

  Planner -->|"HANDOFF TO DEVELOPER<br/>RequireBrief"| Developer
  Developer -->|"HANDOFF TO TESTER<br/>RequireWriteFile<br/>RequireShellPass"| Tester
  Tester -->|"HANDOFF TO REVIEWER<br/>TestReportValid"| Reviewer
  Tester -->|"BUGS FOUND"| Developer
  Reviewer -->|"REVISION REQUIRED"| Developer
  Reviewer -->|"REPLAN REQUIRED"| Planner
  Reviewer -->|"APPROVED<br/>RequireShellPass<br/>RequireReviewJudgement"| Done
```

Paste the output into [mermaid.live](https://mermaid.live) to render it. The diagram is printed regardless of whether validation passed or failed, so it can be used to visually debug a broken config.

For sequential configs the diagram renders as a simple linear chain: `Task → Agent1 → Agent2 → …`.

---

## `fuseraft config`

Display or list config files.

```
fuseraft config [path] [options]
```

**Arguments**

| Argument | Default | Description |
|----------|---------|-------------|
| `[path]` | `.fuseraft/config/orchestration.yaml` | Config file to display. |

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `-l, --list` | off | List all `.json`, `.yaml`, and `.yml` files found under `.fuseraft/config/` instead of displaying a single file. |

**Examples**

```bash
# Display default config as formatted tables
fuseraft config

# Display a specific config
fuseraft config configs/devops-team.json

# List all configs in the configs/ directory
fuseraft config --list
```

---

## `fuseraft init`

Generate a ready-to-run YAML orchestration config from an interactive wizard or explicit flags.

```
fuseraft init [output] [options]
```

**Arguments**

| Argument | Default | Description |
|----------|---------|-------------|
| `[output]` | `.fuseraft/config/orchestration.yaml` | Path to write the generated config. |

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `-t, --template <name>` | interactive | Team template to use. See templates below. |
| `-m, --model <id>` | auto-detected | Model ID to use for all agents. Auto-detected from your API keys if omitted. |
| `-e, --endpoint <url>` | `~/.fuseraft/config` | Provider API endpoint URL. Defaults to the endpoint saved in `~/.fuseraft/config` if present. At run time, agents without an explicit `Endpoint` also inherit this value automatically. |
| `--no-interactive` | off | Skip all prompts and generate with the supplied options and defaults. |
| `--no-boilerplate` | off | Skip `architecture.yaml` and `knowledge/lifecycle.yaml` — for small or single-purpose projects that won't use `fuseraft arch check` or `fuseraft knowledge gc`. |
| `-f, --force` | off | Overwrite the config and all agent files without prompting, even if they already exist. |

**Overwrite behavior**

Before writing anything, `init` checks whether the config file or any of its agent files (`agents/*.yaml`) already exist. If any do, it lists every conflicting path and asks for confirmation — declining, or passing `--no-interactive` without `--force`, aborts with no files written. Pass `--force` to skip the check and overwrite everything unconditionally, including any hand-edited agent files.

**Templates**

| Template | Description |
|----------|-------------|
| `solo` | Single capable agent with investigation tooling and lossless compaction — the right starting point for simple tasks |
| `pipeline` | Planner → Developer → Tester → Reviewer as a directed graph; investigation tooling on Developer and Tester; no evidence contracts — use `swe` for production work |
| `swe` | Full SWE pipeline: Planner → PlannerCritic → Developer → Tester → Reviewer with evidence contracts, hypothesis tracking, periodic Verifier, adaptive ContextBudget, and lossless compaction |
| `brownfield` | Archaeology-first pipeline as a directed graph: Archaeologist recons the codebase once, then Planner → Developer → Reviewer; Reviewer routes to Developer (REVISION REQUIRED) or Planner (REPLAN REQUIRED) |
| `research` | Researcher gathers cited findings → Critic adversarially reviews for gaps and unsupported claims → Writer synthesises the final document |
| `data` | DataEngineer fetches and structures raw data → Analyst computes findings → Reporter synthesises a final document; contracts prevent fabricated analysis |
| `devops` | OpsPlanner writes an ops plan with `rollback_command` → Executor runs steps → Verifier health-checks; Verifier can trigger a rollback cycle if checks fail |
| `debate` | Decision-focused adversarial pipeline: Proposer argues a position → Challenger critiques adversarially → Moderator synthesises a structured final verdict |
| `audit` | Auditor scans for security / quality / compliance issues → Prioritizer triages by severity → Developer fixes with hypothesis tracking → Verifier confirms |
| `magentic` | AI-managed team: a manager LLM plans and coordinates five specialist workers (Researcher, Planner, Developer, Tester, Critic) dynamically; user approves the plan before execution |

**Model auto-detection**

If `--model` is not provided, `init` first checks the `ModelId` saved in `~/.fuseraft/config`. If no model is saved there, it inspects environment variables in this order and picks the default model for the first provider that has a key set:

| Environment variable | Default model |
|---------------------|---------------|
| `OPENAI_API_KEY` | `gpt-4o` |
| `ANTHROPIC_API_KEY` | `claude-sonnet-4-6` |
| `XAI_API_KEY` | `grok-4` |
| `GOOGLE_AI_API_KEY` | `gemini-2.5-flash` |
| `MISTRAL_API_KEY` | `mistral-medium-latest` |
| `DEEPSEEK_API_KEY` | `deepseek-chat` |

If no key is set, `gpt-4o` is used as the fallback default.

**Examples**

```bash
# Interactive wizard (prompts for template, model, provider URL, and output path)
fuseraft init

# Write to a custom path
fuseraft init .fuseraft/config/my-team.yaml

# Single agent — simplest starting point
fuseraft init --template solo
fuseraft init --template solo --no-interactive

# Standard dev pipeline (graph) — no evidence contracts
fuseraft init --template pipeline --model claude-sonnet-4-6

# Full SWE pipeline — evidence contracts, hypothesis tracking, periodic Verifier
fuseraft init --template swe --model claude-sonnet-4-6
fuseraft init .fuseraft/config/swe.yaml --template swe --model claude-sonnet-4-6

# Brownfield codebase — Archaeologist recons first, then plan → implement → review
fuseraft init --template brownfield
fuseraft init --template brownfield --model claude-sonnet-4-6 --endpoint https://api.anthropic.com

# Research pipeline — Researcher → Critic → Writer
fuseraft init --template research --model claude-sonnet-4-6

# Data analysis pipeline — DataEngineer → Analyst → Reporter
fuseraft init --template data

# Infrastructure and deployment with rollback
fuseraft init --template devops

# Adversarial deliberation for decisions and design reviews
fuseraft init --template debate

# Security / quality / compliance audit
fuseraft init --template audit --model claude-sonnet-4-6

# AI-managed Magentic team
fuseraft init --template magentic
fuseraft init .fuseraft/config/magentic-team.yaml --template magentic --model gpt-4o

# CI / scripted usage
fuseraft init .fuseraft/config/ci-team.yaml --template swe --model gpt-4o --no-interactive

# Regenerate an existing config and agent files without prompting
fuseraft init --template swe --model claude-sonnet-4-6 --no-interactive --force
```

After generating, `init` prints the next steps:

```
Review:   fuseraft config .fuseraft/config/orchestration.yaml
Validate: fuseraft validate .fuseraft/config/orchestration.yaml
Run:      fuseraft run --config .fuseraft/config/orchestration.yaml "Your task"
```

`init` also scaffolds the knowledge directory tree and writes default config files the first time it is run in a directory:

| File created | Purpose |
|---|---|
| `.fuseraft/architecture.yaml` | Architecture layer manifest for `fuseraft arch check` |
| `.fuseraft/knowledge/lifecycle.yaml` | Retention policy for `fuseraft knowledge gc` |
| `.fuseraft/knowledge/decisions/` | Architecture decision records (ADRs) |
| `~/.fuseraft/knowledge/{project_slug}/repository/` | Cross-session repository memory patterns |
| `.fuseraft/knowledge/objectives/` | Long-horizon objective tracking |

These files are skipped if they already exist.

---

## `fuseraft graph`

Repository semantic graph — index and query symbols across the codebase.

### `fuseraft graph build`

Scan all `.cs`, `.go`, and `.py` source files under the project root and write (or overwrite) the repository semantic graph to `~/.fuseraft/state/{project_slug}/repository.graph`. The graph records every file, namespace/package, type, interface, method, property, field, and ADR as a node; edges express structural relationships (`defines`, `imports`, `inherits`, `implements`, `references`, `adr_governs`).

Agents use the graph via the `graph_search`, `graph_refs`, and `graph_dependents` plugin tools. The graph is also updated incrementally by the harness whenever an agent writes a `.cs`, `.go`, or `.py` file.

```
fuseraft graph build [options]
```

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `-d, --dir <path>` | current directory | Root directory to scan. |
| `-o, --output <path>` | `~/.fuseraft/state/{project_slug}/repository.graph` | Output path for the graph file. |

**Examples**

```bash
# Build the graph for the current project
fuseraft graph build

# Scan only a subdirectory
fuseraft graph build --dir src/

# Write to a custom location
fuseraft graph build --output /tmp/my-project.graph
```

---

## `fuseraft arch`

Architecture drift detection — check that source files respect the layer boundaries defined in `.fuseraft/architecture.yaml`.

### `fuseraft arch check`

Scan import statements in all source files under the project root and compare them against the layer manifest. Exits `0` when no violations are found, `1` when at least one violation is detected.

`fuseraft init` writes a default `.fuseraft/architecture.yaml` on first run. Edit its `Language`, `Layers`, and `MayDependOn` lists to match your project.

```
fuseraft arch check [options]
```

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `-m, --manifest <path>` | `.fuseraft/architecture.yaml` | Path to the architecture manifest. |
| `-d, --dir <path>` | current directory | Root directory to scan. |

**Examples**

```bash
# Check against the default manifest
fuseraft arch check

# Use a custom manifest
fuseraft arch check --manifest config/arch.yaml

# Scan only the src/ subtree
fuseraft arch check --dir src/
```

**Output**

When violations are found the command prints a table:

```
File                        Line  Source Layer    Target Layer  Namespace
src/cli/commands/run.py       8   Cli             Core          myapp.infrastructure.db
```

Each row identifies the offending file, the line number of the illegal import, the layer that owns the source file, the layer that owns the imported namespace, and the namespace itself.

---

### Manifest format

The manifest is a YAML file with a top-level `Language` field and a `Layers` list.

**`Language`** — selects the file glob and import-statement parser. Supported values:

| Value | Files scanned | Import syntax detected |
|-------|--------------|------------------------|
| `csharp` (default) | `*.cs` | `using Foo.Bar;` |
| `python` | `*.py` | `import foo.bar` · `from foo.bar import …` |
| `java` | `*.java` | `import com.example.Foo;` · `import static …` |
| `typescript` | `*.ts`, `*.tsx` | `import … from '…'` · `require('…')` |
| `javascript` | `*.js`, `*.jsx` | `import … from '…'` · `require('…')` |
| `go` | `*.go` | `import "pkg/path"` · import block lines |
| `rust` | `*.rs` | `use foo::bar::Baz;` |
| `ruby` | `*.rb` | `require 'foo/bar'` |

Unknown values fall back to `csharp`. Relative imports (e.g. `./foo`, `../bar`) are automatically ignored for TypeScript, JavaScript, and Ruby.

**`Layers`** — each entry has:

| Field | Required | Description |
|-------|----------|-------------|
| `Name` | yes | Display name used in violation reports. |
| `Paths` | yes | Source path prefixes owned by this layer, relative to project root. |
| `Namespaces` | no | Module/namespace prefixes owned by this layer. For `csharp`, defaults to `fuseraft.<Name>` when omitted. For all other languages, must be declared explicitly. |
| `MayDependOn` | no | Names of other layers this layer may import from. Omit or leave empty to forbid all cross-layer imports. |

**`Namespaces` format by language:**

| Language | Example |
|----------|---------|
| `python` | `myapp.core` |
| `java` | `com.example.core` |
| `typescript` / `javascript` | `src/core` or `@myorg/core` |
| `go` | `github.com/myorg/myrepo/core` |
| `rust` | `myapp::core` |
| `ruby` | `myapp/core` |

**Example — Python project:**

```yaml
Language: python

Layers:
  - Name: Domain
    Paths:
      - myapp/domain/
    Namespaces:
      - myapp.domain
    MayDependOn: []

  - Name: Infrastructure
    Paths:
      - myapp/infra/
    Namespaces:
      - myapp.infra
    MayDependOn:
      - Domain

  - Name: Api
    Paths:
      - myapp/api/
    Namespaces:
      - myapp.api
    MayDependOn:
      - Domain
      - Infrastructure
```

**Example — Go project:**

```yaml
Language: go

Layers:
  - Name: Domain
    Paths:
      - internal/domain/
    Namespaces:
      - github.com/myorg/myrepo/internal/domain
    MayDependOn: []

  - Name: Repository
    Paths:
      - internal/repository/
    Namespaces:
      - github.com/myorg/myrepo/internal/repository
    MayDependOn:
      - Domain

  - Name: Handler
    Paths:
      - internal/handler/
    Namespaces:
      - github.com/myorg/myrepo/internal/handler
    MayDependOn:
      - Domain
      - Repository
```

**Quick start with the REPL:**

```
fuseraft repl
```

Then paste this prompt to auto-populate the manifest for your project:

```
Read the source tree and populate .fuseraft/architecture.yaml with the actual
layers, source paths, namespace prefixes, and MayDependOn rules for this project.
Set Language to the project's primary language. Use write_file to save the result.
```

---

## `fuseraft knowledge`

Knowledge lifecycle management — archive superseded ADRs, demote stale repository memories, decay old provenance claims, prune orphaned graph nodes, and compact the provenance registry.

### `fuseraft knowledge gc`

Run all lifecycle policies configured in `.fuseraft/knowledge/lifecycle.yaml`. **Dry-run by default** — pass `--apply` to commit changes to disk.

```
fuseraft knowledge gc [options]
```

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `--apply` | off | Commit lifecycle changes to disk. Without this flag the command reports what would change without touching any files. |
| `-l, --lifecycle <path>` | `.fuseraft/knowledge/lifecycle.yaml` | Path to the lifecycle policy file. |
| `--graph <path>` | `~/.fuseraft/state/{project_slug}/repository.graph` | Override the repository graph path. |
| `--nuclear` | off | Extreme mode — also clears every reproducible global file (logs, memories, sessions, run state, crash dumps, scratchpad) for **every project**, not just this one. Requires `--apply`; always prompts for an extra confirmation unless `--yes` is also passed. |
| `-y, --yes` | off | Skip the extra confirmation prompt required by `--nuclear`. |

**`.fuseraftignore` integration**

When `.fuseraft/.fuseraftignore` is present and `--apply` is set, `fuseraft knowledge gc` also deletes ephemeral state and log files listed in the ignore file (e.g. `knowledge_findings.json` under `~/.fuseraft/state/{project_slug}/`, and `app.log`/`repl_events/*.jsonl` under `~/.fuseraft/logs/{project_slug}/`, scanned recursively). Files produced by gc itself — such as `provenance.archive.json` — are never deleted.

**Policy fields** (in `lifecycle.yaml`)

| Field | Default | Effect |
|-------|---------|--------|
| `AdrRetentionDays` | `0` | Days after a decision reaches `Superseded` status before it is archived. `0` = archive immediately on the next gc run. |
| `MemoryReinforceWindowDays` | `90` | Demote `Approved` repository memories to `Candidate` when they have not been reinforced for this many days. |
| `ConfidenceDecayDays` | `30` | Downgrade `Verified` provenance claims to `Inferred` when their `VerifiedAt` is older than this many days and no `ExpiresAt` is set. `0` = disable decay. |
| `OrphanedNodeGracePeriodDays` | `7` | Prune graph nodes with no edges and no recent file touch after this many days. `0` = disable. |
| `MaxProvenanceAgeDays` | `0` | Archive provenance records past `ExpiresAt` after this many additional days. `0` = archive immediately. |

**Examples**

```bash
# Preview what would be archived/demoted/decayed (dry-run)
fuseraft knowledge gc

# Apply all lifecycle policies
fuseraft knowledge gc --apply

# Use a custom lifecycle config
fuseraft knowledge gc --apply --lifecycle custom/lifecycle.yaml

# Preview the full global reset (every project's logs/memories/sessions/etc.)
fuseraft knowledge gc --nuclear

# Actually clear it, skipping the confirmation prompt
fuseraft knowledge gc --nuclear --apply --yes
```

Archived ADRs are moved to `.fuseraft/knowledge/decisions/archive/` and remain queryable via `decision_search`. Archived provenance records are appended to `~/.fuseraft/state/{project_slug}/provenance.archive.json`.

**`--nuclear`**: the big-red-button mode. In addition to the policies above, it wipes the global,
machine-generated subtrees under `~/.fuseraft/` — `logs/`, `memory/`, `knowledge/` (repository memory
graphs), `sessions/`, `repl-sessions/`, `snapshots/`, `state/`, `crashdump/`, `scratchpad/`, and
`skill-curation.jsonl` — across **every project**, not just the one you're standing in. It never
touches `config/`, `.key`, `schedule/`, or `skills/`, and never touches a project's own `.fuseraft/`
directory. It always prints a per-category file-count/size report first; add `--apply` to actually
delete, which then prompts for a second confirmation (bypass with `--yes`) since the blast radius spans
every project on the machine.

---

## `fuseraft memory`

Persistent memory — REPL/agent facts (`list`, `delete`) stored in `~/.fuseraft/memory/`, and repository memory — cross-session patterns extracted from the evidence graph after each session closes (`review`). Repository memory candidates must be approved before they are injected into agent prompts.

### `fuseraft memory list`

List stored REPL or agent memories.

```
fuseraft memory list [options]
```

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `--agent <agent>` | — | Target the named agent's memory store (`~/.fuseraft/memory/agents/<agent>`) instead of the REPL memory store. |

**Examples**

```bash
# List REPL memories
fuseraft memory list

# List a specific agent's memories
fuseraft memory list --agent reviewer
```

### `fuseraft memory delete`

Delete a stored REPL or agent memory by name, or wipe the entire store.

```
fuseraft memory delete [name] [options]
```

**Arguments**

| Argument | Description |
|----------|-------------|
| `[name]` | Name of the memory to delete (as shown by `fuseraft memory list` or `/memory` in the REPL). |

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `--all` | off | Delete every stored memory instead of a single named entry. |
| `--agent <agent>` | — | Target the named agent's memory store (`~/.fuseraft/memory/agents/<agent>`) instead of the REPL memory store. |
| `-y, --yes` | off | Skip the confirmation prompt when using `--all`. |

**Examples**

```bash
# Delete a single REPL memory by name
fuseraft memory delete build-command

# Wipe all REPL memories (prompts for confirmation)
fuseraft memory delete --all

# Wipe all memories for a specific agent, skipping confirmation
fuseraft memory delete --all --agent reviewer --yes
```

### `fuseraft memory review`

Interactively review candidate repository memories and approve or reject them. Approved memories are injected into the system prompt of every subsequent agent session; rejected memories are suppressed.

```
fuseraft memory review [options]
```

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `--dir <path>` | `~/.fuseraft/knowledge/{project_slug}/repository` | Repository memory directory. |
| `--all` | off | Show all entries including `Approved` and `Rejected`, not just `Candidate` entries. |

**Examples**

```bash
# Review pending candidates (interactive)
fuseraft memory review

# Browse all entries including already-decided ones
fuseraft memory review --all
```

For each candidate you are prompted to **Approve**, **Reject**, or **Skip**. The decision is written to disk immediately; the command can be interrupted and re-run.

---

## `fuseraft objective`

Long-horizon objective tracking — create and monitor objectives that span multiple sessions.

Active objectives are summarised in the system prompt of every agent session and in compaction summaries so the team never loses sight of the big picture.

### `fuseraft objective create`

Create a new long-horizon objective.

```
fuseraft objective create [options]
```

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `-t, --title <text>` | interactive | Short title for the objective. |
| `-d, --description <text>` | — | What the objective achieves and why it matters. |
| `--tasks <list>` | — | Comma-separated initial remaining tasks. |

**Examples**

```bash
# Interactive (prompts for title)
fuseraft objective create

# Non-interactive
fuseraft objective create --title "Ship auth refactor" --description "Replace session tokens with JWTs" --tasks "Design,Implement,Test,Deploy"
```

---

### `fuseraft objective list`

List all objectives, optionally filtered by status.

```
fuseraft objective list [options]
```

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `-s, --status <status>` | — | Filter: `Active`, `Paused`, `Completed`, `Abandoned`. |
| `-a, --all` | off | Show all objectives regardless of status. |

**Examples**

```bash
# Show all objectives
fuseraft objective list

# Show only active objectives
fuseraft objective list --status Active
```

---

### `fuseraft objective status`

Show detailed status and progress for a single objective.

```
fuseraft objective status <id>
```

**Arguments**

| Argument | Description |
|----------|-------------|
| `<id>` | Objective ID (e.g. `OBJ-0001`). |

**Examples**

```bash
fuseraft objective status OBJ-0001
```

Output includes the title, description, status, computed completion percentage, completed and remaining task lists, and all session IDs that contributed work.

---

## `fuseraft context`

Manage reference material that is automatically available to all agents in a session.

When a session starts, fuseraft reads the context index and appends a summary block to every agent's system prompt. Agents can then call `read_file` to access the files — no extra tool is needed and no discovery step is required.

Files are stored in `.fuseraft/context/<name>/` inside the project working directory, so they are always inside the sandbox.

### `fuseraft context add`

Import a file or directory into the context store.

```
fuseraft context add <source> [options]
```

**Arguments**

| Argument | Description |
|----------|-------------|
| `<source>` | Path to the file or directory to import. Supports `~` expansion. |

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `-n, --name <alias>` | Filename without extension (files) or directory name (dirs) | Short alias used to reference this item in agent prompts. Only letters, digits, hyphens, and underscores are allowed. |
| `-d, --description <text>` | — | Human-readable description appended to the context entry in agent prompts. |
| `--dir <path>` | Current directory | Project directory containing `.fuseraft/`. |

**Examples**

```bash
# Import a single file (name derived from filename: "architecture")
fuseraft context add ~/docs/architecture.pdf

# Import with an explicit alias and description
fuseraft context add ~/data/schema.sql --name db-schema --description "Production database schema"

# Import an entire directory
fuseraft context add ~/specs/ --name specs --description "Product specifications"

# Target a specific project directory
fuseraft context add ~/docs/runbook.md --dir ~/projects/my-app
```

**Binary document extraction:** When the source is a `.pdf`, `.docx`, `.pptx`, or `.xlsx` file, fuseraft automatically extracts the plain text and stores it as a `.txt` file. Agents read the extracted text via `read_file` — no `Document` plugin required. A note is printed on import:

```
✓ architecture — 1 file(s), 48.2 KB
  Extracted from architecture.pdf: PDF — 24 page(s) → architecture.txt
```

If extraction fails (encrypted file, corrupt format), the binary is stored with a warning and will not be readable by agents via `read_file`.

After importing, agents see an entry like this at the top of their system prompt:

```
CONTEXT — reference material imported for this session (use read_file to access):
  [db-schema] — Production database schema
    .fuseraft/context/db-schema/schema.sql  (12.4 KB, imported 2026-04-12)
```

### `fuseraft context list`

List all imported context items.

```
fuseraft context list [options]
```

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `--dir <path>` | Current directory | Project directory containing `.fuseraft/`. |

**Examples**

```bash
fuseraft context list
fuseraft context list --dir ~/projects/my-app
```

### `fuseraft context remove`

Remove a context item and delete its copied files.

```
fuseraft context remove <name> [options]
```

**Arguments**

| Argument | Description |
|----------|-------------|
| `<name>` | Alias of the context item to remove. |

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `--dir <path>` | Current directory | Project directory containing `.fuseraft/`. |

**Examples**

```bash
fuseraft context remove db-schema
fuseraft context remove specs --dir ~/projects/my-app
```

When the last item is removed the `index.json` file is also deleted, leaving the context directory clean.

---

## `fuseraft schedule`

Create, list, remove, and run scheduled fuseraft sessions using cron expressions. Jobs are stored as YAML files in `~/.fuseraft/schedule/`. No daemon is required — `fuseraft schedule run` is designed to be called by `cron`, `systemd.timer`, or any periodic scheduler.

### `fuseraft schedule add`

Create a new scheduled job.

```
fuseraft schedule add <name> --cron <expr> --task <description> [options]
```

**Arguments**

| Argument | Description |
|----------|-------------|
| `<name>` | Unique job name, used as the YAML filename slug (e.g. `nightly-audit` → `~/.fuseraft/schedule/nightly-audit.yaml`). |

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `--cron <expr>` | — | **Required.** Standard 5-field cron expression (minute hour day month weekday). Example: `"0 2 * * *"` for 2 AM UTC daily. |
| `-t, --task <text>` | — | **Required.** Task description passed to `fuseraft run` as the session goal. |
| `-c, --config <path>` | `.fuseraft/config/orchestration.yaml` | Path to the orchestration config YAML used for this job. |
| `--work-dir <path>` | — | Working directory passed to `fuseraft run --work-dir`. |
| `-o, --output <path>` | — | Output transcript path template. Supports `{name}`, `{date}` (`yyyy-MM-dd`), and `{time}` (`HHmm`) substitutions. `~` is expanded. Example: `~/.fuseraft/logs/{name}-{date}.txt`. |
| `-d, --description <text>` | — | Human-readable description shown in `fuseraft schedule list`. |

**Examples**

```bash
# Run a security audit every night at 2 AM UTC
fuseraft schedule add nightly-audit \
  --cron "0 2 * * *" \
  --task "Run a security audit of the codebase and report findings" \
  --config .fuseraft/config/security-team.yaml \
  --output "~/.fuseraft/logs/nightly-audit-{date}.txt"

# Generate a weekly status report every Monday at 9 AM UTC
fuseraft schedule add weekly-report \
  --cron "0 9 * * 1" \
  --task "Generate a weekly status report" \
  --config .fuseraft/config/report.yaml \
  --description "Weekly stakeholder report"

# Run in a specific working directory
fuseraft schedule add dependency-check \
  --cron "0 6 * * *" \
  --task "Check for outdated dependencies and open a PR if any are found" \
  --work-dir ~/projects/my-app
```

The job file is written to `~/.fuseraft/schedule/{slug}.yaml`. The next scheduled run time is computed and saved immediately.

---

### `fuseraft schedule list`

List all scheduled jobs.

```
fuseraft schedule list
```

Displays a table with name, cron expression, next run time (UTC), last run time, and enabled status. Jobs that are currently due are highlighted in yellow with a `(due)` indicator.

**Examples**

```bash
fuseraft schedule list
```

```
╭──────────────────┬─────────────┬──────────────────────┬──────────────────────┬─────────╮
│ Name             │ Cron        │ Next Run (UTC)        │ Last Run (UTC)       │ Enabled │
├──────────────────┼─────────────┼──────────────────────┼──────────────────────┼─────────┤
│ nightly-audit    │ 0 2 * * *   │ 2026-05-18 02:00      │ 2026-05-17 02:00     │ yes     │
│ weekly-report    │ 0 9 * * 1   │ 2026-05-18 09:00      │ never                │ yes     │
╰──────────────────┴─────────────┴──────────────────────┴──────────────────────┴─────────╯
```

---

### `fuseraft schedule remove`

Remove a scheduled job.

```
fuseraft schedule remove <name>
```

**Arguments**

| Argument | Description |
|----------|-------------|
| `<name>` | Name of the job to remove. |

Deletes the YAML file and any associated `.lock` file.

**Examples**

```bash
fuseraft schedule remove nightly-audit
```

---

### `fuseraft schedule run`

Execute all due jobs, or force-run a specific job by name.

```
fuseraft schedule run [options]
```

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `-n, --name <name>` | — | Force-run a specific job, ignoring its schedule and enabled status. Omit to tick all due jobs. |
| `--dry-run` | off | Show which jobs would execute without actually running them. |

**How it works**

For each due job (or the named job when `-n` is used):

1. A `.lock` file (`{slug}.lock`) is created to prevent concurrent execution. If the lock already exists, the job is skipped with a warning.
2. The session is launched via `fuseraft run <task> --no-banner [--config …] [--work-dir …]`, capturing output to the configured `OutputPath` (or stdout if none is set).
3. After the run completes, `last_run` is set to the current UTC time and `next_run` is computed from the cron expression. The job YAML is updated atomically.
4. The lock file is removed.

**Examples**

```bash
# Tick all due jobs (designed to be called by cron every minute)
fuseraft schedule run

# Preview what would run without executing anything
fuseraft schedule run --dry-run

# Force-run a specific job now, regardless of schedule
fuseraft schedule run --name nightly-audit
```

**Setting up system cron**

Add a crontab entry that calls `fuseraft schedule run` every minute:

```
# m h dom mon dow command
* * * * * /usr/local/bin/fuseraft schedule run --no-banner >> ~/.fuseraft/logs/schedule.log 2>&1
```

Or use `systemd.timer` for finer control:

```ini
# ~/.config/systemd/user/fuseraft-schedule.service
[Unit]
Description=fuseraft scheduled session runner

[Service]
ExecStart=/usr/local/bin/fuseraft schedule run --no-banner
```

```ini
# ~/.config/systemd/user/fuseraft-schedule.timer
[Unit]
Description=Run fuseraft schedule every minute

[Timer]
OnCalendar=minutely
Persistent=true

[Install]
WantedBy=timers.target
```

```bash
systemctl --user enable --now fuseraft-schedule.timer
```

**Job YAML format**

Each job is stored as a plain YAML file in `~/.fuseraft/schedule/`:

```yaml
name: nightly-audit
description: Nightly security audit
cron: 0 2 * * *
task: Run a security audit of the codebase and report findings
config: .fuseraft/config/security-team.yaml
work_dir: ~/projects/my-app
output_path: ~/.fuseraft/logs/nightly-audit-{date}.txt
enabled: true
created_at: 2026-05-17T10:00:00+00:00
last_run: 2026-05-17T02:00:00+00:00
next_run: 2026-05-18T02:00:00+00:00
```

Jobs can be edited by hand — `fuseraft schedule run` reads the YAML fresh on each tick. Set `enabled: false` to temporarily pause a job without removing it.

---

## `fuseraft skills`

Install, list, remove, and validate global skills available to all agent sessions. Skills are stored in `~/.fuseraft/skills/` and registered in an FTS5 search index so fuseraft can automatically identify which ones are relevant to a given task.

See [Skills](skills.md) for an overview of how skills work and how to write them.

### `fuseraft skills add`

Copy a skill into `~/.fuseraft/skills/` and add it to the search index.

```
fuseraft skills add <source>
```

**Arguments**

| Argument | Description |
|----------|-------------|
| `<source>` | Path to a skill directory (containing `SKILL.md`) or directly to a `SKILL.md` file. Supports `~` expansion. |

The slug is derived from the `name:` field in the `SKILL.md` frontmatter. If no `name:` field is present, the source directory name is used. If a skill with the same slug already exists it is updated in place.

**Examples**

```bash
# Install a skill from a sibling repository
fuseraft skills add ../skills/sandbox-test

# Install from a personal skills library
fuseraft skills add ~/my-skills/triage

# Point directly at a SKILL.md file
fuseraft skills add ~/my-skills/triage/SKILL.md
```

---

### `fuseraft skills list`

List all installed global skills.

```
fuseraft skills list
```

Displays a table with the slug, description, `compatibility` field (if any), and Agent Skills specification conformance (`✓`/`✗`) for each skill found under `~/.fuseraft/skills/`. Run `fuseraft skills validate` for details on any `✗` entries.

**Examples**

```bash
fuseraft skills list
```

---

### `fuseraft skills remove`

Remove an installed global skill and drop it from the search index.

```
fuseraft skills remove <slug>
```

**Arguments**

| Argument | Description |
|----------|-------------|
| `<slug>` | Slug of the skill to remove, as shown by `fuseraft skills list`. |

**Examples**

```bash
fuseraft skills remove triage
```

---

### `fuseraft skills curation-log`

View the skill curation log. Every curation attempt — success, skip, or failure — is recorded in `~/.fuseraft/skill-curation.jsonl`.

```
fuseraft skills curation-log [options]
```

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `-n, --last <N>` | all | Show only the last N entries. |
| `--outcome <outcome>` | — | Filter by outcome: `created`, `updated`, `skipped`, `no_skill`, `failed`. |
| `--source <source>` | — | Filter by source: `run` or `repl`. |
| `--path <path>` | `~/.fuseraft/skill-curation.jsonl` | Override the log file path. |

**Examples**

```bash
# View the full curation log
fuseraft skills curation-log

# Show only failures
fuseraft skills curation-log --outcome failed

# Show the last 20 entries from REPL sessions
fuseraft skills curation-log --last 20 --source repl
```

See [Configuration → Skill curation](configuration.md#skill-curation) for the log format and outcome reference.

---

### `fuseraft skills validate`

Validate a `SKILL.md`'s frontmatter against the [Agent Skills specification](https://agentskills.io/specification) — fuseraft's equivalent of the spec's own recommended `skills-ref validate` tool. Checks the `name` field's format, length, and match against its parent directory name; the `description` field's presence and length; and the `compatibility` field's length. Uses the same validator fuseraft's orchestration skills provider applies at load time, so a skill that passes here is guaranteed to load identically in both the REPL and `fuseraft run` sessions.

```
fuseraft skills validate [path]
```

**Arguments**

| Argument | Description |
|----------|-------------|
| `[path]` | Path to a skill directory to validate. Omitted: validates every skill under `~/.fuseraft/skills/`. |

Exits with status `0` when every checked skill is fully conformant, `1` otherwise.

**Examples**

```bash
# Validate every installed skill
fuseraft skills validate

# Validate a skill before installing it
fuseraft skills validate ../skills/sandbox-test
```

---

## `fuseraft log`

View fuseraft log files. Orchestration session logs (`fuseraft log events`) are read from the global `~/.fuseraft/logs/sessions/` directory. REPL and application logs are read from the current project's `.fuseraft/logs/` directory.

### `fuseraft log events`

View the orchestration event log produced by `fuseraft run` sessions.

```
fuseraft log events [options]
```

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `-n, --last <N>` | all | Show only the last N entries. |
| `--session <id>` | — | Filter by session ID (prefix match). |
| `--event <type>` | — | Filter by event type (e.g. `session_error`, `tool_blocked`, `validation_fail`). |
| `--path <path>` | session-scoped | Override the log file path. Omit to read all sessions, or use `--session` to scope to one. |

**Examples**

```bash
# Tail the 50 most recent events
fuseraft log events --last 50

# Show all errors from the current project
fuseraft log events --event session_error

# Show all events for a specific session
fuseraft log events --session a3f92c1d
```

---

### `fuseraft log repl`

View the REPL event log produced by interactive `fuseraft repl` sessions.

```
fuseraft log repl [options]
```

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `-n, --last <N>` | all | Show only the last N entries. |
| `--session <id>` | — | Show only the matching session's log (ID or unique prefix), instead of every session. |
| `--event <type>` | — | Filter by event type (e.g. `command`, `skill_curation_complete`, `assistant_response`). |
| `--path <path>` | all session logs under `~/.fuseraft/logs/{project_slug}/repl_events/` | Override the log file path. |

**Examples**

```bash
# Show the last 50 REPL events
fuseraft log repl --last 50

# Show all slash commands issued in the current project
fuseraft log repl --event command

# Show curation events only
fuseraft log repl --event skill_curation_complete
```

---

### `fuseraft log app`

View the application log. fuseraft writes Warning-level and above messages here for diagnostics that survive past the terminal session.

```
fuseraft log app [options]
```

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `-n, --last <N>` | `50` | Show the last N lines. |
| `--level <level>` | — | Filter by Serilog level token: `inf`, `wrn`, `err`, `dbg`. |
| `--path <path>` | `~/.fuseraft/logs/{project_slug}/app.log` | Override the log file path. |

**Examples**

```bash
# Show the last 50 lines
fuseraft log app

# Show only errors
fuseraft log app --level err

# Show the last 200 lines
fuseraft log app --last 200
```

---

## `fuseraft models`

List all models available from the configured provider.

```
fuseraft models
```

Reads `~/.fuseraft/config` to resolve the provider endpoint and API key, then calls the provider's models listing endpoint (`GET {endpoint}/models` for OpenAI-compatible providers; `GET {endpoint}/api/tags` for Ollama). The currently configured model is highlighted.

If `~/.fuseraft/config` is missing or incomplete, the command runs the same interactive setup wizard as `fuseraft repl` — prompting for a provider URL and API key, then a model picked from the live list — and saves the result before fetching the model list.

The output ends with a hint pointing at `fuseraft repl --model <id>` (and `--save` to make it the default) — see [`fuseraft repl`](#fuseraft-repl) above.

**Example**

```bash
fuseraft models
```

```
  Available models from https://api.anthropic.com/v1 (12)

  claude-3-5-haiku-20241022
  claude-3-5-sonnet-20241022
  claude-3-haiku-20240307
  claude-sonnet-4-6  ← current
  …
```

---

## `fuseraft update`

Fetch the latest release from GitHub and atomically replace the running binary.

```
fuseraft update [options]
```

**Options**

| Flag | Default | Description |
|------|---------|-------------|
| `--check` | off | Report whether a newer release is available without downloading or installing anything. |

The command detects the current platform and architecture, downloads the matching release archive (`fuseraft-<version>-<rid>.tar.gz`), and installs the new binary.

**Linux / macOS** — the new binary is written to a `.new` sidecar file and atomically renamed over the original. This works even while fuseraft is running because `rename()` is inode-level.

**Windows** — Windows locks the running executable and cannot rename it in place. `fuseraft update` instead writes the new binary as `fuseraft.exe.pending` in the same directory, then launches `fuseraft-update.exe` in a new console window and exits. The updater:
1. Waits a moment for the calling fuseraft process to exit.
2. Checks for any remaining fuseraft instances and asks whether to kill them.
3. Renames `fuseraft.exe` → `fuseraft.exe.backup` (blocks new launches during the swap).
4. Moves `fuseraft.exe.pending` → `fuseraft.exe`.
5. Deletes the backup and reports success.

`fuseraft-update.exe` must be present alongside `fuseraft.exe`. It is included in every Windows release archive published by CI.

If the current version already matches or exceeds the latest release the command exits immediately with no changes.

**Examples**

```bash
# Check whether an update is available
fuseraft update --check

# Download and install the latest release
fuseraft update
```
