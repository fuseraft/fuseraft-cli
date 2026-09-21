# Subagents

A subagent is a focused assistant with its own system prompt, tool set and (optionally) model that the main REPL agent — or you — can hand a self-contained task to. It works in an isolated context, then reports back a summary, so a long tool-call chain never lands in your main conversation.

fuseraft ships three built-in subagents (`/explore`, `/locate`, `/delegate`). This page is about the fourth kind: **agents you define yourself**, as Markdown files.

---

## Defining an agent

Create a `.md` file in one of these directories. Each file is one agent.

| Scope | Path |
|-------|------|
| Project (fuseraft) | `<project>/.fuseraft/agents/` |
| Project (shared) | `<project>/.agents/agents/` |
| User (fuseraft) | `~/.fuseraft/agents/` |
| User (shared) | `~/.agents/agents/` |

Earlier entries win when two files declare the same name; the ignored file is reported by `/agents`. Only top-level `*.md` files are read (`README.md` is skipped, subdirectories are not scanned). Use `.agents/agents/` for an agent you want other Agent-Skills-compatible tools to find too.

```markdown
---
name: reviewer
description: Read-only code reviewer. Use to inspect files for bugs and edge cases and get a short findings list with file:line references. Never modifies files.
max_iterations: 12
---
You are a meticulous senior code reviewer. Read the files you are asked about, look for
correctness bugs and unhandled edge cases, and report a numbered findings list. Each
finding must cite `file:line`. If you find nothing wrong, say exactly "No findings."
```

The YAML frontmatter says *when* to use the agent and *what it may touch*; the Markdown body is its system prompt. fuseraft appends a short runtime footer (working directory, the tools it actually has, "report back concisely").

### Frontmatter

| Field | Required | Meaning |
|-------|----------|---------|
| `name` | no | Lowercase letters, digits, `-` or `_` (max 64). Defaults to the file name. This is what the model and `/agent` call it by. |
| `description` | **yes** | What the agent is for. This is the *only* thing the calling model sees when deciding whether to use it — write it like a trigger condition. |
| `model` | no | A model id to run this agent on, e.g. a cheaper one for routine work. Omitted: the session's subagent model (`subagent.model`, else the main model). |
| `tools` | no | Which tools it may use — see below. |
| `max_iterations` | no | Cap on rounds per run (1–100, default 30; a round is one model call, and tools it fires in parallel count once). A run that hits it reports `stopped after N rounds without finishing` (plus any partial output) instead of passing off unfinished work as an answer, and its `subagent_end` event has `outcome: iteration_limit`. |

Field names are case-insensitive. Fields fuseraft does not implement (for example OpenHands' `permission_mode`, `hooks`, `skills`) are **reported, not silently ignored** — `/agents` lists them so a setting that looks like it is protecting you but is not never goes unnoticed. `color` is accepted for cross-tool compatibility and has no effect.

### Choosing tools

| `tools:` | The agent gets |
|----------|----------------|
| *(omitted)* | The **read-only** set: file reads, search, `git_status`/`git_diff`/`git_log`/`git_show`. **No shell** — `shell_run` can change things, so a "read-only" agent must not have it by default. |
| `[read_file, write_file, patch_file]` | Exactly those tools (comma-separated text works too). |
| `['*']` | Everything the built-in `/delegate` subagent has: files, shell, git. |
| `[]` (or `tools:` with no value) | No tools at all — a pure reasoning agent that works from the task text. |

A tool name that does not exist in your session is dropped and reported. An agent can never receive the subagent tools themselves, so **agents cannot spawn agents**.

---

## Using your agents

**The model calls them itself.** When at least one agent is defined, the model gets a `subagent_run` tool whose description lists every agent and its `description`. Ask naturally — *"have your doc-writer agent add docstrings to `textstats.py`"* — or just describe work an agent's description fits. With no agents defined, the tool does not exist and costs nothing.

**You can run one directly:**

```
1> /agents
╭────────────┬─────────┬───────────┬───────────────────────────────────┬───────────────────╮
│ agent      │ scope   │ model     │ tools                             │ description       │
├────────────┼─────────┼───────────┼───────────────────────────────────┼───────────────────┤
│ doc-writer │ project │ [default] │ read_file, write_file, patch_file │ Writes docstrings…│
│ reviewer   │ project │ [default] │ read-only (17)                    │ Read-only code re…│
╰────────────┴─────────┴───────────┴───────────────────────────────────┴───────────────────╯

2> /agent reviewer review textstats.py
```

`/agents` also prints every problem found while loading — a file with no frontmatter, a missing `description`, an unknown tool, a model that could not be created (the agent then falls back to the session's subagent model). Definitions are read once at REPL start; restart to pick up edits.

---

## Safety

A custom agent is **not** a way around the session's controls — it runs on the same tool instances the main agent uses:

- **Sandbox, deny rules and HITL still apply.** A write or shell command from inside an agent shows the same y/N prompt (with the diff, for file edits) as one from the main agent.
- **`/safe-mode` and `/tools restrict` apply to subagents too.** With `/safe-mode on`, an agent that lists `shell_run` simply does not have it that run. (This also closes a gap in the built-in `/delegate`, which used to keep shell and git under safe mode.) The gate is checked at run time, so toggling safe mode mid-session takes effect on the next run.
- **Agent files are code-adjacent.** They choose a system prompt and a tool set, and travel with the repository. Treat `.fuseraft/agents/` and `.agents/agents/` like a `Makefile` — only run fuseraft in directories you trust. See [Security — Skills execution trust model](security.md#skills-execution-trust-model), which applies equally.

Every run emits `subagent_start` / `subagent_tool_call` / `subagent_end` events with `mode: "agent:<name>"`.

---

## Scope

User-defined agents are a REPL feature. `fuseraft run` orchestration configs keep their own `Subagent` plugin (see [Plugins](plugins.md)); they do not read these files.
