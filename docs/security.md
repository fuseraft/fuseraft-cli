# Security & Sandbox

fuseraft-cli provides two runtime containment mechanisms: a filesystem sandbox that restricts where agents can read, write, and execute, and an HTTP allowlist that restricts which hosts agents can contact. Both are enforced in code via an `IFunctionInvocationFilter` that runs before plugin functions execute.

---

## Filesystem sandbox

Set `Security.FileSystemSandboxPath` to a directory path. Every `FileSystem` and `Shell` plugin call that involves a path argument is checked before execution.

```yaml
Security:
  FileSystemSandboxPath: /home/user/projects/myapp
```

### What is checked

| Plugin | Argument | Check type |
|--------|----------|-----------|
| `FileSystem` | `path` | Hard deny if resolved path is outside sandbox |
| `FileSystem` | `directory` | Hard deny if resolved path is outside sandbox |
| `Shell` | `workingDirectory` | Hard deny if resolved path is outside sandbox |
| `Shell` | `command` | Best-effort scan for absolute paths escaping sandbox |
| `Shell` | `script` | Best-effort scan for absolute paths escaping sandbox |

### Path resolution

All paths are resolved to their canonical absolute form (symlinks followed, `..` removed) before checking. A path is allowed if its canonical form starts with the sandbox root.

### Shell command scanning

The `command` and `script` arguments are scanned with a regex for tokens that look like absolute paths. Matches are resolved and checked against the sandbox. System binary prefixes are **exempted** so agents can invoke normal tools without being blocked:

**Exempted prefixes (Unix):** `/usr/`, `/bin/`, `/sbin/`, `/lib/`, `/lib64/`, `/opt/`, `/nix/`, `/run/current-system/`, `/snap/`

**Exempted prefixes (Windows):** `C:\Windows\`, `C:\Program Files\`, `C:\Program Files (x86)\`

This means `/usr/bin/dotnet build src/` is allowed, but `cat /etc/passwd` is blocked.

### Limitation

Shell command scanning is heuristic. It can be bypassed by variable interpolation, subshells, or shell escaping. **For strict containment, use the `CodeExecution` plugin (Docker) instead of `Shell`.** Docker containers run with `--network none` and are isolated from the host filesystem.

### Denial response

When a check fails, the function is never executed and the agent receives this tool result:

```
[DENIED] Path '/etc/passwd' is outside the configured sandbox '/home/user/projects/myapp'.
All file operations must stay within the sandbox.
```

The agent sees this as a tool error and can respond accordingly (typically by staying within the sandbox).

---

## Change envelope

Restricts **write** operations (`write_file`, `patch_file`, `delete_file`) to files matching at least one declared glob pattern. Read operations (`read_file`, `list_files`) are never affected. Requires `FileSystemSandboxPath` to be set — patterns are evaluated relative to the sandbox root.

```yaml
Security:
  FileSystemSandboxPath: /home/user/projects/myapp
  ChangeEnvelope:
    - src/billing/**
    - src/payments/processor.go
    - tests/billing/**
```

A write attempt outside the envelope produces:

```
[DENIED] Path 'src/auth/token.go' is outside the configured change envelope.
Only files matching the declared envelope globs may be written in this session.
Ask the Planner to expand the scope if this file needs to change.
```

**Glob syntax** — standard glob patterns using `*` (single directory level), `**` (any depth), and `?` (single character). Patterns are matched case-insensitively on Windows and case-sensitively on Unix.

**Brownfield auto-population** — when `Brownfield.SeedEnvelopeFromBrief: true` is set and a discovery brief exists at `Brownfield.DiscoveryBriefPath`, the brief's `in_scope_files` list is automatically merged into `ChangeEnvelope` at session startup. The envelope grows as the Archaeologist expands scope; it never shrinks during a session. See [Configuration → Brownfield mode](configuration.md#brownfield-mode).

**Enforcement is additive** — any pattern already in `ChangeEnvelope` at config load is retained alongside the patterns seeded from the brief.

**When no envelope is set** — all writes within the sandbox root are permitted (default behaviour).

---

## File read size limit

`read_file` returns at most `Security.ReadFileSizeLimit` characters per call (default 20,000 ≈ 5k tokens). Files larger than the limit are truncated with a notice telling the agent how to read further with `shell_run + tail`.

```yaml
Security:
  ReadFileSizeLimit: 40000   # raise to ~10k tokens per read for large-file workloads
```

Tune this when agents need to read large files in one call, or lower it to reduce per-read token cost for agents with small context windows. The limit applies globally to all agents in the session.

---

## HTTP allowlist

Set `Security.HttpAllowedHosts` to a list of permitted hostnames. If the list is non-empty, any `Http` plugin call to a hostname not on the list is denied.

```yaml
Security:
  HttpAllowedHosts:
    - api.github.com
    - registry.npmjs.org
    - pypi.org
```

Private and loopback addresses are **always blocked** regardless of the allowlist:

| Range | Blocked addresses |
|-------|-----------------|
| Loopback | `127.0.0.0/8` |
| Private class A | `10.0.0.0/8` |
| Private class B | `172.16.0.0/12` |
| Private class C | `192.168.0.0/16` |
| Link-local | `169.254.0.0/16` |
| IPv6 loopback | `::1` |
| IPv6 link-local | `fe80::/10` |

This prevents SSRF-style attacks where an agent is tricked into making requests to internal infrastructure.

An empty `HttpAllowedHosts` list means unrestricted (but private IPs are still blocked).

Entries in `HttpAllowedHosts` support `${ENV_VAR}` token expansion — the token is replaced with the environment variable's value at startup:

```yaml
Security:
  HttpAllowedHosts:
    - "${SNOW_HOST}"       # e.g. mycompany.service-now.com
    - api.github.com
```

Tokens referencing unset variables expand to an empty string. See [Configuration → ApiProfiles](configuration.md#apiprofiles) for the same expansion applied to profile header values.

### AllowPrivateHosts

Set `Security.AllowPrivateHosts: true` to bypass the private/loopback IP check. This allows agents to reach locally-running servers such as a mock API or a development service.

```yaml
Security:
  AllowPrivateHosts: true
  HttpAllowedHosts:
    - localhost
    - "127.0.0.1"
```

**Do not set this in production configs.** It is intended exclusively for local development and sandbox environments where agents must contact a server running on the same machine. The `HttpAllowedHosts` allowlist is still enforced when set — `AllowPrivateHosts` only disables the private-IP range check, not the host allowlist.

---

## Combining both

```yaml
Security:
  FileSystemSandboxPath: /home/user/projects/myapp
  HttpAllowedHosts:
    - api.github.com
```

Agents are now constrained to:
- Reading and writing files only within `/home/user/projects/myapp`
- Making HTTP requests only to `api.github.com`

---

## Docker containment (CodeExecution plugin)

For the highest isolation, use the `CodeExecution` plugin's `sandbox_run` instead of `Shell`. Each invocation gets a fresh Docker container:

- `--network none` — no network access
- `--memory 256m` — memory capped
- `--cpus 0.5` — CPU capped
- Container is removed after execution

```yaml
Plugins:
  - CodeExecution
```

```
sandbox_run(language="python", code="import os; print(os.listdir('/'))")
```

This gives a fully isolated execution environment regardless of what the `FileSystemSandboxPath` is set to.

---

## Execution rings

When `Security.FileSystemSandboxPath` is configured, each agent is placed in an execution ring based on its `TrustScore`. Rings layer on top of the path-level sandbox to control which *operations* the agent may perform within those paths.

| Ring | TrustScore | Writes allowed | Shell allowed |
|------|-----------|----------------|---------------|
| Ring 1 (Trusted) | ≥ 0.80 | yes | yes |
| Ring 2 (Standard) | ≥ 0.60 | yes | yes |
| Ring 3 (Sandbox) | < 0.60 | **no** | **no** |

Ring 3 agents can read files and list directories but are blocked from writing, deleting, running shell commands, or making network requests. This is useful for review-only agents or auditors that should not be able to modify state.

```yaml
- Name: Auditor
  TrustScore: 0.5
  Plugins:
    - FileSystem
```

See [Governance — Execution rings](governance.md#execution-rings) for details.

---

## `sudo` protection

`sudo` is unconditionally blocked in the Shell plugin. Any command or script containing `sudo` — including in pipe chains (`cmd && sudo apt install ...`), semicolon sequences, or multi-line scripts — is caught before execution and the agent receives:

```
[DENIED] sudo is not permitted. Prefer non-privileged alternatives: pip install --user,
python -m pip install --user, pipx, or a virtual environment
(python -m venv .venv && .venv/bin/pip install ...).
If elevated privileges are truly required, tell the user exactly which command to run
and they will run it themselves.
```

This keeps privilege escalation out of the automated loop. If elevated access is genuinely needed, the agent surfaces the command to the user to run manually.

---

## Prompt injection detection

Tool results are scanned for adversarial instruction overrides before they are passed to the agent. If a `shell_run` or `read_file` result looks like it is trying to override the agent's instructions, the content is flagged and the event is recorded in the [audit log](governance.md#audit-log).

Detection is automatic — no configuration required.

---

## API key storage

When you configure the REPL via the first-run wizard or `/provider setup`, the API key is stored in the OS-native credential store — never in `~/.fuseraft/config` on disk.

| Platform | Store | Mechanism |
|----------|-------|-----------|
| Linux | GNOME Keyring | `secret-tool` CLI (libsecret); service=`fuseraft-cli`, account=`default` |
| macOS | Keychain | `security` CLI; service=`fuseraft-cli`, account=`default` |
| Windows | Credential Manager | Win32 `CredRead`/`CredWrite` via P/Invoke; target=`fuseraft-cli/default`. Works in Git Bash and any other shell. |
| Fallback | `~/.fuseraft/.key` | Plain-text file with Unix mode 0600. Used only when no keychain is available. A warning is shown on first write. |

`~/.fuseraft/config` stores only the model ID and provider URL — no secrets. If you open the file you will see:

```json
{
  "modelId": "claude-sonnet-4-6",
  "endpoint": "https://api.anthropic.com/v1"
}
```

**Migration from older configs.** Configs written before keychain support was added may contain a plain-text `apiKey` field. On the first run after upgrading, fuseraft detects this field, moves the value into the keychain, and rewrites the config without it. No manual action is needed.

**Using an environment variable instead.** Setting a provider env var (e.g. `ANTHROPIC_API_KEY`) always works as a fallback. The env var is used when no `~/.fuseraft/config` exists or when the keychain has no entry for `fuseraft-cli`.

**VS Code extension.** When the fuseraft VS Code extension invokes the CLI it always passes `--vscode`. In this mode the CLI reads the API key from the `FUSERAFT_API_KEY` environment variable rather than the OS keychain. The extension stores the key in VS Code's built-in `SecretStorage` (backed by the OS credential store) and injects it into every terminal it opens. No manual configuration is needed — set your key once via **fuseraft: Set Up Provider** and it is available to all commands run through the extension.

---

## Crash dumps

When the application exits with an unhandled exception, a structured JSON report is written to `~/.fuseraft/crashdump/` before the process terminates. Each file is named with a timestamp and a random short ID:

```
~/.fuseraft/crashdump/20260422-153012-a3f1b2c4.json
```

The path is printed to the console immediately after the exception so you can locate it without searching.

**What is captured**

| Field | Description |
|-------|-------------|
| `session_id` | Unique ID for this crash instance (matches the filename) |
| `timestamp` | UTC ISO 8601 timestamp |
| `command` | Command-line arguments supplied to the process |
| `os` | Operating system description |
| `runtime` | .NET framework description |
| `app_version` | fuseraft-cli version (semver + git hash) |
| `exception` | Exception type, message, and stack trace; inner exceptions are included recursively; `AggregateException` lists all inner exceptions |

**What is NOT captured**

- API keys (never written to disk outside the OS keychain)
- Session content (task text, agent messages, LLM responses)
- File contents or shell output

A dump is written only for genuine crashes. Normal exits, user cancellation (Ctrl+C), and command-line usage errors do not produce one.

---

## Skills execution trust model

Skills loaded from `~/.fuseraft/skills/`, `~/.agents/skills/`, or the built-in `<binary>/skills/` directory are operator-controlled and treated as trusted. Scripts bundled in those skills execute as OS subprocesses and inherit the process environment, including any API keys in env vars.

Project-scoped skills — loaded from `<cwd>/.fuseraft/skills/` and `<cwd>/.agents/skills/` — carry additional risk: **they travel with the repository**. A repo checked out from an untrusted source may contain a `.agents/skills/` directory with malicious scripts. Those scripts are auto-discovered and advertised to agents at session start, with no prompt to the user.

### Risk: untrusted project skills

If `fuseraft run --work-dir` points at a directory you did not author, any skill scripts in that directory will be available for agents to execute. Because script execution inherits the process environment, a malicious script can read API keys, tokens, and other credentials available as env vars.

**Mitigations:**

- Only run `fuseraft` in working directories you trust. Treat `.agents/skills/` and `.fuseraft/skills/` in a cloned repo the same way you would treat a `Makefile` or `package.json` postinstall script.
- For higher assurance, run fuseraft inside a Docker container (`CodeExecution` plugin) where the host environment is not exposed.
- `UseScriptApproval` support is planned — when enabled it will require explicit user confirmation before any skill script executes. Until then, script execution is automatic once a skill is loaded.

---

## Security notes

- The path sandbox and ring system are enforced at the agent middleware layer, not at the OS level. A compromised plugin bypass (e.g. a malicious MCP server) could potentially circumvent them.
- For production use with untrusted tasks, run fuseraft-cli inside a container or VM rather than relying solely on the sandbox config.
- Session files in `~/.fuseraft/sessions/` are written with owner-only permissions (Unix mode 0600) to prevent other users from reading session content.
- See [Governance](governance.md) for the full set of runtime safety controls.
