# Security Policy

## Supported versions

| Version | Security fixes |
|---------|---------------|
| `main` (latest) | ✅ Yes |
| Older tags | ❌ No — please upgrade |

fuseraft CLI is early-stage software. Only the latest commit on `main` and the most recent published release receive security fixes.

---

## Reporting a vulnerability

**Please do not file a public GitHub issue for security vulnerabilities.**

Use one of the two private channels below:

### Option A — GitHub Private Security Advisory (preferred)

1. Go to the **Security** tab of this repository.
2. Click **Report a vulnerability**.
3. Fill in the advisory form. GitHub keeps the report private until a fix is published.

### Option B — Email

Send a report to **scstauf@gmail.com** with:

- Subject: `[fuseraft-cli] Security report`
- A description of the vulnerability and its impact
- Steps to reproduce or a proof-of-concept (if available)
- Any suggested fix (optional but appreciated)

PGP encryption is not required but appreciated for highly sensitive reports.

---

## Response timeline

| Milestone | Target |
|-----------|--------|
| Acknowledgement | Within 5 business days |
| Initial assessment | Within 10 business days |
| Fix or mitigation | Within 90 days for critical; 180 days for lower severity |
| Public disclosure | After a fix is released (coordinated with reporter) |

If a fix will take longer than the target, we will communicate the delay and provide a mitigation or workaround if one is available.

---

## Coordinated disclosure

We follow coordinated (responsible) disclosure:

- Security fixes are committed and released before any public discussion of the vulnerability.
- Once a fix is released, we publish a GitHub Security Advisory crediting the reporter (unless they prefer to remain anonymous).
- We ask reporters to refrain from public disclosure until the fix is released, or for a maximum of 90 days after the initial report — whichever comes first.

---

## Scope

The following areas are in scope for security reports:

| Area | Notes |
|------|-------|
| **API key / credential storage** | Keychain integration (`SecretToolKeyStore`, `MacOsKeychainStore`, `WindowsCredentialManagerStore`, `UnavailableKeyStore`) and `~/.fuseraft/config` handling. fuseraft never writes API keys to disk in plaintext — a report that it does (or that it can be made to) is in scope. |
| **Shell plugin** | Command injection, sandbox bypass, `sudo` protection bypass |
| **FileSystem plugin** | Path traversal, sandbox escape |
| **HTTP plugin** | SSRF, allowlist bypass, private-IP filter bypass |
| **Skills execution** | Malicious scripts in project-scoped skill directories (`<cwd>/.agents/skills/`, `<cwd>/.fuseraft/skills/`) executing without user consent; credential exfiltration via subprocess env inheritance |
| **Prompt injection** | Adversarial tool results that override agent instructions |
| **Session files** | Permission issues in `~/.fuseraft/sessions/` or `~/.fuseraft/logs/{project_slug}/repl_events/` |
| **MCP server integration** | Malicious tool schemas, argument injection from connected servers |
| **Dependency vulnerabilities** | Known CVEs in direct NuGet dependencies that are exploitable via fuseraft-cli |

Out of scope:

- Vulnerabilities that require the attacker to already have write access to `~/.fuseraft/` or the project directory
- Social engineering or phishing
- Issues in LLM provider infrastructure (Anthropic, OpenAI, xAI, etc.)
- Theoretical vulnerabilities without a realistic attack path
- Issues only reproducible with `Security.AllowPrivateHosts: true` and a deliberately misconfigured setup

---

## Security features overview

For context on the existing security controls, see:

- [Security & Sandbox](../docs/security.md) — filesystem sandbox, HTTP allowlist, execution rings, sudo protection, and prompt injection detection
- [Security — API key storage](../docs/security.md#api-key-storage) — OS keychain integration details
