---
name: repl-tmux-driver
description: Drive an interactive `fuseraft repl` session from outside via tmux - inject single- or multi-line input, poll for the agent to finish working, and capture output for review. Trigger when the user wants to live-test a REPL change, dogfood the REPL agent on a real task, or verify /safe-mode, /tools, /hitl, or other REPL command behavior against a real model rather than only unit tests.
compatibility: Requires tmux and a built fuseraft binary (./build.sh --target=Build)
---

# REPL Tmux Driver

Drive `fuseraft repl` as an interactive subprocess through tmux so you can feed it a real task, watch it use real tools against a real model, and verify the result - rather than relying on unit tests alone.

## When to Use

Use this skill when:
- Live-testing a REPL behavior change (a new `/command`, a tool-gating fix, a prompt change) against a real model, not just `ReplCommands.HandleAsync` unit tests
- Dogfooding: having the REPL agent itself perform a real task on this repo, then reviewing its diff
- Reproducing a REPL bug report interactively to confirm root cause or confirm a fix
- Verifying `/tools`, `/safe-mode`, `/hitl`, `/adversarial`, or similar mode toggles actually change agent-visible tool surface, not just internal state

Do **not** use this skill for:
- Testing orchestration (`fuseraft run`) sessions - those are non-interactive and can be scripted directly, no tmux needed
- Anything a plain unit test in `tests/FuseraftCli.Tests` already covers - reach for tmux only when a real model round-trip matters
- The VS Code webview bridge (`--vscode`) - that speaks a JSON protocol over stdio, not the human-readable prompt this skill polls for

## Workflow

### Step 1: Build and confirm the model is reachable

```bash
./build.sh --target=Build
./src/bin/Release/net10.0/fuseraft models   # confirms provider/API key work; marks "<model> <- current"
```

Always rebuild before a live test of a source change - a stale binary silently tests the old behavior.

### Step 2: Launch the REPL in a dedicated tmux session

```bash
tmux kill-session -t <name> 2>/dev/null   # safe no-op if it doesn't already exist
tmux new-session -d -s <name> -x 220 -y 50 -c <repo-root>
tmux send-keys -t <name> "./src/bin/Release/net10.0/fuseraft repl [--plugins Extended] [--model ...]" Enter
```

Wait a few seconds, then confirm the banner and prompt appeared:

```bash
tmux capture-pane -t <name> -p | tail -20
```

Pick flags to match what's under test - e.g. `--plugins Extended` to exercise the Extended tool bucket, `--no-tools` for a prompt-only session, `--resume <id>` to continue a prior one.

### Step 3: Send input

**Single-line message:** send it directly.

```bash
tmux send-keys -t <name> "your message here" Enter
```

**Multi-line / multi-paragraph task:** do not pass a string containing newlines straight to `send-keys` - each embedded newline submits early as its own command. Instead write the task to a file and use the REPL's `/paste` mode with `tmux load-buffer`/`paste-buffer`:

```bash
tmux send-keys -t <name> "/paste" Enter
tmux load-buffer -b task_buf /path/to/task.txt
tmux paste-buffer -b task_buf -t <name>
tmux send-keys -t <name> Enter
tmux send-keys -t <name> ".done" Enter
```

Write the task file with enough context that the agent doesn't have to guess: name the relevant source files and any existing pattern to follow, state the constraints, and ask it to build and run the test suite before reporting back. Describe the problem and point at precedent - don't hand it a finished diff to transcribe; a well-scoped real task is what makes this a genuine test of the agent, not a typing exercise.

### Step 4: Wait for it to finish - don't blind-sleep

The REPL shows a spinner (`thinking...`, or `<verb>...  <tool_name> (Ns)`) while working and drops back to a bare numbered prompt (`1>`, or `[safe] 1>` under safe mode) once idle. Poll for that state instead of guessing a sleep duration. Use a proper wait primitive (a Monitor until-loop, or a backgrounded `until` loop) rather than a chain of blind `sleep`s:

```bash
until tmux capture-pane -t <name> -p | tail -6 | grep -qE '^(\[[a-z-]+\] )?[0-9]+> *$'; do
  sleep 5
done
```

Size the timeout to the task - a multi-file fix plus a full test run can take several minutes.

### Step 5: Capture and review the result

`tmux capture-pane -p` piped straight into some shell tools can come back looking empty (control-character noise trips naive output handling). Redirect to a file and read that instead of relying on inline capture:

```bash
tmux capture-pane -t <name> -p -S -400 > /path/to/scratch/output.txt
```

Then read the file directly. Treat the agent's own summary as a claim, not a fact, and verify independently:
- `git diff` (not just `--stat`) for every file it touched
- Rebuild and rerun the real test suite yourself: `./build.sh --target=Build && ./build.sh --target=Test`
- If the change affects REPL-visible behavior, drive a **second**, fresh tmux session by hand to exercise the exact before/after (e.g. run `/tools` before and after toggling the mode that changed) rather than trusting that unit tests alone prove the live behavior

### Step 6: Clean up

End the session with `/exit` rather than just killing the pane, so session-end bookkeeping (memory extraction, final event log flush) runs:

```bash
tmux send-keys -t <name> "/exit" Enter
sleep 2
tmux kill-session -t <name> 2>/dev/null
```

Note the "Resume with: fuseraft --resume <id>" line if the same session might need to continue later.

## Gotchas

- **Stale binary.** Rebuild before every live-test session - a REPL launched from an old binary silently tests old behavior and any "fix confirmed" result is worthless.
- **`tmux capture-pane` looking empty.** Redirect to a file and read the file rather than trusting a tool's inline stdout capture of the raw pane dump.
- **Sandboxed tmux instability.** In some sandboxed environments a long-lived tmux pane's underlying process can be silently killed and restarted, which looks identical to an application crash or an unexpected `/clear`. If a session seems to have reset without explanation, check the pane's shell PID before concluding it's a fuseraft bug.
- **Don't chain blind sleeps to poll.** Prefer an until-loop that checks the actual prompt state over guessing durations - guesses are either too short (you read a mid-turn state) or too long (you waste the wait).
- **`/paste` needs the literal `.done`** on its own line (or Ctrl+D) to exit paste mode - a plain trailing newline is not enough and leaves the REPL waiting for more input.

## References

- Full REPL command reference: `docs/cli-reference.md`
- Live list of REPL commands: run `/help` inside the session
