# Serve (Daemon Mode)

`fuseraft run` builds an agent team, drives one task to completion, and exits. `fuseraft repl` does the same but interactively. `fuseraft serve` is the third shape: it builds the agent team **once** and stays running, waiting to be dispatched into by a human or another agent, instead of exiting after a single task. It's what makes a fuseraft agent team a resident, addressable participant rather than something you have to re-launch every time there's work.

This page is a guided walkthrough of how the daemon works. For the exhaustive flag-by-flag and MCP-tool listing, see [CLI Reference — `fuseraft serve`](cli-reference.md#fuseraft-serve) and [`fuseraft attach`](cli-reference.md#fuseraft-attach).

---

## Starting a daemon

```bash
fuseraft serve
```

```
fuseraft serve — MyProject
  MCP (dispatch_task/get_status/get_result) → http://localhost:24601/mcp
  Attach socket → /home/user/.fuseraft/run/1f92641edb0442bd.sock  (fuseraft attach)
  Unattended mutating actions: denied (--unattended-policy)
  Auto-dispatch: off (--auto-objective)
  Idle — waiting for a task. Ctrl+C to stop.
```

The MCP port, like the attach socket path, defaults to one deterministically derived from the project's directory (`--http-port` to pin a specific one instead) — so two daemons for two different projects can both be started with no flags and never collide on the same default port.

One daemon serves one working directory and one orchestration config for its whole lifetime — the agent team, MCP server connections, and governance state (circuit breaker, audit chain) are all built once at startup and reused for every task, rather than rebuilt per dispatch. A pidfile enforces one daemon per project: a second `fuseraft serve` in the same directory refuses to start while the first is alive. `Ctrl+C` shuts it down gracefully, finishing any in-flight task first.

Tasks are processed **one at a time** — every fuseraft orchestrator assumes a single shared conversation history, so there's no safe way to run two dispatched tasks concurrently against the same agent team. Blocking with nothing to do *is* the idle mode; a second dispatch while one is running simply queues behind it (see [Queue position](#queue-position)).

---

## Two ways in: MCP and attach

Every dispatched task — however it arrives — funnels through the same queue, the same approval gate, and the same checkpointing. The two front doors differ only in who's on the other end.

### Another agent, over MCP

`fuseraft serve` hosts an MCP server at `/mcp` (streamable HTTP, localhost-only) exposing three tools. Any MCP client — another fuseraft instance, Claude Code, anything that speaks the protocol — can call them directly:

```
dispatch_task(task: "Add input validation to the signup form")
# → { "sessionId": "58440fde", "status": "queued", "position": 0 }

get_status(sessionId: "58440fde")
# → { "status": "running", "turnCount": 2, "lastAgent": "Developer" }

get_result(sessionId: "58440fde")
# → { "status": "completed", "succeeded": true, "messages": [...] }
```

Dispatch is fire-and-forget by design: a task can take minutes, so `dispatch_task` returns immediately with a session ID and you poll for the outcome — the same pattern `fuseraft run --resume` already uses for checkpointed sessions, just over a live connection instead of disk.

### A human, over `fuseraft attach`

```bash
fuseraft attach
```

Connects to the daemon's Unix socket and lets you type tasks directly. Any number of people can attach concurrently — everyone sees the daemon's live progress for whatever's currently running, not just their own dispatch:

```
Attached → ~/.fuseraft/run/1f92641edb0442bd.sock. Type a task and press Enter. Ctrl+C to detach.
> Run the test suite and tell me if anything fails
queued → session a1b2c3d4 (running next)
→ Assistant is working...
  Assistant → shell_run(npm test)
Approval requested (shell_command):
{"type":"approval_request","kind":"shell_command","command":"npm test"}
Approve? (y/N): y
✓ done
All 42 tests passed.
>
```

A second person attached at the same time sees the `agent_starting`/`tool_calling` lines scroll by live — but never the approval prompt for a task they didn't dispatch (see [The safety model](#the-safety-model)).

---

## The safety model

**Unattended mutating actions deny by default.** A task with nobody attached — dispatched over MCP, or self-picked via [auto-dispatch](#objective-driven-auto-dispatch) — has any shell/write/git-push tool call denied unless `--unattended-policy allow` is set. This mirrors the REPL's own default-deny posture (`.env` access, the `--yolo`-gated sandbox); a task another agent asked for gets no more trust by default than one nobody's watching.

**Approval goes to whoever dispatched the task, never to a bystander.** With several people attached, live progress broadcasts to everyone, but a mutating tool call's approval prompt routes only to the connection that actually dispatched that specific task. Someone who's just watching cannot be prompted into approving a stranger's — or an agent's — action, no matter how many people happen to be attached.

Read the full trust-model writeup, including why the MCP endpoint has no authentication, in [Security — `fuseraft serve` trust model](security.md#fuseraft-serve-trust-model).

---

## Queue position

Since only one task runs at a time, dispatching while another is in flight just queues it. `dispatch_task`, `get_status`, and the attach socket's `queued` acknowledgement all report a `position` — how many tasks are ahead of yours (the running one, plus anything queued earlier). `0` means yours runs next.

---

## Objective-driven auto-dispatch

Everything above is still reactive: the daemon only runs what it's told. `--auto-objective <id>` (repeatable) is the exception — when idle, the daemon checks the named [objective](knowledge.md#objective-tracking)'s remaining tasks and runs the next one itself, with nobody dispatching anything:

```bash
fuseraft objective create --title "Backlog" \
  --tasks "Update the changelog,Add tests for the export command,Fix the flaky retry test"

fuseraft serve --auto-objective OBJ-0001 --unattended-policy allow
```

Left alone, the daemon works through the list — each self-picked task goes through the identical queue/approval/broadcast path as any other dispatch, distinguished only by an `auto_dispatched` event so anyone attached knows the daemon decided to do this on its own:

```
⚙ daemon self-dispatched from OBJ-0001: Update the changelog (session e36644f8)
→ Assistant is working...
✓ done
```

```bash
fuseraft objective status OBJ-0001
# Progress: 100% (3/3 tasks)
```

Two things keep this from being reckless:

- **No extra trust.** A self-picked task has no attach-connection origin, so it's gated by `--unattended-policy` exactly like an MCP-dispatched one — auto-dispatch is opt-in, not a bypass.
- **Failure-skip.** A task that fails 3 times in a row stops being auto-retried — it's still visible in the objective's remaining tasks for you to fix or re-run explicitly, but the daemon won't keep burning API calls on something that's clearly not going to succeed unattended.

An objective never runs on its own just because it exists, either — an old one sitting in `.fuseraft/knowledge/objectives/` from months ago doesn't start running just because a daemon happened to start in that directory. You have to name it explicitly.

---

## Not yet supported

One daemon serves one working directory — there's no multi-tenant mode for several projects behind a single process. There's no A2A agent-card front door (MCP is the only agent-facing protocol today). `--socket` is Unix-domain only; there's no Windows named-pipe equivalent yet.

---

## See also

- [CLI Reference — `fuseraft serve`](cli-reference.md#fuseraft-serve) and [`fuseraft attach`](cli-reference.md#fuseraft-attach) — every flag and the full MCP tool signatures
- [Security — `fuseraft serve` trust model](security.md#fuseraft-serve-trust-model) — the unauthenticated MCP endpoint, unattended policy, approval routing
- [Scripting & Automation — dispatch into a resident daemon](scripting.md#or-dispatch-into-a-resident-fuseraft-serve-daemon) — using `serve` instead of shelling out to `fuseraft run` per event
- [Knowledge Layer](knowledge.md) — objectives, ADRs, and the repository graph
- [MCP Integration](mcp.md) — connecting fuseraft *out* to other MCP servers (the other direction from this page)
- [Design](design.md#19-the-fuseraft-serve-daemon) — architecture rationale: why the queue is serialized, why the socket path is hashed, why approval and broadcast are split
