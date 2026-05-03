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
| `list_files` | `directory`, `pattern` (default `"*"`) | List files matching a glob pattern. Returns up to 500 results. |
| `get_file_info` | `path` | Returns metadata: type (file/directory), size, created/modified timestamps, and Unix permissions (on Unix systems). |
| `stat_file` | `path` | Return version, size, and last-modified for a file. Version is a monotonic counter incremented on every `write_file` call. Returns `version=NOT_TRACKED` when the file exists but was never written through `write_file`. Cheaper than `read_file` for conflict detection. |
| `write_file` | `path`, `content`, `raw` (default false), `baseVersion` (default 0) | Create or overwrite a file. Creates parent directories automatically. When `baseVersion > 0`, the write is rejected with `VERSION_MISMATCH` if the current stored version differs — use `stat_file` first to read the version and detect concurrent writes. |
| `patch_file` | `path`, `oldText`, `newText` | Replace an exact block of text in a file. Fails with a hint if `oldText` is not found verbatim. |
| `delete_file` | `path` | Delete a file if it exists. |
| `set_permissions` | `path`, `mode` | Set Unix file permissions (chmod). Accepts a 3- or 4-digit octal string such as `"755"` or `"0644"`. No-op on Windows. |
| `create_directory` | `path` | Create a directory and all intermediate parent directories. Safe to call when the directory already exists. |
| `delete_directory` | `path`, `recursive` (default false) | Delete a directory. Set `recursive=true` to remove a non-empty directory and all its contents. Refuses to delete the sandbox root. |
| `copy_file` | `source`, `destination`, `overwrite` (default false) | Copy a file to a new location. Creates the destination directory if needed. |
| `move_file` | `source`, `destination`, `overwrite` (default false) | Move or rename a file or directory. Creates the destination parent directory if needed. |
| `path_exists` | `path` | Check whether a file or directory exists at the given path. |
| `list_directory` | `directory`, `pattern` (default `"*"`) | List files and subdirectories in a single directory (non-recursive). Subdirectories are shown with a trailing `/`. Returns up to 500 entries. |

**Tip:** When `FileSystemSandboxPath` is configured, all paths are resolved to canonical form and rejected if they fall outside the sandbox root. See [Security](security.md).

---

## Shell

Execute shell commands and scripts.

| Function | Parameters | Description |
|----------|-----------|-------------|
| `shell_run` | `command`, `workingDirectory` (optional), `timeoutSeconds` (default 60) | Run a shell command. Supports pipes, redirects, and chained commands. Captures stdout, stderr, and exit code. |
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

The shell used is `/bin/bash` on Unix and `cmd` on Windows. The shell binary is located from common system paths at startup.

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
# config/orchestration.yaml snippet
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

Search the filesystem by name or content.

| Function | Parameters | Description |
|----------|-----------|-------------|
| `search_files` | `pattern`, `directory` (default `"."`), `maxResults` (default 100) | Find files by wildcard/glob pattern. |
| `search_content` | `query`, `directory` (default `"."`), `filePattern` (default `"*"`), `maxResults` (default 100), `caseSensitive` (default false) | Search file contents by regex or plain text (like grep). |
| `search_symbol` | `symbol`, `directory` (default `"."`), `extension` (default `""`), `maxResults` (default 50) | Find symbol definitions (class, function, interface, variable, etc.) using language-agnostic patterns. |

---

## Probe

Structured hypothesis testing and assertion utilities. Useful for Tester agents that need to verify behavior with evidence.

| Function | Parameters | Description |
|----------|-----------|-------------|
| `probe_code` | `language`, `code`, `directory` (default `"."`), `timeoutSeconds` (default 30) | Execute a code snippet and return stdout/stderr/exit code. Supported languages: `bash`, `python`, `javascript`, `go`, `csharp`, `powershell`, `kiwi`. |
| `probe_assert_output` | `command`, `expected`, `matchType` (default `"contains"`), `directory`, `timeoutSeconds` | Run a command and assert its output. `matchType`: `contains`, `equals`, `regex`, `exitcode`. Returns PASS or FAIL with evidence. |
| `probe_compare_outputs` | `commandA`, `commandB`, `directory`, `timeoutSeconds` | Run two commands and return their outputs side-by-side for comparison. |
| `probe_run_hypothesis` | `hypothesis`, `command`, `expectedObservation`, `setupCommand` (optional), `directory`, `timeoutSeconds` | Given/When/Then structured test. |

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

Delegates codebase exploration to a focused sub-agent that runs its own contained tool loop and returns a concise prose summary. Use this instead of calling `read_file` on many files directly — the caller's context window never sees raw file contents, only the distilled answer.

The sub-agent times out after 8 minutes and caps its response at 2048 tokens.

**Default tool set:** `read_file`, `list_files`, `grep_file`, `get_file_summary`, `get_file_info`, `search_files`, `search_content`, `search_symbol`, `shell_run`, `shell_get_env`, `shell_which`, `shell_get_working_directory`, `git_status`, `git_diff`, `git_log`, `git_show`, `git_branch_list`. The sub-agent is instructed not to implement, edit, delete, commit, or push anything.

```yaml
Plugins:
  - SubAgent
```

| Function | Parameters | Description |
|----------|-----------|-------------|
| `ExploreAsync` | `query` | Ask the explorer a specific question about the codebase. Returns a prose summary under 600 words. |

**Typical usage:**

```
Before implementing a change:
  ExploreAsync("Which files in src/Parsing/ handle string interpolation and how do they interact?")

When you need to verify current build state before reporting:
  ExploreAsync("Does the project build cleanly? If not, what are the errors?")
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

### Custom sub-agent plugin list (`SubAgentPlugins`)

To give the sub-agent a different set of plugins from the default, list them under `SubAgentPlugins`. Capability filters from `Capabilities` apply to sub-agent plugins the same way they apply to the parent.

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
