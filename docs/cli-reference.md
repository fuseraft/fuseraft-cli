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
| `--ci` | off | CI mode. After the session ends, reads `.fuseraft/test-report.json` and exits with code `2` if any criterion has `status: FAIL`. Exits `0` if the report is absent or all criteria pass. |
| `--devui` | off | Start a local web server and print a URL for real-time session visualization. See [DevUI](#devui) below. |
| `--work-dir <path>` | — | Set the working directory for the session. Priority: flag > `Security.FileSystemSandboxPath` in the config > current directory. |
| `--context-file <path>` | — | Attach a file as context. Its content is appended to the task. PDF, DOCX, PPTX, and XLSX files are extracted to plain text automatically; other files are read as UTF-8. Repeatable — specify once per file. Ignored when resuming. |
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
```

**Task input priority**

When multiple task inputs are provided, the following order applies:

1. **Session checkpoint** — when resuming, the original task is always used; `[task]`, `--task-file`, and `--context-file` are ignored with a warning
2. **`--task-file`** — if supplied, the file contents are used as the task
3. **`[task]`** — the positional argument
4. **Interactive prompt** — if nothing is supplied, you are asked to type a task
5. **Built-in demo** — if the prompt is left blank, a default demo task runs

The task file is read as plain UTF-8 text. Leading and trailing whitespace is trimmed. The file can contain any content — Markdown, plain prose, bullet lists, structured specs.

`--context-file` is a modifier on top of whatever task source is used: after the task text is resolved, each context file's content is appended as a fenced code block under an `--- Attached files:` section. PDF, DOCX, PPTX, and XLSX files are automatically extracted to plain text; all other files are appended as UTF-8. Files that cannot be found or read emit a warning and are skipped without aborting the run.

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

If an agent fails the same validator 3 consecutive times, the session pauses regardless of `--hitl` mode:

```
⚠ HITL intervention required.
  Agent:    Developer
  Blocked:  RequireWriteFile (3 consecutive failures)
  Last error: ...

Redirect Developer (Enter to abort session):
```

- **Any text** — inject a redirect message and restart the stream
- **Enter** — abort the session (checkpoint is saved for `--resume`)

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
| `-s, --system <prompt>` | — | System prompt. Defaults to a coding/research prompt when tools are enabled. |
| `--resume <id>` | — | Resume a previous REPL session by its session ID. Use `/sessions` inside the REPL to list resumable sessions. |
| `--no-banner` | off | Skip the ASCII banner. |
| `--no-tools` | off | Disable all built-in tools and start a plain chat session. |
| `--verbose` | off | Enable debug logging: prints per-turn detail (token estimate, tool-round count, total tool calls) and shows the event log path at startup. |
| `--vscode` | off | VS Code mode. Reads the API key from the `FUSERAFT_API_KEY` environment variable (injected by the fuseraft VS Code extension) instead of the OS keychain. Automatically passed by the extension — not intended for manual use. |

**Startup display**

On launch a compact header shows the model name, a single info line listing active tool categories, loaded context (agents/memory/skills), and available sub-agent commands, and the session ID:

```
── claude-sonnet-4-6 ─────────────────────────────────────
  FileSystem  Shell  Search  Git  Http  ·  memory  ·  3 skills  ·  /help
  session: a87569bcd7b0
```

The session ID is shown on every startup so you can note it down for later resumption with `--resume`. The event log path is only shown with `--verbose`.

**First-time setup**

If `~/.fuseraft/config` is missing or incomplete, `fuseraft repl` runs an interactive setup wizard before starting the session. It prompts for a model ID, provider URL, and API key. Settings are saved after the first successful reply — the config file stores model and endpoint only; the API key goes into the OS keychain. Use `/provider setup` to reconfigure at any time.

**Custom and enterprise providers** — the wizard accepts any OpenAI-compatible endpoint. Supply the full base URL (e.g. `https://chat.mycompany.com/openai/`) and any model ID recognised by that endpoint, including non-standard formats such as AWS Bedrock deployment IDs (`anthropic.claude-sonnet-4-5-20250929-v1:0`). When both a custom endpoint and an API key are provided, auto-detection is skipped entirely and the endpoint is treated as OpenAI-compatible.

See [Getting Started — Set your API key](getting-started.md#set-your-api-key) and [Security — API key storage](security.md#api-key-storage) for more detail.

**Model resolution order**

1. `--model` flag (if passed)
2. `modelId` in `~/.fuseraft/config` (if the config is complete)
3. First provider with an API key in the environment (fallback auto-detection):

| Environment variable | Default model |
|---------------------|---------------|
| `ANTHROPIC_API_KEY` | `claude-sonnet-4-5` |
| `OPENAI_API_KEY` | `gpt-4o-mini` |
| `XAI_API_KEY` | `grok-4-1-fast-reasoning` |
| `GOOGLE_AI_API_KEY` | `gemini-2.0-flash` |
| `MISTRAL_API_KEY` | `mistral-small-latest` |
| `DEEPSEEK_API_KEY` | `deepseek-chat` |

**Built-in tools**

Unless `--no-tools` is passed, the REPL gives the model access to:

| Plugin | Tools |
|--------|-------|
| FileSystem | `read_file`, `write_file`, `list_files`, `delete_file` |
| Shell | `shell_run`, `shell_run_script`, `shell_get_env`, `shell_which`, `shell_get_working_directory`, `shell_get_session_temp_dir` |
| Search | `search_files`, `search_content`, `search_symbol` |
| Git | `git_status`, `git_diff`, `git_log`, `git_commit`, and more |
| Http | `http_get`, `http_post` |
| Skills | `load_skill`, `run_skill_script` (only when skills are installed — see [Skills](skills.md)) |

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
| `/clear` | Clear conversation history (system prompt is kept) |
| `/compact` | Ask the model to summarise the session into a handoff document, then replace history with that summary. The system prompt and tools/skills catalog are kept; everything else is discarded. Use this when context is filling up but you want to continue in the same session. |
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
| `/context` | Show estimated context window usage: token count vs. budget, explicit budget label, completed turn count, per-role message counts, per-category breakdown, delta since last check, and projected turns remaining after 2+ turns |
| `/events` | Show event stats for the current session: turns, total tool calls, per-turn tool breakdown, and top tools by frequency |
| `/events stats` | Same as `/events` |
| `/explore <query>` | Run a sub-agent exploration loop over the codebase and return a prose summary. The sub-agent uses read-only tools and runs in an isolated context with no shared history from the main session. |
| `/locate <symbol>` | Run a sub-agent symbol lookup and return a `path:line` result. Faster and more targeted than `/explore` for single-symbol lookups. |
| `/safe-mode` | Show current safe mode status |
| `/safe-mode on` | Disable Shell, Git, and Http tool categories to prevent mutations |
| `/safe-mode off` | Restore tool categories to their state before safe mode was enabled |
| `/adversarial` | Show adversarial mode status |
| `/adversarial on` | Enable a critic agent that reviews each `/execute` step after postconditions pass. The critic judges whether the step was completed correctly and halts the plan if it disagrees. |
| `/adversarial off` | Disable the critic agent |
| `/provider` | Show the current model, endpoint, and API key store |
| `/provider setup` | Reconfigure provider URL, model ID, and API key; saves immediately |
| `/max-tokens <n>` | Cap the model's output to `n` tokens per response |
| `/max-tokens reset` | Restore the provider's default max output tokens |
| `/exit` | End the session |

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

**Adversarial mode**

Enable adversarial mode with `/adversarial on` to add a critic agent as an extra gate on each `/execute` step. After the deterministic postcondition check passes (tool called, file created), the critic receives an isolated view of the step — its description, the tools called, and the agent's response — and judges whether the step was actually completed correctly.

If the critic approves, execution continues. If it rejects, the plan halts just as a postcondition failure would, with the critic's reason stored as a recovery hint. Running `/recover` then injects that reason into the retry prompt so the agent knows exactly what the critic found wrong.

```
> /adversarial on
  Adversarial mode on: critic agent will review each /execute step.

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

The critic runs in an isolated context with no shared history from the main session — the same sub-agent infrastructure used by `/explore` and `/locate`. It requires tools to be active; `/adversarial on` will warn if `--no-tools` was set at startup. On timeout or error the critic degrades to approved so a transient failure never blocks execution.

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

**Memory commands**

The REPL automatically maintains a persistent memory store at `~/.fuseraft/memory/repl/`. Each entry is identified by a UUID and stored as `memory_{guid}.md`. Memories are **scoped to the working directory** where they were created:

- If the current directory contains a `.fuseraft/` folder, the REPL loads only memories whose GUIDs are listed in `.fuseraft/memory_refs.json`. Directories with a `.fuseraft/` folder but no refs file start with an empty memory set (no cross-project bleed).
- Directories without a `.fuseraft/` folder fall back to loading all global memories (legacy behaviour, useful outside of a project context).

When a memory is saved, the REPL writes the entry to the global store and registers its GUID in `.fuseraft/memory_refs.json` for the current directory. Repeated saves of the same-named memory reuse the existing GUID, so the entry is updated in-place rather than duplicated.

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

**Compacting a session**

As a conversation grows, token usage climbs and the model's effective context window shrinks. Use `/compact` to reset history without losing continuity:

1. The model summarises the entire conversation into a handoff document — what was being worked on, key decisions, current state, and what comes next.
2. The full history is discarded and replaced with that single summary message. The system prompt, tools, and skills catalog are kept intact.
3. The session continues as if it had just started, but with the summary as its opening context.

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

Every session appends structured JSONL events to `.fuseraft/repl_events.jsonl` in the current working directory (created automatically). Events include `session_start`, `user_input`, `tool_call`, `assistant_response`, `command`, and `session_end`, each stamped with a UTC timestamp and session ID. Use `/events` to view a summary of the current session without leaving the REPL.

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

**Examples**

```bash
# List incomplete sessions
fuseraft sessions

# List all sessions including completed
fuseraft sessions --all

# Delete a specific session
fuseraft sessions --delete a3f92c1d

# Purge all completed sessions
fuseraft sessions --delete all
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
10. Termination strategy type is `regex`, `maxiterations`, or `composite`
11. Regex termination: `Pattern` is non-empty
12. Agent names referenced in termination strategies exist in the agents list
13. If `Telemetry` is set: `OtlpEndpoint` is a valid absolute URI
14. With `--strict`: every plugin name in any agent's `Plugins` list is registered
15. For every `ApiKeyEnvVar` referenced: the environment variable is set in the current shell (warning if missing). Note: agents that rely on the OS keychain rather than an env var skip this check — keychain auth is verified only when `--check-connectivity` is used.

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

**Templates**

| Template | Description |
|----------|-------------|
| `dev-team` | Five-agent pipeline: Planner → Developer → Tester → Reviewer with keyword routing, plus a periodic Verifier that audits the evidence graph for inconsistencies |
| `research` | Two-agent pipeline: Researcher gathers information, Writer produces the final report |
| `devops` | Three-agent pipeline for infrastructure and deployment tasks |
| `content` | Two-agent pipeline: Writer drafts, Editor refines and approves |
| `minimal` | Single general-purpose agent for simple tasks |
| `brownfield` | Four-agent pipeline: Archaeologist recons the codebase, Planner designs the change, Developer implements with change-envelope enforcement, Reviewer inspects by code review |
| `magentic` | Magentic-managed team: a manager LLM plans and coordinates Researcher + Developer agents dynamically |
| `designer` | Single-agent orchestration that designs, writes, and validates fuseraft configs interactively — describe your use case in plain language and get a ready-to-run YAML config back |
| `graph` | Planner → Developer → Tester → Reviewer as a declarative directed graph; forward edges advance the phase, back-edges (REVISION REQUIRED, BUGS FOUND, REPLAN REQUIRED) restart from the target node |
| `brownfield-graph` | Brownfield codebase pipeline as a directed graph; Archaeologist → Planner → Developer → Reviewer/approved; the Reviewer has two distinct back-edges — REVISION REQUIRED routes to Developer and REPLAN REQUIRED routes to Planner |

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

# Non-interactive with explicit template and model
fuseraft init --template dev-team --model claude-sonnet-4-6
fuseraft init --template minimal --no-interactive

# Brownfield codebase — Archaeologist recons first, then plan → implement → review
fuseraft init --template brownfield
fuseraft init --template brownfield --model claude-sonnet-4-6 --endpoint https://api.anthropic.com

# Generate a Magentic team config
fuseraft init --template magentic
fuseraft init .fuseraft/config/magentic-team.yaml --template magentic --model gpt-4o

# Generate an Orchestration Designer — describe your use case, get a validated config back
fuseraft init --template designer
fuseraft init .fuseraft/config/designer.yaml --template designer --model claude-sonnet-4-6

# Graph pipeline — explicit directed-graph topology with forward edges and back-edges
fuseraft init --template graph
fuseraft init .fuseraft/config/graph-team.yaml --template graph --model claude-sonnet-4-6

# Brownfield graph — Archaeologist → Planner → Developer → Reviewer/approved with multi-target back-edges
fuseraft init --template brownfield-graph
fuseraft init .fuseraft/config/brownfield-graph.yaml --template brownfield-graph --model claude-sonnet-4-6

# CI / scripted usage
fuseraft init .fuseraft/config/ci-team.yaml --template dev-team --model gpt-4o --no-interactive
```

After generating, `init` prints the next steps:

```
Review:   fuseraft config .fuseraft/config/orchestration.yaml
Validate: fuseraft validate .fuseraft/config/orchestration.yaml
Run:      fuseraft run --config .fuseraft/config/orchestration.yaml "Your task"
```

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

Install, list, and remove global skills available to all agent sessions. Skills are stored in `~/.fuseraft/skills/` and registered in an FTS5 search index so fuseraft can automatically identify which ones are relevant to a given task.

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
fuseraft skills add ../skills/productivity/handoff

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

Displays a table with the slug and description for each skill found under `~/.fuseraft/skills/`.

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
fuseraft skills remove handoff
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
