# Plugins

Plugins are the tools agents can call. Each plugin is a named collection of kernel functions. Add plugin names to an agent's `Plugins` list to make them available.

```yaml
Plugins:
  - FileSystem
  - Shell
  - Git
  - Search
```

Plugin names are case-insensitive.

---

## FileSystem

Read, write, and navigate the local filesystem.

| Function | Parameters | Description |
|----------|-----------|-------------|
| `read_file` | `path`, `startLine` (default 1), `maxLines` (optional) | Read a text file. Returns up to `Security.ReadFileSizeLimit` characters (default 20,000). Larger files are truncated with a notice. Rejects binary files. |
| `grep_file` | `path`, `pattern`, `contextLines` (default 2), `maxMatches` (default 30) | Case-insensitive text or regex search within a single file. Returns matching lines with surrounding context. |
| `get_file_summary` | `path` | Return a previously saved structural summary, or a character-count notice when no summary exists. |
| `save_file_summary` | `path`, `summary` | Persist a structural summary of a file so future agents can retrieve it cheaply via `get_file_summary`. |
| `list_files` | `directory`, `pattern` (default `"*"`), `maxResults` (default 100, clamped to 500) | List files recursively matching a glob pattern. Reports when the cap truncated results — in a large or multi-repo directory the matches beyond the cap may be concentrated in whichever subtree was walked first, so narrow with `directory`/`pattern` rather than only raising `maxResults`. |
| `get_file_info` | `path` | Returns metadata: type (file/directory), size, created/modified timestamps, Unix permissions (on Unix systems), and — for files — the write-version counter (`NOT_TRACKED` if the file exists but was never written through `write_file`). Cheaper than `read_file`, and doubles as an existence check: a "Path not found" result means the path doesn't exist. |
| `write_file` | `path`, `content`, `raw` (default false), `baseVersion` (default 0) | Create or overwrite a file. Creates parent directories automatically. When `baseVersion > 0`, the write is rejected with `VERSION_MISMATCH` if the current stored version differs — use `get_file_info` first to read the version and detect concurrent writes. |
| `patch_file` | `path`, `oldText`, `newText` | Replace an exact block of text in a file. Fails with a hint if `oldText` is not found verbatim. |
| `delete_file` | `path` | Delete a file if it exists. |
| `set_permissions` | `path`, `mode` | Set Unix file permissions (chmod). Accepts a 3- or 4-digit octal string such as `"755"` or `"0644"`. No-op on Windows. |
| `create_directory` | `path` | Create a directory and all intermediate parent directories. Safe to call when the directory already exists. |
| `delete_directory` | `path`, `recursive` (default false) | Delete a directory. Set `recursive=true` to remove a non-empty directory and all its contents. Refuses to delete the sandbox root. |
| `copy_file` | `source`, `destination`, `overwrite` (default false) | Copy a file to a new location. Creates the destination directory if needed. |
| `move_file` | `source`, `destination`, `overwrite` (default false) | Move or rename a file or directory. Creates the destination parent directory if needed. |
| `list_directory` | `directory`, `pattern` (default `"*"`) | List files and subdirectories in a single directory (non-recursive). Subdirectories are shown with a trailing `/`. Returns up to 500 entries. |

**Tip:** When `FileSystemSandboxPath` is configured, all paths are resolved to canonical form and rejected if they fall outside the sandbox root. See [Security](security.md).

---

## Shell

Execute shell commands and scripts.

| Function | Parameters | Description |
|----------|-----------|-------------|
| `shell_run` | `command`, `workingDirectory` (optional), `timeoutSeconds` (default 60), `quiet` (default false) | Run a shell command. Supports pipes, redirects, and chained commands. Captures stdout, stderr, and exit code. Pass `quiet: true` to get `OK` back on success instead of full output (e.g. scaffolding, `dotnet restore`, environment setup) — full output and exit code are still returned on failure regardless of `quiet`. |
| `shell_run_script` | `script`, `workingDirectory` (optional), `timeoutSeconds` (default 120) | Write a multi-line script to a temp file and execute it. Useful for complex multi-command workflows. |
| `shell_get_env` | `name` | Return an environment variable value (empty string if not set). |
| `shell_set_env` | `name`, `value` | Set an environment variable for the current session. Inherited by all subsequent `shell_run` calls. Pass an empty string to clear a variable. |
| `shell_which` | `program` | Return the full path of a program (equivalent to `which` / `where`). |
| `shell_get_working_directory` | — | Return the effective working directory. Returns the sandbox root if a sandbox is configured. |
| `shell_get_session_temp_dir` | — | Return a session-scoped temporary directory. Created on first call; deleted automatically when the session ends. |
| `shell_run_background` | `command`, `workingDirectory` (optional) | Start a command without blocking and return a job ID. Use for long-running processes (dev servers, watchers, pipelines) where you need to continue working while the process runs. |
| `shell_get_job_status` | `jobId` | Check whether a background job is still running, has completed, or was killed. Includes a short tail of recent output. |
| `shell_get_job_output` | `jobId` | Return the full captured output of a background job so far (stdout + stderr combined, capped at 100 KB). |
| `shell_kill_job` | `jobId` | Terminate a running background job. |

The shell used is `/bin/bash` on Unix and `cmd.exe` on Windows. The shell binary is located from common system paths at startup.

**Windows PowerShell fallback:** Agents commonly write PowerShell syntax (`Get-ChildItem`, `$env:`, `Where-Object`, ...) even though `cmd.exe` is the default shell here, since PowerShell is the modern norm on Windows. `cmd.exe` can't resolve any of that and always fails with the same `'X' is not recognized as an internal or external command` message. `shell_run` and `shell_run_script` detect that exact signature and transparently retry the command via PowerShell (preferring `pwsh` if installed, falling back to the built-in Windows PowerShell 5.1) before returning to the agent — so a PowerShell-flavored command succeeds on the first try instead of costing a wasted tool call. `shell_run_background` applies the same retry within a short grace window after starting the process, swapping in a PowerShell process before the job ID is ever handed back if the original exits immediately with that signature. If the command genuinely fails (in either shell), the original `cmd.exe` failure is what's returned — the fallback never masks a real error.

**`sudo` protection:** `sudo` is always blocked. Any command or script containing `sudo` (including after pipes, `&&`, `;`, or newlines) is rejected before execution. The denial message instructs the agent to use non-privileged alternatives (`pip install --user`, `pipx`, virtualenvs) or, if elevated access is truly required, to tell the user what to run so they can do it themselves.

**Shell command approval in `--hitl` mode:** When `fuseraft run --hitl` is active, every `shell_run` and `shell_run_script` call pauses and shows the command for approval before executing. See [CLI Reference — Shell command approval](cli-reference.md#human-in-the-loop-controls).

**Security note:** When `FileSystemSandboxPath` is set, the `workingDirectory` argument is hard-denied if it falls outside the sandbox. The `command` and `script` arguments are scanned for absolute paths escaping the sandbox; system binary prefixes (`/usr/`, `/bin/`, `/opt/`, `/nix/`, etc.) are exempted. Shell scanning is heuristic — for strict containment use `CodeExecution` (Docker) instead.

---

## Git

Read and write a Git repository.

**Read operations**

| Function | Parameters | Description |
|----------|-----------|-------------|
| `git_status` | `repoPath` (default `"."`) | Show working-tree status: staged, unstaged, and untracked files. |
| `git_diff` | `repoPath`, `staged` (default false), `maxLines` (default 200) | Show a unified diff. Pass `staged: true` for the index diff. |
| `git_log` | `repoPath`, `count` (default 10), `ref` (optional) | Show recent commit history with hashes, authors, and messages. |
| `git_show` | `commitRef`, `repoPath`, `maxLines` (default 300) | Show the content and diff of a specific commit. |
| `git_branch_list` | `repoPath`, `includeRemotes` (default false) | List branches. |
| `git_stash_list` | `repoPath` | List all stashed changesets. |
| `git_is_inside_work_tree` | `repoPath` (optional) | Returns `"true"` if the path is inside a git working tree, `"false"` otherwise (exit codes 128 or 129 map to `"false"`). Use this to guard git operations when the sandbox may not be a git repository. |

**Write operations**

| Function | Parameters | Description |
|----------|-----------|-------------|
| `git_add` | `paths`, `repoPath` | Stage files. Pass `"."` to stage everything. |
| `git_commit` | `message`, `repoPath`, `stageAll` (default false) | Commit staged changes. |
| `git_checkout` | `target`, `repoPath`, `createBranch` (default false) | Switch to a branch or restore files. |
| `git_create_branch` | `branchName`, `repoPath` | Create a new branch from HEAD. |
| `git_init` | `directory` (optional) | Initialize a new repository. |
| `git_push` | `remote` (optional), `branch` (optional), `setUpstream` (default false), `repoPath` | Push local commits to a remote. Omit `remote` and `branch` to push the current tracking branch. 120 s timeout. |
| `git_pull` | `remote` (optional), `branch` (optional), `repoPath` | Fetch and integrate changes from a remote. 120 s timeout. |
| `git_stash` | `message` (optional), `repoPath` | Save the current working-tree changes to the stash. |
| `git_stash_pop` | `repoPath` | Apply the most recent stash entry and remove it from the stash list. |
| `git_reset` | `mode` (default `"mixed"`), `ref` (default `"HEAD"`), `repoPath` | Reset HEAD. `soft`: moves HEAD only. `mixed`: unstages changes. `hard`: discards all uncommitted changes — use with caution. |

---

## Http

Make HTTP requests to external APIs.

| Function | Parameters | Description |
|----------|-----------|-------------|
| `http_get` | `url`, `headers` (JSON, optional), `timeoutSeconds` (default 30), `profile` (optional) | GET request. Returns response body as text. |
| `http_post` | `url`, `body`, `contentType` (default `"application/json"`), `headers`, `timeoutSeconds`, `profile` | POST request with body. |
| `http_put` | `url`, `body`, `contentType` (default `"application/json"`), `headers`, `timeoutSeconds`, `profile` | PUT request. |
| `http_patch` | `url`, `body`, `contentType` (default `"application/json"`), `headers`, `timeoutSeconds`, `profile` | PATCH request. Useful for partial updates (e.g. closing a ticket). |
| `http_delete` | `url`, `headers`, `timeoutSeconds` (default 30), `profile` | DELETE request. |
| `http_head` | `url`, `headers`, `timeoutSeconds` (default 30), `profile` | HEAD request. Returns response headers only, no body. |

### API profiles (`profile` parameter)

All HTTP functions accept an optional `profile` parameter. When provided, the plugin looks up that name in the `ApiProfiles` config section and applies:

- **Base URL** — if `url` is a relative path (no `http://`/`https://` scheme), it is prepended with the profile's `BaseUrl`. Absolute URLs are used as-is.
- **Default headers** — merged with any per-call `headers`. Per-call headers win on conflicts.
- **Timeout** — the profile's `TimeoutSeconds` is used when the caller does not supply an explicit timeout (i.e. when `timeoutSeconds` equals the default of 30).

```yaml
# .fuseraft/config/orchestration.yaml snippet
ApiProfiles:
  snow:
    BaseUrl: "https://mycompany.service-now.com/api/now"
    TimeoutSeconds: 60
    DefaultHeaders:
      Authorization: "Bearer ${SNOW_API_TOKEN}"
      Accept: "application/json"
```

```
# Agent can now call:
http_get(url="/table/incident", profile="snow")
# → GET https://mycompany.service-now.com/api/now/table/incident
#   Authorization: Bearer <value of $SNOW_API_TOKEN>
```

Header values in `DefaultHeaders` support `${ENV_VAR}` token expansion — the token is replaced with the environment variable's value at startup. See [Configuration → ApiProfiles](configuration.md#apiprofiles) for the full reference.

**Security note:** When `HttpAllowedHosts` is non-empty, only listed hostnames are permitted. The allowlist is enforced after profile resolution — a profile that resolves to an unlisted host is still denied. Private and loopback addresses (`127.x`, `10.x`, `172.16–31.x`, `192.168.x`, `169.254.x`, IPv6 loopback/link-local) are always blocked regardless of the allowlist.

---

## Json

Parse, transform, and query JSON data.

| Function | Parameters | Description |
|----------|-----------|-------------|
| `json_format` | `json` | Pretty-print a JSON string with indentation. |
| `json_minify` | `json` | Strip whitespace to produce compact output. |
| `json_get` | `json`, `path` | Get a nested value using dot-separated path notation, e.g. `"data.results.0.title"`. |
| `json_keys` | `json` | Return top-level keys of an object, or the length of an array. |
| `json_search` | `json`, `keyName` | Find all keys matching a search term (case-insensitive, deep search). |
| `json_merge` | `baseJson`, `patchJson` | Shallow-merge two JSON objects. Patch fields overwrite base fields. |
| `json_to_text` | `json`, `depth` (default 0) | Convert JSON to a human-readable summary suitable for feeding into a model prompt. |
| `json_validate` | `json` | Check whether a string is well-formed JSON. |

---

## Search

Search file contents and locate symbols. Finding files by name is `list_files` in [FileSystem](#filesystem) — kept there rather than duplicated here since it's the one covered by sandbox path enforcement and the FileSystem capability map.

| Function | Parameters | Description |
|----------|-----------|-------------|
| `search_content` | `query`, `directory` (default `"."`), `filePattern` (default `"*"`), `maxResults` (default 100), `caseSensitive` (default false) | Search file contents by regex or plain text (like grep). |
| `search_symbol` | `symbol`, `directory` (default `"."`), `extension` (default `""`), `maxResults` (default 50) | Find symbol definitions (class, function, interface, variable, etc.) using language-agnostic patterns. Results are automatically recorded as `SymbolDefinition` nodes in the evidence graph when `EvidenceStore` is configured. |
| `search_callers` | `symbol`, `directory` (default `"."`), `extension` (default `""`), `maxResults` (default 100) | Find call sites and usages of a symbol: invocations, constructor calls, type annotations, and inheritance declarations. Excludes definition lines so results contain only references. Results are automatically recorded as `SymbolReference` nodes in the evidence graph when `EvidenceStore` is configured; `TargetFile` is resolved from any existing `SymbolDefinition` nodes for the same symbol. |

**Directory exclusions:** all three functions skip `.git`, `node_modules`, `bin`, `obj`, `.vs`, `.idea`, `.nuget`, `.venv`, `__pycache__`, `.fuseraft`, and `vendor` — the same list `list_files` (FileSystem) uses. This matters most for `search_content`: without it, an unscoped query (`directory: "."`, `filePattern: "*"`) walks into compiled build output and can match inside a `.dll`/`.pdb` read as text, returning megabytes of garbage. Pass a narrower `directory` or `filePattern` (e.g. `*.cs`) to scope a search further.

---

## Probe

Structured hypothesis testing and assertion utilities. Useful for Tester agents that need to verify behavior with evidence.

| Function | Parameters | Description |
|----------|-----------|-------------|
| `probe_code` | `language`, `code`, `directory` (default `"."`), `timeoutSeconds` (default 30) | Execute a code snippet and return stdout/stderr/exit code. Supported languages: `bash`, `python`, `javascript`, `go`, `csharp`, `powershell`, `kiwi`. |
| `probe_assert_output` | `command`, `expected`, `matchType` (default `"contains"`), `directory`, `timeoutSeconds` | Run a command and assert its output. `matchType`: `contains`, `equals`, `regex`, `exitcode`. Returns PASS or FAIL with evidence. |
| `probe_compare_outputs` | `commandA`, `commandB`, `directory`, `timeoutSeconds` | Run two commands and return their outputs side-by-side for comparison. |
| `probe_run_hypothesis` | `hypothesis`, `command`, `expectedObservation`, `setupCommand` (optional), `directory`, `timeoutSeconds` | Given/When/Then structured test. |

On Windows, `language: "powershell"` (or `"ps"`) resolves to `pwsh` if it's installed, otherwise falls back to the built-in Windows PowerShell 5.1 — it no longer fails outright on machines that only have the stock PowerShell.

---

## CodeExecution

Sandboxed code execution via Docker containers. Each run gets a fresh, isolated container with no network access and capped resources.

**Requirements:** Docker CLI and daemon must be running. Check with the `check_docker` function.

| Function | Parameters | Description |
|----------|-----------|-------------|
| `code_execution_check_docker` | — | Verify Docker is available and the daemon is reachable. |
| `code_execution_sandbox_run` | `language`, `code`, `timeoutSeconds` (default 30) | Run code in a fresh Docker container. Supported languages: `python`, `node`, `bash`, `go`, `rust`. |
| `code_execution_repl_start` | `language` | Start a REPL session (Python or Node). Returns a session ID. |
| `code_execution_repl_exec` | `sessionId`, `code`, `timeoutSeconds` | Execute code in an active REPL session. Variables and functions persist between calls. |
| `code_execution_repl_reset` | `sessionId` | Clear accumulated code in a REPL session (session stays open). |
| `code_execution_repl_stop` | `sessionId` | Remove the REPL session and all accumulated state. |

**Container resource limits:** `--network none`, `--memory 256m`, `--cpus 0.5`

**Docker images used:**

| Language | Image |
|----------|-------|
| Python | `python:3.12-slim` |
| Node | `node:20-slim` |
| Bash | `bash:5.2` |
| Go | `golang:1.23-alpine` |
| Rust | `rust:1-slim` |

---

## Scratchpad

Persistent per-agent key-value store that survives across sessions. Each agent that includes `"Scratchpad"` in its `Plugins` list gets an isolated JSON file at `~/.fuseraft/scratchpad/{AgentName}.json`. A `global` scope (`~/.fuseraft/scratchpad/global.json`) is shared by all agents in the orchestration.

Files are stored outside the project directory so notes persist regardless of which project or config file is used.

**Typical usage pattern:**

```
At the start of a resumed session:  scratchpad_read_all()
When you make a key decision:       scratchpad_write("auth_decision", "Use JWT tokens", tags="auth")
When you want to find past notes:   scratchpad_search("auth")
Before ending the session:          scratchpad_write("session_summary", "Completed task X, left Y for next time")
```

| Function | Parameters | Description |
|----------|-----------|-------------|
| `scratchpad_write` | `key`, `value`, `tags` (optional), `scope` (default `"agent"`) | Store or update an entry. Tags are comma-separated keywords. |
| `scratchpad_read` | `key`, `scope` (default `"agent"`) | Retrieve a single entry by key. |
| `scratchpad_read_all` | `scope` (default `"agent"`) | Read all entries — call this at session start to restore context. |
| `scratchpad_search` | `query`, `scope` (default `"agent"`) | Case-insensitive substring search across keys, values, and tags. |
| `scratchpad_delete` | `key`, `scope` (default `"agent"`) | Remove an entry. |

**Scope values:**

| Scope | File | Visibility |
|-------|------|-----------|
| `agent` (default) | `~/.fuseraft/scratchpad/{AgentName}.json` | Private to this agent |
| `global` | `~/.fuseraft/scratchpad/global.json` | Shared by all agents |

**Note:** Unlike other plugins, `"Scratchpad"` is not shared between agents — each agent gets its own instance backed by its own file. This is handled automatically; no extra configuration is needed.

---

## Chatroom

Shared append-only message log for agent-to-agent coordination. All agents that include `"Chatroom"` in their `Plugins` list share the same JSONL file, but each agent sends under its own name.

| Function | Parameters | Description |
|----------|-----------|-------------|
| `chatroom_send` | `recipient`, `message` | Append a message to the log. `recipient` can be a specific agent name or `"All"` to broadcast. |
| `chatroom_read` | `count` (default `20`) | Read the most recent `count` messages from the log. |

**Typical usage:**

```
After completing a task:    chatroom_send("Tester", "Files are written and build passes.")
At the start of your turn:  chatroom_read()
Broadcasting a blocker:     chatroom_send("All", "Blocked on missing dependency — need shell access.")
```

**Note:** Like `"Scratchpad"`, `"Chatroom"` is instantiated per-agent (so each instance knows the sender's name), but all instances write to the same file so messages are visible across the team.

---

## Changes

Read-only view of the session change log written automatically by the orchestrator's `ChangeTracker`. Records which files were written, which commands ran, and which git commits were made — turn by turn — so downstream agents (Tester, Reviewer) know exactly what happened without having to infer it from the chat history.

**Availability:** Only registered when `ChangeTracking` is present in the orchestration config. See [Configuration](configuration.md#change-tracking).

| Function | Parameters | Description |
|----------|-----------|-------------|
| `changes_read` | — | Return the full session change log: every agent turn that wrote files, ran commands, or made git commits. |
| `changes_read_latest` | `count` (default `1`) | Return the `count` most-recent change entries. Use this before deciding what to test or review. |

**Typical usage:**

```
Tester, before writing tests:   changes_read_latest()
Reviewer, at session start:     changes_read()
```

Each entry shows the agent name, turn index, timestamp, files written/deleted, commands run (with pass/fail status), and git commits.

---

## Investigation

Durable investigation memory: records hypotheses, rejected paths, and confirmed root causes so future agents never re-run the same dead-end investigation. All writes go to `~/.fuseraft/state/{project_slug}/investigation-log.json`. The log survives compaction and is injected into every agent's context via the `investigation_log` context source.

**Availability:** Only registered when `ChangeTracking` is present in the orchestration config — same gate as [Changes](#changes). Used by the `brownfield`, `audit`, and `graph` init templates.

| Function | Parameters | Description |
|----------|-----------|-------------|
| `investigation_create_hypothesis` | `hypothesis` | Record a new hypothesis for investigation. Returns an assigned ID (`H-001`, `H-002`, ...). |
| `investigation_reject_hypothesis` | `id`, `reason`, `evidence` (optional, one item per line) | Mark a hypothesis as rejected with the reason and disproving evidence. Also emits an `AttemptFailedEvent` to the event sink. |
| `investigation_confirm_hypothesis` | `id`, `evidence` (optional, one item per line) | Mark a hypothesis as confirmed with supporting evidence. |
| `investigation_record` | `summary`, `conclusion` | Log a completed investigation with its summary and conclusion. |
| `investigation_identify_root_cause` | `cause` | Append a confirmed root cause to the log. No-ops if the same cause is already recorded. |

**Typical usage:**

```
Investigate a lead:      investigation_create_hypothesis("Race condition in the cache invalidation path")
Dead end:                investigation_reject_hypothesis("H-001", "Cache writes are already mutex-guarded", evidence="Checked FileSystemPlugin.cs:120-140")
Confirmed:               investigation_confirm_hypothesis("H-002", evidence="Reproduced with concurrent write_file calls")
Wrap up:                 investigation_record("Checked cache invalidation for races", "Not the cause — see H-003")
Root cause found:        investigation_identify_root_cause("SessionReadCache does not invalidate on write_file with baseVersion=0")
```

---

## Session

Gives REPL agents first-class access to their own session metadata, saved-session history, diagnostic log files, and context management. Always available in the REPL when tools are enabled; not applicable to `fuseraft run` orchestrations.

| Function | Parameters | Description |
|----------|-----------|-------------|
| `repl_session_current` | — | Return the current session's ID, model, start time, working directory, snapshot path, and log file locations. |
| `repl_session_list` | — | List all saved REPL sessions newest-first. The active session is marked with `◄ current`. |
| `repl_session_read_event_log` | `targetSessionId` (optional), `maxLines` (default 50) | Read entries from that session's `repl_events/{session_id}.jsonl`. Defaults to the current session; accepts a full ID or unique prefix for another session. |
| `repl_session_read_log` | `logName` (default `"repl_events"`), `maxLines` (default 100) | Read the tail of a named diagnostic log. Valid names: `repl_events`, `events`, `provider_errors`, `app`. |
| `compact_context` | `focus` (optional) | Compact the conversation history into a concise handoff summary and replace it immediately. Pass an optional one-line focus hint (e.g. `"fix build error in SharePointClient.cs"`) to steer the summary. Call this when context is near the 80k token ceiling or the agent is repeatedly hitting budget errors. |
| `get_context_status` | — | Return the current context budget: `estimated_tokens`, `budget`, `pct_used`, `tokens_remaining`, and `turn`. Call before a multi-file investigation or whenever you want to check how much headroom remains. |

**Session context in system prompt:** The current session ID, start time, snapshot path, and event log path are injected into the system prompt automatically — the agent always knows its session without needing to call a tool first.

**Typical usage:**

```
# Find my session ID and log locations
repl_session_current()

# Compare this session with past ones
repl_session_list()

# Debug what happened in the last 20 events
repl_session_read_event_log(maxLines=20)

# Check for provider errors
repl_session_read_log(logName="provider_errors")

# Check how full the context window is before a large investigation
get_context_status()

# Free up context when nearing the 80k ceiling
compact_context(focus="finish fixing the auth middleware")
```

---

## Todo

Self-directed todo list the model uses to plan and track its own multi-step work within a single REPL session. In-memory only — scoped to the session, not persisted to disk. Always available in the REPL when tools are enabled (i.e. unless `fuseraft repl --no-tools` is used); not applicable to `fuseraft run` orchestrations and not added via an agent's `Plugins` list.

Unlike [Scratchpad](#scratchpad) (free-form key/value notes), Todo holds one ordered checklist that is always replaced wholesale on write — the model writes the full plan up front, then rewrites the full list after each step to flip statuses, rather than patching individual entries.

| Function | Parameters | Description |
|----------|-----------|-------------|
| `todo_write` | `itemsJson` | Replace the current todo list. Pass a JSON array of items, e.g. `[{"content":"Read entry point","status":"completed"},{"content":"Map request flow","status":"in_progress"}]`. `status` is one of `pending`, `in_progress`, `completed`. Always pass the complete list, not just the changed item — this call replaces the whole list. |
| `todo_read` | — | Read the current todo list. |

---

## Compaction

Lets an agent request a history compaction flush on demand — the same path as the automatic
turn-count and token-budget triggers, using whatever compaction mode is configured.

**Availability:** Only effective when `Compaction` is present in the orchestration config.
Calling `compact_conversation` without a configured compactor is a no-op.

```yaml
Plugins:
  - Compaction
```

```
# In agent instructions:
When your context is growing large and you need to free up space, call compact_conversation().
```

| Function | Parameters | Description |
|----------|-----------|-------------|
| `compact_conversation` | — | Compact conversation history using the configured compaction mode. |

**How it works:** The tool returns immediately. The runner detects the call at the end of the
turn and triggers `ApplyCompactionAsync` before the next stream starts — identical to the
automatic threshold trigger.

---

## Handoff

Provides a single `handoff` tool for deterministic, type-safe routing. Agents call `handoff(route_keyword: "...")` instead of emitting a keyword in free text. The tool-call argument is parsed by the model's function-calling infrastructure — far more reliable than expecting an exact string on its own line in an open-ended prose response.

When `handoff` is called, fuseraft terminates the agent's tool loop immediately so no further tools can be called in the same turn. This guarantees the handoff is always the last action the agent takes.

```yaml
Plugins:
  - Handoff
```

```
# In agent instructions:
When your work is complete, call handoff(route_keyword: "HANDOFF TO TESTER").
```

| Function | Parameters | Description |
|----------|-----------|-------------|
| `handoff` | `route_keyword` | Signal completion and hand off to the next step. Provide the exact keyword defined in your instructions (e.g. `"HANDOFF TO TESTER"`, `"APPROVED"`). Must be the last tool call in your response. |

**How it interacts with keyword routing:** The keyword strategy checks for a `handoff` tool call first. If found, the `route_keyword` argument is used directly as the routing signal — text scanning is skipped entirely. Text-based keywords are still supported as a fallback for configs that do not include the `Handoff` plugin.

---

## SubAgent

Exposes two lightweight sub-agent tools that keep the caller's context window clean. Instead of reading many files directly, the parent agent delegates work to a focused loop that returns only a distilled result.

Both tools share the same tool set, timeout (8 minutes), and cancellation behaviour — the parent agent's cancellation token is linked so interrupts propagate immediately. The sub-agent's current working directory is automatically injected into its system prompt so it never wastes a tool call discovering it.

**Default tool set (read-only):** `read_file`, `list_files`, `grep_file`, `get_file_summary`, `get_file_info`, `search_content`, `search_symbol`, `shell_run`, `shell_get_env`, `shell_which`, `git_status`, `git_diff`, `git_log`, `git_show`, `git_branch_list`, `git_stash_list`. The sub-agent is instructed never to implement, edit, delete, commit, or push anything.

```yaml
Plugins:
  - SubAgent
```

| Tool | Parameters | Description |
|------|-----------|-------------|
| `sub_agent_explore` | `query`, `format` | Multi-hop exploration. Returns a prose summary (≤600 words) or a bulleted file list depending on `format`. Up to 20 iterations by default. |
| `sub_agent_locate` | `target` | Single-target lookup. Finds where a symbol, type, method, interface, or file is defined. Returns `path:line — description`. Capped at 5 iterations and 512 output tokens — much cheaper than explore for targeted lookups. |

**`format` values for `sub_agent_explore`:**

| Value | Output |
|-------|--------|
| `"prose"` (default) | Narrative summary, ≤600 words |
| `"file_list"` | Markdown bullet list of relevant file paths, each with a one-line role description |

**Tool selection inside the sub-agent loop (enforced by system prompt):**

> `search_symbol` → `list_files` → `search_content` → `get_file_summary` → `grep_file` → `read_file` → `shell_run`

The model is instructed to prefer earlier options when they suffice, reserving `read_file` for when a summary is insufficient and `shell_run` only for verifying a specific hypothesis (build, test) — not for browsing.

**Typical usage:**

```
# Broad question — use explore
sub_agent_explore("Which files in src/Parsing/ handle string interpolation and how do they interact?")

# Need a list of paths — use file_list format
sub_agent_explore("What files does AgentFactory depend on?", format="file_list")

# Single target — use locate (5 iterations, much cheaper)
sub_agent_locate("IOrchestrationHook")
sub_agent_locate("AgentFactory.Create")
sub_agent_locate("EventEmitter.cs")
```

### Sub-agent model override (`SubAgentModel`)

By default the sub-agent inherits the parent agent's model. Set `SubAgentModel` to run it on a cheaper model for cost control:

```yaml
Agents:
  - Name: Developer
    Model: claude-opus-4-7
    Plugins:
      - FileSystem
      - Shell
      - SubAgent
    SubAgentModel: claude-haiku-4-5-20251001   # exploration on a cheaper model
```

Any model alias or provider model ID accepted by the `Models` config section can be used here.

### Sub-agent iteration cap (`SubAgentMaxToolCalls`)

Controls the maximum number of tool-call iterations inside the `sub_agent_explore` loop. `sub_agent_locate` always uses a hard cap of 5.

```yaml
Agents:
  - Name: Archaeologist
    Plugins:
      - SubAgent
    SubAgentMaxToolCalls: 30   # allow deeper exploration for this agent
```

`0` (default) uses the built-in default of 20.

### Custom sub-agent plugin list (`SubAgentPlugins`)

To give the sub-agent a different set of plugins from the default, list them under `SubAgentPlugins`. Capability filters from `Capabilities` apply to sub-agent plugins the same way they apply to the parent. Unknown plugin names raise an error at session startup (they are not silently ignored).

```yaml
Agents:
  - Name: Developer
    Plugins:
      - FileSystem
      - Shell
      - SubAgent
    SubAgentPlugins:
      - FileSystem
      - Search
      - Git
    Capabilities:
      Git: [read]
```

**Note:** `SubAgent` is instantiated per-agent by `AgentFactory`. The stub registered in `PluginRegistry` is only used by `fuseraft plugins` for enumeration.

---

## Document

Read rich document formats as plain text. All operations are read-only. Sandbox rules apply when `FileSystemSandboxPath` is configured.

| Function | Parameters | Description |
|----------|-----------|-------------|
| `document_extract_text` | `path` | Extract full plain text from a PDF, DOCX, PPTX, or XLSX file. Returns a format/size header followed by the extracted text. |
| `document_get_info` | `path` | Return format metadata (page/sheet count, file size, extracted character count) without returning the full text. Cheaper than `extract_text` for planning. |
| `document_list_sheets` | `path` | List sheet names in an Excel file (`.xlsx` only). |
| `document_get_sheet` | `path`, `sheetName`, `maxRows` (default 0 = all) | Extract one sheet from an Excel file as a pipe-delimited text table. |

**Supported formats:** `.pdf`, `.docx`, `.pptx`, `.xlsx`

**Context store integration:** When you run `fuseraft context add` on a supported document, the text is automatically extracted and stored as a `.txt` file at import time. Agents can then access it via `read_file` without needing the `Document` plugin. Use `Document` when you need on-demand extraction inside a session (e.g. processing documents found during a task, or working with individual Excel sheets).

---

## Decision

Architecture Decision Registry (ADR) — record, search, and supersede architecture decisions across sessions. Each decision gets a stable ID (`ADR-NNNN`) and tracks title, context, rationale, alternatives, consequences, and tags.

| Function | Parameters | Description |
|----------|-----------|-------------|
| `decision_search` | `query` (default `""`), `status` (optional), `tag` (optional) | Search ADRs by keyword across title, context, decision text, and tags. Filter by status (`Proposed`, `Accepted`, `Deprecated`, `Superseded`) or tag. Leave `query` empty to list all. |
| `decision_read` | `id` | Fetch a single ADR by ID (e.g. `ADR-0042`). Returns full detail including alternatives, consequences, and governed files. |
| `decision_create` | `title`, `context`, `decision`, `alternatives` (optional), `consequences` (optional), `tags` (optional), `supersedes` (optional), `governs` (optional) | Record a new architecture decision. `alternatives` and `consequences` are comma-separated lists. `supersedes` is a comma-separated list of ADR IDs; those records are automatically marked Superseded. `governs` is a comma-separated list of file paths or symbol IDs the decision applies to. |
| `decision_supersede` | `id`, `newId` | Mark an existing ADR as Superseded. `newId` is the replacement ADR (recorded for traceability). |

---

## Graph

Read the repository semantic graph — nodes (files, packages/namespaces, types, methods, interfaces, ADRs) and edges (references, inheritance, implementation, dependencies), covering C#, Go, and Python source. The graph is populated automatically by the `search_symbol` and `search_callers` tools and by `decision_create`.

| Function | Parameters | Description |
|----------|-----------|-------------|
| `graph_search` | `query` (default `""`), `kind` (optional), `file` (optional) | Find graph nodes by name, kind, or file path. `kind` accepts `File`, `Namespace`, `Package`, `Type`, `Interface`, `Method`, `Property`, `Field`, or `Adr`. Returns up to 50 results. |
| `graph_refs` | `symbolId` | Find all nodes that reference, implement, or inherit from the given symbol ID (e.g. `type:fuseraft.Core.Models.AdrEntry`). Returns inbound `references`, `implements`, and `inherits` edges. |
| `graph_dependents` | `symbolId`, `depth` (default 3) | Transitively walk inbound `depends_on`, `references`, `implements`, and `inherits` edges up to `depth` hops (max 10). Shows every node that directly or indirectly depends on the target. |

---

## Objective

Long-horizon objective tracking — record multi-session goals, attach tasks, and track progress across orchestration runs.

| Function | Parameters | Description |
|----------|-----------|-------------|
| `objective_create` | `title`, `description` (optional), `tasks` (optional) | Create a new objective. `tasks` is a comma-separated list of remaining task descriptions. Returns the assigned ID (`OBJ-NNNN`). |
| `objective_read` | `id` | Fetch a single objective by ID with full detail: description, status, completed/remaining tasks, linked sessions, and timestamps. |
| `objective_update` | `id`, `title` (optional), `description` (optional), `status` (optional) | Update an objective's title, description, or status. `status` accepts `Active`, `Paused`, `Completed`, or `Abandoned`. |
| `objective_list` | `status` (optional) | List all objectives. Filter by status (`Active`, `Paused`, `Completed`, `Abandoned`). Shows title, status, and completion percentage. |
| `objective_link_task` | `id`, `task`, `completed` (default `true`), `sessionId` (optional) | Add a task to an objective or mark an existing task as completed. When `completed=false`, the task is added to the remaining list. Tracks the current session ID when provided. |

---

## SessionContext

Shared writable context summary for the current orchestration session. Agents write a plain-text summary before handing off; the successor reads it to catch up without re-reading every source file. The summary is stored at `~/.fuseraft/sessions/{project_slug}/{session_id}/context_summary.md` — each `session_context_write` call replaces the previous content so the file always reflects current state.

| Function | Parameters | Description |
|----------|-----------|-------------|
| `session_context_read` | — | Read the context summary written by the previous agent. Returns a truncation notice if the file exceeds 8,000 characters. Call this at the start of every turn before reading source files. |
| `session_context_write` | `summary` | Write or replace the session context summary. Overwrites any previous summary. Call this before every handoff. Bullet-point format works well — include what was accomplished, which files changed, and any open issues the next agent should know about. |

---

## Artifact

Fixed-target-path artifact writers for recon and planning-style agents (e.g. the `brownfield` template's Archaeologist, `greenfield`'s Preflight, `audit`'s Auditor and Prioritizer, `devops`'s OpsPlanner, `research`'s Researcher and Reviewer). Each registered name below is the same underlying class bound at construction to exactly one file path, one required format, and one uniquely-named write tool — there is no path parameter, so a call can never be redirected at the project's own source files the way `write_file`/`patch_file` can. Pair with `Capabilities: { FileSystem: [read] }` so the agent can examine the sandbox but can only persist findings through its one write tool.

Every instance validates `content` against its required format before writing (`json` parses with `System.Text.Json`, `yaml` with YamlDotNet, `md` has no required structure), and creates parent directories automatically.

| Plugin name | Tool | Format | Default path |
|-------------|------|--------|--------------|
| `Conventions` | `write_file_conventions` | json | `~/.fuseraft/sessions/{project_slug}/{session_id}/conventions.json` |
| `DiscoveryBrief` | `write_file_discovery_brief` | json | `~/.fuseraft/sessions/{project_slug}/{session_id}/brief.brownfield.json` |
| `Preflight` | `write_file_preflight` | json | `~/.fuseraft/sessions/{project_slug}/{session_id}/preflight.json` |
| `Brief` | `write_file_brief` | json | `~/.fuseraft/sessions/{project_slug}/{session_id}/brief.json` |
| `BriefReview` | `write_file_brief_review` | json | `~/.fuseraft/sessions/{project_slug}/{session_id}/brief-review.json` |
| `AuditFindings` | `write_file_audit_findings` | json | `.fuseraft/artifacts/audit-findings.json` (sandbox-relative) |
| `RemediationPlan` | `write_file_remediation_plan` | json | `.fuseraft/artifacts/remediation-plan.json` (sandbox-relative) |
| `OpsPlan` | `write_file_ops_plan` | yaml | `.fuseraft/artifacts/ops-plan.yaml` (sandbox-relative) |
| `ResearchFindings` | `write_file_research_findings` | md | `.fuseraft/docs/research-findings.md` (sandbox-relative) |
| `ResearchReview` | `write_file_research_review` | json | `.fuseraft/docs/research-review.json` (sandbox-relative) |

Each write tool takes `content` (full file content) and `format` (must be exactly `md`, `json`, or `yaml` — and must match the instance's required format above).

```yaml
Agents:
  - Name: Auditor
    Plugins:
      - FileSystem
      - Search
      - Shell
      - Investigation
      - AuditFindings
    Capabilities:
      FileSystem: [read]
```

**Note:** the `Conventions`/`DiscoveryBrief`/`Preflight`/`Brief`/`BriefReview` paths are session-scoped — one file per session, under the global `~/.fuseraft/sessions/` tree. The `AuditFindings`/`RemediationPlan`/`OpsPlan`/`ResearchFindings`/`ResearchReview` paths are fixed relative to the sandbox root (or the current directory when no `FileSystemSandboxPath` is set) — shared across sessions in the same project so a downstream agent's `read_file` call always finds them regardless of which session wrote them.

---

## Skills

Exposes installed skills as callable tools in the REPL. Only present when at least one skill is found at startup — see [Skills](skills.md) for how discovery works.

Unlike other plugins, Skills is not listed in an agent's `Plugins` config. It is registered automatically by the REPL based on what is installed on the filesystem.

| Function | Parameters | Description |
|----------|-----------|-------------|
| `load_skill` | `name` | Load the full `SKILL.md` body for a skill by slug. The model calls this when the catalog entry indicates the skill is relevant to the current task. |
| `run_skill_script` | `skill`, `script`, `args` (optional) | Run a script bundled with a skill. `script` is the filename inside the skill directory (e.g. `transform.py`). `args` is a space-separated argument string. Supported extensions: `.sh`, `.py`, `.js`. |

---

## MCP plugins

In addition to the built-in plugins above, tools from any connected MCP server are available as plugins. The plugin name is the `Name` field from `McpServers` config.

```yaml
McpServers:
  - Name: MyTools
    Transport: stdio
    Command: npx
    Args:
      - "-y"
      - my-mcp-server
Agents:
  - Name: Developer
    Plugins:
      - FileSystem
      - Shell
      - MyTools
```

See [MCP Integration](mcp.md) for full detail.
