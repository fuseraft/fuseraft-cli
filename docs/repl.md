# REPL

`fuseraft repl` (or just `fuseraft` with no subcommand) is a terminal chat session with a single model that can read and edit files, run shell commands, search your codebase, and use git — no config file required. It's the fastest way to get fuseraft doing real work; reach for [multi-agent orchestration](getting-started.md) (`fuseraft run`) once a task genuinely needs more than one agent working from a declarative plan.

This page is a guided walkthrough of what the REPL can do and how its pieces fit together. For the exhaustive flag-by-flag and command-by-command listing, see [CLI Reference — `fuseraft repl`](cli-reference.md#fuseraft-repl).

---

## Starting a session

```bash
fuseraft
# or, equivalently:
fuseraft repl
```

If `~/.fuseraft/config` doesn't exist yet, a setup wizard asks for a provider URL and API key and lets you pick a model from the live results — see [Getting Started](getting-started.md#set-your-api-key). After that, every future launch starts chatting immediately.

```bash
fuseraft --model claude-sonnet-4-6      # override the model for this session
fuseraft --resume a87569bc              # continue a previous session
fuseraft --no-tools                     # plain chat, no filesystem/shell/git/search
```

Every session gets an 8-character session ID (shown in the startup header) and auto-saves after every turn — closing the terminal never loses work. See [Sessions](sessions.md#repl-sessions) for resuming, forking, and rewinding.

---

## The prompt

```
1> your message here
```

The leading number is the turn count. When [safe mode](#the-safety-model) or [HITL](#the-safety-model) is active, the prompt grows a tag:

```
[hitl] 1> delete the build output and rerun the tests
```

After each response, a compact status line reports what happened:

```
  ── turn 1 · ~3,200 tok · 2 tools
```

The prompt supports history navigation (↑/↓), word-wise cursor movement, and standard readline-style kill/delete shortcuts with no external dependencies — see the full key table in [CLI Reference — Input and line editing](cli-reference.md#fuseraft-repl).

---

## What the model can do

Unless `--no-tools` is passed, the model gets a curated core toolset by default:

| Plugin | What it's for |
|--------|----------------|
| FileSystem | Read, write, patch, list, search files |
| Shell | Run commands and scripts, manage environment variables |
| Search | Search file content, find symbols, find callers |
| Git | Status, diff, log, add, commit |
| Todo | A self-directed checklist the model uses to plan and track multi-step work |
| SubAgent | `subagent_explore` / `subagent_locate` / `subagent_delegate` — the same tools behind [`/explore`, `/locate` and `/delegate`](#sub-agents-and-getting-unstuck), callable mid-turn; plus `subagent_run` when you've defined [custom sub-agents](subagents.md) |
| Session | Context-budget self-management (`compact_context`, `get_context_status`) |
| Skills | `load_skill` / `run_skill_script`, when [skills](#skills) are installed |

Rarer or destructive operations (`delete_file`, `git_push`, `shell_run_background`, ...) are opt-in via `--plugins Extended`, and `Http` and a few session-scoping plugins are opt-in the same way — kept out of the default set so every request's tool schema stays small. See [CLI Reference — Built-in tools](cli-reference.md#fuseraft-repl) for the full lists, or [Plugins](plugins.md) for what each tool actually does.

Run `/tools` at any time to see what's active right now.

---

## The safety model

Everything in this section is about controlling what the **model** can do on its own, unsupervised. Three independent layers, on by default:

**Sandboxing** — FileSystem, Shell, and Git are confined to the directory you launched from. A path outside it is rejected before anything else runs.

**HITL (human-in-the-loop)** — every shell command, every FileSystem write/delete, every Git write, and write-ish HTTP calls pause for a y/N approval:

```
[hitl] 2> delete the build artifacts and rerun the tests
⏸ Shell command requested:
  rm -rf dist/ && npm test
Allow? (y/N):  n
Command blocked.
```

Toggle it with `/hitl on` / `/hitl off`, or run `/hitl auto` to stop being asked about commands that are provably read-only (`ls`, `git status`, `grep`, … — see [Read-only auto-approval](cli-reference.md#read-only-auto-approval-hitl-auto)). Or skip it for the whole session with `--yolo` (which also drops the sandbox — full unattended access, for trusted use only).

Working across more than one project tree in a session? `--include <dir>` (repeatable) adds more allowed roots alongside the launch directory — shown in the banner as `Included:`. A path outside every allowed root isn't always a hard stop either: for the FileSystem read/write tools, a denied path offers a HITL prompt to grant it on the spot, and the grant covers the rest of the session. See [CLI Reference](cli-reference.md#fuseraft-repl) and [Security — Multi-root sessions](security.md#multi-root-sessions) for the full picture.

**Capability restriction** — `/safe-mode on` blocks Shell/Git/Http outright; `/tools restrict <plugin> <tag…>` is finer-grained (e.g. `/tools restrict Git read` removes `git_commit`/`git_push` from the model's tool schema entirely, while leaving `git_status`/`git_diff` available). Both also bind [sub-agents](subagents.md#safety): a delegated agent cannot use a tool the session has closed off.

None of this touches read-only tools (`read_file`, `git_status`, `http_get`, ...) — approval gates are for actions with side effects. See [CLI Reference — Shell/FileSystem/Git/Http write approval](cli-reference.md#fuseraft-repl) for prompts, defaults, and how restriction reaches across the `Extended` tool bucket.

---

## Running commands yourself: the `!` shell escape

The safety model above exists to gate what the *model* does unsupervised. It doesn't apply to you — prefix any line with `!` to run a real shell command directly, without leaving the REPL for another terminal:

```
1> !git status
On branch main
nothing to commit, working tree clean

1> !npm test
...(live output, streams as it runs)...
```

The child process inherits your terminal's stdio directly (no buffering, no redirection), so interactive programs — `less`, `vim`, `ssh`, an installer prompt — work exactly as they would in a real terminal, and Ctrl+C interrupts just that command without ending the session.

```
1> !cd src            # tracks a working directory that persists across ! commands
1> !pwd
/repo/src
1> !cd -              # back to the previous directory
1> !!                 # repeat the last ! command
```

Deliberately **not** sandboxed and **not** gated by `/hitl` — you typed it, not the model — and the command and its output are never added to conversation history, so the model never sees it. It's meant to feel like a second terminal, not a tool call. See [CLI Reference — Shell](cli-reference.md#fuseraft-repl) for the full command table.

---

## Skills

If [skills](skills.md) are installed, invoke one directly with `$<skill-name>`:

```
1> $commit
```

Tab-completes after typing `$`. The model can also load skills itself via the `load_skill` tool without you naming one — `$skill` is just the fast path when you already know which one you want.

---

## Deliberate multi-step work: `/plan` and `/execute`

`/plan <task>` asks the model to produce a structured plan — no tool calls yet — that you can review before anything runs. `/execute` then drives each step as its own turn, verifying postconditions (the expected tool was called, an expected file exists) and halting on failure instead of plowing ahead:

```
1> /plan create a Hello World C# console app in ./hello
  Plan (3 steps). Review, then run /execute.
  1. Create the project directory      tool: CreateDirectory
  2. Write Program.cs                  tool: WriteFile
  3. Write hello.csproj                tool: WriteFile

2> /execute
  ✓ Step 1 complete.  2 steps remaining.
  ...
```

When a step halts, `/resume` retries it as-is; `/recover` retries it with a context block telling the model what was expected versus what actually happened, so it can self-correct. See [CLI Reference — Plan / execute workflow](cli-reference.md#fuseraft-repl) for the full walkthrough, including recovery examples.

---

## Sub-agents and getting unstuck

`/explore <query>` and `/locate <symbol>` hand a read-only investigation off to an isolated sub-agent and return a prose summary or a `path:line` result — useful when you want an answer without polluting the main conversation with a long tool-call chain. `/delegate <task>` does the same for a self-contained subtask that needs to actually make changes (files, shell, git).

Need a specialist — a reviewer, a test writer, a docs agent — with its own instructions, tools and even model? Define it as a Markdown file in `.fuseraft/agents/` and the model can call it (or you can, with `/agent <name> <task>`). See [Sub-agents](subagents.md).

When a session has stalled — repeating a mistake, stuck in a loop, drifted off-task — `/assist` has a sub-agent read the whole conversation, diagnose the root cause, and inject a corrective message addressed to the main agent, so you don't have to.

### Working until it's really done: `/goal`

Ask for something with several parts and a turn can end with the agent confidently saying "done" while a part is missing. `/goal` adds an independent check:

```
1> /goal make every test in tests/ pass and add a Usage section to the README
```

The agent works on it as an ordinary turn. When it stops, a **second, tool-less model call** — which sees only the transcript, never the agent's own system prompt — audits it against the objective. It looks for evidence in what the tools actually returned (file contents, command output, test results); an agent merely *saying* "tests pass" does not count. If something is unverified, the agent is re-prompted with exactly what is missing, and the cycle repeats.

It ends in one of six ways:

| Outcome | Meaning |
|---------|---------|
| `✓ complete` | The audit found every requirement provably met. |
| `? paused` | The agent needs something only you can give (a decision, a credential, an approval you denied). Control returns to you instead of guessing. |
| `⚠ not verified` | The audit budget ran out (default 5, `--max N` up to 50). |
| `⚠ stalled` | The same work was reported missing three audits running — the agent is stuck, not progressing. |
| `⚠ interrupted` | You pressed Ctrl+C. |
| `⚠ audit could not run` | The provider failed during the audit. It never assumes success on a failed check. |

Everything except `complete` can be picked up with `/goal resume` (a fresh budget, same objective). `--max 8` sets the budget: `/goal --max 8 <objective>`. HITL, the sandbox and safe mode apply to every turn exactly as usual, and Ctrl+C stops the loop. Each audit is recorded as a `goal_audit` event.

Each round is a full agent turn plus one audit call, so a goal costs more than a single message — that is what it is for.

### Images

Attach a screenshot, diagram or photo to a message with `/image`, or mention it inline:

```
1> /image screenshot.png why is the Retry button misaligned?
  attached: screenshot.png (image/png, 240 KB)

2> compare @before.png with @"after v2.png" and list what changed
```

`/image <path>… [message]` attaches every leading path that names an image and sends the rest as the message (quote a path that contains spaces; with no message it asks the model to describe the image). An `@path` inside an ordinary message attaches the file **if it exists and is an image** — `@someone` or `@missing.png` are left alone as plain text. PNG, JPEG, GIF and WebP are supported; the file's *contents*, not its extension, decide (a text file renamed `.png` is refused with a reason). Up to 8 images per message, 20 MB each — providers cap lower (Anthropic: 5 MB), and a rejected message says so and suggests `/model`.

You need a **vision-capable model**. If the provider refuses a message with an image, the image is dropped from history (so it is not resent every turn) and you get a hint rather than a bare `400`.

Images are kept in context sparingly: only the **4 most recent** stay attached; older ones become a one-line text placeholder (`[image omitted from context: shot.png, image/png, 240 KB]`) so a long session does not resend — and pay for — every screenshot it ever saw. Each attached image counts as roughly 1,600 tokens toward the context budget. `/retry` resends the image with the message.

Images survive `--resume`: session files store a short reference and keep the bytes once, content-addressed, in `~/.fuseraft/repl-sessions/images/`. The event log records only *how many* images a turn carried — never their bytes. In the VS Code panel, paste a screenshot straight into the message box, or attach an image file with the paperclip.

### Loop and failure guards

A turn has no fixed cap on tool-call rounds — a long run of *successful* calls is fine — so the REPL watches for a turn that is going nowhere instead. Each guard first nudges the model inside the tool result it is about to read, then stops the turn if it carries on. Progress made so far is always kept; send a follow-up to try a different approach.

| Pattern | Nudge | Turn stops |
|---------|-------|-----------|
| The **same call** (same tool, same arguments) repeated back to back | on the 3rd | on the 5th |
| Two calls **alternating** — `read a` / `run tests` / `read a` / `run tests` … — with nothing changing in between | after 6 calls | after 10 calls |
| Consecutive tool **failures** (an `[ERROR]`, `[DENIED]`, non-zero exit, or thrown error) | on the 2nd | on the 3rd |

Any call with different arguments — a different `patch_file` body, a different path — breaks a repeat or alternation, so a genuine edit-and-verify cycle never trips them, and a single success resets the failure count. Each nudge fires once per streak. In `fuseraft run`, orchestration agents get the same repeat and alternation checks and record them as `tool_loop_warning` events (`kind`: `soft` or `hard`; `pattern`: `identical` or `alternating`).

---

## A second opinion: adversarial mode

`/adversarial on` adds a critic agent that reviews every `/execute` step and every free-form response, checking that claims are grounded in actual tool output rather than fabricated. A rejected `/execute` step halts like a postcondition failure would; a rejected free-form response gets one automatic correction turn. It costs an extra LLM call per turn, so it's off by default.

---

## Memory

The REPL keeps a persistent memory store, scoped to the working directory it was created in. At session start, relevant memories are injected into the system prompt; at session end, the model is asked to extract new ones automatically.

```
1> /memory
  [user_role] (user): Senior engineer working on fuseraft-cli
  [feedback_terse] (feedback): Prefers concise responses without trailing summaries

1> /memory save     # capture facts now, without waiting for exit
```

See [CLI Reference — Memory commands](cli-reference.md#fuseraft-repl) for scoping rules and file locations.

---

## Managing context

`/context` shows current token usage against the model's budget. `/compact` summarizes everything older than a recent verbatim tail into a handoff document and resets history on top of it — the same thing fires automatically once usage crosses 75% of budget, so a long session doesn't need to be babysat. Facts the model stated without a backing tool call are tombstoned as `[UNVERIFIED ASSUMPTION: ...]` during compaction rather than carried forward as established truth. See [CLI Reference — Compacting a session](cli-reference.md#fuseraft-repl) for the full mechanics.

---

## Sessions: resuming, forking, rewinding, undo

Every session auto-saves and can be resumed with `--resume <id>` or picked up mid-conversation with `/switch`. Resuming re-displays the last few turns so you can see where you left off (`/replay [n|all]` shows more, and `repl.resumeReplayTurns` sets the default; the VS Code panel shows them as ordinary chat messages). `/fork` branches the conversation off to a new session ID without disturbing the original — handy for trying two approaches from the same starting point. `/rewind` truncates conversation history to an earlier turn; `/undo` is its filesystem counterpart, reverting whatever files the most recent turn wrote, patched, moved, or deleted.

This is covered in full in [Sessions — REPL sessions](sessions.md#repl-sessions), including the session file format and how forking/rewinding interact.

---

## Connecting MCP servers

`/mcp add` walks through connecting a stdio or HTTP MCP server interactively; its tools become available to the model on the next turn and the connection persists across future sessions unless you pass `--session-only`. See [MCP Integration](mcp.md).

---

## VS Code

The [Fuseraft VS Code extension](https://github.com/fuseraft/fuseraft-vscode) hosts the same REPL in a chat panel over a JSON bridge protocol — same commands, same safety model, streamed into the editor instead of a terminal. (The `!` shell escape above is terminal-only for now — piping raw shell output into the webview's JSON stream would corrupt the protocol.)

---

## See also

- [CLI Reference — `fuseraft repl`](cli-reference.md#fuseraft-repl) — every flag, slash command, and prompt example
- [Getting Started](getting-started.md) — installation and first-time setup
- [Sessions](sessions.md) — resuming, forking, rewinding, session file format
- [Skills](skills.md) — installing and writing skill packages
- [Security & Sandbox](security.md) — API key storage, sandbox details
- [MCP Integration](mcp.md) — connecting external tool servers
