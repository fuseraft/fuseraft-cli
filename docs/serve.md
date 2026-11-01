---
title: "Serve (Daemon Mode)"
description: "fuseraft run builds an agent team, drives one task to completion, and exits. fuseraft repl does the same but interactively. fuseraft serve is the third shape: it builds the agent team once and stays r…"
---

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
  Attach WebSocket → ws://localhost:24601/attach  (fuseraft attach --url)
  HTTP on localhost, no token (--bind)
  A2A: off (--a2a)
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

`fuseraft serve` hosts an MCP server at `/mcp` (streamable HTTP, localhost-only unless you [bind it elsewhere with a token](#from-another-machine-over-websocket)) exposing three tools. Any MCP client — another fuseraft instance, Claude Code, anything that speaks the protocol — can call them directly:

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

### From another machine, over WebSocket

The same attach protocol is also served as a WebSocket at `/attach`, on the MCP port. Use it when the Unix socket isn't reachable: from another machine, from a container, or from a host such as fuseraft-web.

```bash
# On the daemon's machine
export FUSERAFT_SERVE_TOKEN=$(openssl rand -hex 32)
fuseraft serve --bind 0.0.0.0

# Anywhere else
export FUSERAFT_SERVE_TOKEN=...   # the same value
fuseraft attach --url ws://build-box.lan:24601/attach
```

Each WebSocket text message is one line of the attach protocol, so a client sees exactly what a socket client sees, starting with `hello`. Over loopback no token is needed, as with the Unix socket. Binding beyond loopback (`--bind`) requires `FUSERAFT_SERVE_TOKEN`, and then both `/attach` and `/mcp` require `Authorization: Bearer <token>`. Browsers are refused: a handshake carrying an `Origin` header gets 403, so a web page you visit can't open the daemon's socket.

**Use TLS beyond a trusted network.** Without it, the token and every task and approval cross the network in the clear, and the daemon warns about it at startup. Give it a certificate and the same listener serves `https://` and `wss://`:

```bash
fuseraft serve --bind 0.0.0.0 --tls-cert /etc/fuseraft/serve.crt --tls-key /etc/fuseraft/serve.key
#   TLS certificate CN=build-box.lan, SHA-256 3A:9F:…
fuseraft attach --url wss://build-box.lan:24601/attach
# A self-signed certificate, pinned, as a client that can pick its task up again
fuseraft attach --url wss://build-box.lan:24601/attach --cert-sha256 3A:9F:… --client-id laptop
```

A certificate from a CA the client trusts needs nothing more. For a self-signed one, a client can pin the SHA-256 fingerprint the daemon prints: `fuseraft attach --cert-sha256`, or the fingerprint field in fuseraft-web. A TLS proxy in front of a loopback-bound daemon works too.

**One token per client.** `FUSERAFT_SERVE_TOKEN` is one secret everyone shares. To give each client its own, so one can be revoked without the others, list them in a file and pass `--tokens-file`:

```bash
# /etc/fuseraft/tokens  (chmod 600; the daemon refuses a file other users can read)
# name     token (16+ characters)   scope (optional: full, the default, or read)
web        3f9a...c2
laptop     81be...07
dashboard  d41c...9e                read
```

A `read` token watches: over `/attach` it sees every task's live progress, but the daemon refuses any task it sends, so nothing ever asks it to approve anything; over `/mcp` it can call `get_status` and `get_result`, but `dispatch_task` isn't listed and is refused. Use it for a dashboard, a status page, or someone who should see what the daemon is doing without being able to direct it.

The daemon re-reads the file when it changes. Delete a line and that token stops working at once, and connections that used it are closed within a few seconds. Changing a token's scope closes its connections the same way, so none keep the old scope. Client ids are kept per token, so a client holding one token can't pick up another token's tasks or approvals by sending the same `Fuseraft-Client-Id`. `FUSERAFT_SERVE_TOKEN` still works alongside the file, as the token named `default`.

**Reconnecting.** Pings every 15 seconds notice a peer that vanished without closing. A client that sends a `Fuseraft-Client-Id` header on the handshake keeps its tasks across connections: if the connection drops while its task waits for approval, the daemon waits `--reconnect-grace` (60 seconds by default) for the client to come back and asks again on the new connection. Results that finish while it's away are sent when it returns, and its `hello` says what's still running. fuseraft-web does this, and so survives restarts and network blips. `fuseraft attach --url ... --client-id <id>` does the same: attach again with the id and it picks up your running task, asks its approval again, and shows what finished meanwhile. A client without an id (the Unix socket, or `--url` without `--client-id`) keeps the old behavior: when it disconnects, its task's approvals go to `--unattended-policy` at once.

**Restarting the daemon.** The queue is saved to `~/.fuseraft/state/<project>/serve-queue.json` (readable only by you) whenever a task is queued, starts or finishes, along with results a named client hasn't collected yet. A daemon that's stopped, killed or crashes runs those tasks again when it starts: the one that was running resumes from its saved session, as `fuseraft run --resume` would, and the rest follow in order, each still belonging to the client that sent it. A tool call that was mid-way when the daemon died runs again, as with any resume. Stop the daemon with Ctrl+C or SIGTERM (what systemd and `podman stop` send): it denies a pending approval rather than waiting for it, and the task resumes and asks again on the next start. A task that's been interrupted more than 3 times is failed instead of resumed, so one that crashes the daemon can't do it forever. With `Checkpoint.Mode: memory` there's no saved session to resume from, so an interrupted task fails and says so; queued ones still run.

---

## The safety model

**Unattended mutating actions deny by default.** A task with nobody attached — dispatched over MCP, or self-picked via [auto-dispatch](#objective-driven-auto-dispatch) — has any shell/write/git-push tool call denied unless `--unattended-policy allow` is set. This mirrors the REPL's own default-deny posture (`.env` access, the `--yolo`-gated sandbox); a task another agent asked for gets no more trust by default than one nobody's watching.

**Approval goes to whoever dispatched the task, never to a bystander.** With several people attached, live progress broadcasts to everyone, but a mutating tool call's approval prompt routes only to the client that actually dispatched that specific task (the same connection, or a reconnect under the same client id). Someone who's just watching cannot be prompted into approving a stranger's — or an agent's — action, no matter how many people happen to be attached.

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

## Hosting agents over A2A

MCP and attach hand the daemon a whole task. `--a2a` adds a third front door: each agent in the config gets its own [A2A](https://a2a-protocol.org) endpoint, so a fuseraft team on another machine can add it as a team member with `RemoteAgent`:

```bash
export FUSERAFT_A2A_TOKEN=$(openssl rand -hex 32)
fuseraft serve --a2a --a2a-bind 0.0.0.0
```

```
  A2A (RemoteAgent.Url) on 0.0.0.0, bearer token from FUSERAFT_A2A_TOKEN:
    → http://localhost:24602/Developer
    → http://localhost:24602/Tester
```

On the other machine, use this machine's address in place of `localhost`:

```yaml
Agents:
  - Name: Tester
    Instructions: Run the test suite and report failures.
    RemoteAgent:
      Url: http://build-box.lan:24602/Tester
      Headers:
        Authorization: Bearer ${BUILD_BOX_A2A_TOKEN}
```

What a call does and doesn't include:

- **Just that agent.** A call runs the hosted agent with its own instructions and tools, in this daemon's working directory, and returns its reply. The calling team's routing, validators and termination run on the caller's side; this team's don't run at all.
- **The same approval rules.** A call has no attached dispatcher, so its mutating tool calls follow `--unattended-policy` like an MCP-dispatched task: denied by default, allowed with `--unattended-policy allow`.
- **One thing at a time.** A call waits for a running task to finish, and a task waits for a running call, because both use the same agents and working directory. Set the caller's `RemoteAgent.TimeoutSeconds` (default 120) high enough to cover a queued task.
- **Its own session.** Each call gets a fresh `a2a-xxxxxxxx` session ID for the change log and events. Anyone attached sees an `a2a_call` event when one starts.

The endpoints listen on `localhost` unless `--a2a-bind` says otherwise. Binding beyond loopback requires `FUSERAFT_A2A_TOKEN`; every request, including the agent card, must then carry `Authorization: Bearer <token>`. The endpoints speak plain HTTP, so across an untrusted network put a TLS proxy in front (cards then name the address the proxy forwards, via `X-Forwarded-Proto`/`X-Forwarded-Host` from a proxy on the same machine) or use an SSH tunnel. `--a2a-agent <name>` (repeatable) hosts only the named agents, and `--a2a-port` changes the port (default: the MCP port + 1).

See [Security — `fuseraft serve` trust model](security.md#fuseraft-serve-trust-model) for what the token does and doesn't protect.

---

## Not yet supported

One daemon serves one working directory — there's no multi-tenant mode for several projects behind a single process. Results of MCP-dispatched tasks that finished before a restart aren't kept, so `get_result` for one returns unknown afterwards; its session history still has the outcome. The A2A endpoints host individual agents, not the whole team as one agent, and don't stream. `--socket` is Unix-domain only; there's no Windows named-pipe equivalent yet, so on Windows attach over `--url`. Browser clients can't attach directly, since they can't send the token and an `Origin` header is refused.

---

## See also

- [CLI Reference — `fuseraft serve`](cli-reference.md#fuseraft-serve) and [`fuseraft attach`](cli-reference.md#fuseraft-attach) — every flag and the full MCP tool signatures
- [Wire Protocol](protocol.md#live-sessions) — the attach socket's JSONL events, for building your own client
- [Security — `fuseraft serve` trust model](security.md#fuseraft-serve-trust-model) — the unauthenticated MCP endpoint, the A2A token, unattended policy, approval routing
- [Configuration — `RemoteAgent`](configuration.md) — the caller's side of an A2A agent
- [Scripting & Automation — dispatch into a resident daemon](scripting.md#or-dispatch-into-a-resident-fuseraft-serve-daemon) — using `serve` instead of shelling out to `fuseraft run` per event
- [Knowledge Layer](knowledge.md) — objectives, ADRs, and the repository graph
- [MCP Integration](mcp.md) — connecting fuseraft *out* to other MCP servers (the other direction from this page)
- [Design](design.md#19-the-fuseraft-serve-daemon) — architecture rationale: why the queue is serialized, why the socket path is hashed, why approval and broadcast are split
