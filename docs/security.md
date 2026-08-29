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

| Plugin | Functions / Argument | Check type |
|--------|----------------------|-----------|
| `FileSystem` | `read_file`, `write_file`, `delete_file`, `list_files` — `path` / `directory` | Hard deny if resolved path is outside sandbox |
| `FileSystem` | `patch_file`, `create_directory`, `delete_directory`, `set_permissions`, `copy_file`, `move_file` | Hard deny if resolved path is outside sandbox (always enforced, regardless of whether `FileSystemPermissions` globs are configured) |
| `Shell` | `shell_run`, `shell_run_script` — `workingDirectory` | Hard deny if resolved path is outside sandbox |
| `Shell` | `shell_run`, `shell_run_script` — `command` / `script` | Best-effort scan for absolute paths escaping sandbox |

### Path resolution

All paths are resolved to their canonical absolute form (symlinks followed, `..` removed) before checking. A path is allowed if its canonical form starts with the sandbox root.

### Shell command scanning

The `command` and `script` arguments are scanned before execution. Two checks run in order:

**1. Subshell blocking** — Commands containing `$(...)`, `` `...` `` (backtick substitution), or `${VAR}` variable expansion are **unconditionally denied**. These constructs evaluate at runtime and produce values that cannot be statically verified against the sandbox root. If your workflow requires command substitution, use the `CodeExecution` plugin (Docker) instead.

**2. Absolute path scan** — The remaining command text is scanned with a regex for tokens that look like absolute paths. Matches are resolved and checked against the sandbox. System binary prefixes are **exempted** so agents can invoke normal tools without being blocked:

**Exempted prefixes (Unix):** `/usr/`, `/bin/`, `/sbin/`, `/lib/`, `/lib64/`, `/opt/`, `/nix/`, `/run/current-system/`, `/snap/`

**Exempted prefixes (Windows):** `C:\Windows\`, `C:\Program Files\`, `C:\Program Files (x86)\`

This means `/usr/bin/dotnet build src/` is allowed, but `cat /etc/passwd` is blocked.

### Limitation

Absolute-path scanning is heuristic. Shell escaping (quoting, concatenation) may bypass regex detection. **For strict containment, use the `CodeExecution` plugin (Docker) instead of `Shell`.** Docker containers run with `--network none` and are isolated from the host filesystem.

### Denial response

When a check fails, the function is never executed and the agent receives this tool result:

```
[DENIED] Path '/etc/passwd' is outside the configured sandbox '/home/user/projects/myapp'.
All file operations must stay within the sandbox.
```

For subshell constructs:

```
[DENIED] Shell command contains a command substitution or variable expansion ('$(cat /etc/passwd)')
that cannot be statically verified against the sandbox. Rewrite the command without subshells,
or use the CodeExecution plugin (Docker) for commands that require substitution.
```

The agent sees these as tool errors and can respond accordingly (typically by staying within the sandbox).

---

## Filesystem permissions (read / write / deny globs)

`Security.FileSystemPermissions` adds per-path access control on top of the sandbox boundary. All three sub-lists use the same glob syntax as `ChangeEnvelope` and are evaluated relative to `FileSystemSandboxPath`. Requires `FileSystemSandboxPath` to be set.

```yaml
Security:
  FileSystemSandboxPath: /home/user/projects/myapp
  FileSystemPermissions:
    Read:
      - src/**
      - docs/**
    Write:
      - tests/**
      - docs/**
    Deny:
      - secrets/**
      - infra/prod/**
      - .env
```

### Evaluation order

For every filesystem function call, the three lists are checked in this order:

1. **Deny** — if the resolved path matches any `Deny` glob, the call is blocked immediately, regardless of `Read` or `Write`.
2. **Write** — if the function is a write operation and `Write` is non-empty, the path must match at least one `Write` glob to proceed.
3. **Read** — if the function is a read operation and `Read` is non-empty, the path must match at least one `Read` glob to proceed.

### Which functions are covered

| Category | Functions | Notes |
|----------|-----------|-------|
| Content-read (Read glob applies) | `read_file`, `grep_file`, `get_file_summary` | Returns file content |
| Metadata (Deny glob only, exempt from Read) | `list_files`, `list_directory`, `get_file_info` | Returns names / timestamps only, not content — use `Deny` to restrict these |
| Write ops (Write glob + envelope apply) | `write_file`, `patch_file`, `delete_file`, `create_directory`, `delete_directory`, `set_permissions` | |
| Mixed read+write (Copy/Move) | `copy_file`, `move_file` | Read glob checked on `source`; Write glob and envelope checked on `destination` |

### Interaction with ChangeEnvelope

`FileSystemPermissions.Write` and `ChangeEnvelope` are independent restrictions — **both must be satisfied** when both are configured. A write is permitted only if the path matches at least one pattern from each list.

`ChangeEnvelope` targets brownfield workflows where the Archaeologist auto-populates the list from a discovery brief. `FileSystemPermissions.Write` is the general-purpose alternative for manual configuration.

`ChangeEnvelope` applies to direct writes (`write_file`, `patch_file`, `delete_file`) and to the **destination** of copy and move operations — so copying or moving a file into a path outside the envelope is also denied.

### Denial response

```
[DENIED] 'infra/prod/deploy.sh': Path is blocked by a configured FileSystem deny rule.
[DENIED] 'src/auth/token.go': Path is outside the configured FileSystem write permissions.
```

---

## Shell policy

`Security.ShellPolicy` controls which shell commands agents may execute. It is enforced in the Shell plugin before execution and **does not require a filesystem sandbox** — it works even when `FileSystemSandboxPath` is not set.

```yaml
Security:
  ShellPolicy:
    Allow:
      - "go test"
      - "npm test"
      - "dotnet test"
    Deny:
      - "rm -rf"
      - "curl | bash"
      - "wget | sh"
      - "dd if="
```

### Evaluation

- **Deny is checked first.** If the command text contains any `Deny` pattern (case-insensitive substring match), the command is blocked regardless of the `Allow` list.
- **Allow is evaluated next.** When the `Allow` list is non-empty, the command must contain at least one `Allow` pattern (case-insensitive substring match) to proceed. Commands that match no allow pattern are rejected.
- When both lists are empty, the shell is unrestricted (subject to the existing `sudo` block).

Matching is substring-based so patterns are flexible:
- `"go test"` matches `go test ./...`, `go test -v ./pkg/...`, etc.
- `"rm -rf"` blocks any command containing that substring.

### Applies to all shell execution

The policy is enforced in `shell_run`, `shell_run_script`, and `shell_run_background`. Commands from any of these three tools are checked against the same `ShellPolicy`.

### Denial response

```
[DENIED] Shell command blocked: matches configured deny pattern 'rm -rf'.
[DENIED] Shell command blocked: not matched by any configured allow pattern.
         Allowed: 'go test', 'npm test', 'dotnet test'.
```

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

### Secret masking in logs

All log output (console, `~/.fuseraft/logs/app.log`, and any debug sidecar file) passes through a secret-masking text formatter before being written. The formatter applies three regex patterns:

| Pattern | Example match | Replaced with |
|---------|--------------|---------------|
| `sk-[A-Za-z0-9_-]{20,}` | `sk-ant-api03-abc123…` | `[REDACTED]` |
| `(?i)bearer <token>` | `Bearer eyJhbGc…` | `[REDACTED]` |
| `(?i)(api_key\|token\|secret)=<value>` | `api_key=supersecret` | `[REDACTED]` |

This means even if a provider error response or debug trace contains an API key, it is stripped before reaching any log sink. No configuration is required — masking is always active.

| Platform | Store | Mechanism |
|----------|-------|-----------|
| Linux | GNOME Keyring | `secret-tool` CLI (libsecret); service=`fuseraft-cli`, account=`default` |
| macOS | Keychain | `security` CLI; service=`fuseraft-cli`, account=`default` |
| Windows | Credential Manager | Win32 `CredRead`/`CredWrite` via P/Invoke; target=`fuseraft-cli/default`. Works in Git Bash and any other shell. |

**No plaintext fallback.** fuseraft never writes API keys to disk in plaintext, on any platform, under any circumstances. If no OS keychain is reachable (e.g. Linux without a running secret service), key storage fails with a clear message and the key is kept in memory for the current process only — you'll need to re-enter it next session, or set a provider environment variable (e.g. `ANTHROPIC_API_KEY`) so you don't have to. On startup, fuseraft also deletes (and, where possible, migrates into the keychain) any leftover `~/.fuseraft/.key` file written by fuseraft versions older than this policy.

`~/.fuseraft/config` stores only the model ID, provider URL, and provider type — no secrets. If you open the file you will see:

```json
{
  "modelId": "claude-sonnet-4-6",
  "endpoint": "https://api.anthropic.com/v1",
  "provider": "openai"
}
```

**Migration from older configs.** Configs written before keychain support was added may contain a plain-text `apiKey` field, and versions predating the no-plaintext policy may have left a `~/.fuseraft/.key` file on disk. On the first run after upgrading, fuseraft detects both, attempts to move the value into the OS keychain, and removes the plaintext copies either way — even if no keychain is available to migrate into. No manual action is needed.

**Using an environment variable instead.** Setting a provider env var (e.g. `ANTHROPIC_API_KEY`) always works as a fallback. The env var is used when no `~/.fuseraft/config` exists or when the keychain has no entry for `fuseraft-cli`.

**VS Code extension.** When the fuseraft VS Code extension invokes the CLI it always passes `--vscode`. In this mode the CLI reads the API key from the `FUSERAFT_API_KEY` environment variable rather than the OS keychain. The extension stores the key in VS Code's built-in `SecretStorage` (backed by the OS credential store) and injects it into every terminal it opens. No manual configuration is needed — set your key once via **fuseraft: Configure fuseraft** and it is available to all commands run through the extension.

**Relocating `~/.fuseraft` (`FUSERAFT_HOME`).** Setting `FUSERAFT_HOME` moves the entire global root — config, sessions, logs, scratchpad, skills, memory — to the given directory (see [Getting Started — Relocating `~/.fuseraft`](getting-started.md#relocating-fuseraft)). This never includes the API key: OS keychains are local to the machine they run on and do not follow a redirected `FUSERAFT_HOME` to a network share, and fuseraft will not write the key to the share as a plaintext file instead (see "No plaintext fallback" above). On a machine with no reachable keychain, set a provider environment variable (e.g. `ANTHROPIC_API_KEY`) rather than relying on persisted key storage.

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
- Microsoft Agent Framework's skills provider supports gating `load_skill`/`read_skill_resource`/`run_skill_script` behind an approval step (`AgentSkillsProviderOptions`), but fuseraft explicitly disables it today, since neither the REPL nor orchestration has a pipeline that resolves an approval request — leaving it enabled would make the tools non-functional rather than gated. Script execution is therefore automatic once a skill is loaded; wiring real approval (REPL: a confirmation prompt; orchestration: `IHumanApprovalService`) is a known future improvement, not yet implemented.
- `read_skill_resource` and `run_skill_script` resolve the model-supplied path against the skill directory and reject anything that resolves outside it, including via a symlinked file or subdirectory planted inside the skill folder — this narrows path-based escape from *within* a loaded skill, but a fully malicious skill script still runs as an OS subprocess with the full process environment; it isn't a substitute for only loading trusted skills.

---

## Security notes

- The path sandbox and ring system are enforced at the agent middleware layer, not at the OS level. A compromised plugin bypass (e.g. a malicious MCP server) could potentially circumvent them.
- For production use with untrusted tasks, run fuseraft-cli inside a container or VM rather than relying solely on the sandbox config.
- Session files in `~/.fuseraft/sessions/` are written with owner-only permissions (Unix mode 0600) to prevent other users from reading session content.
- See [Governance](governance.md) for the full set of runtime safety controls.
