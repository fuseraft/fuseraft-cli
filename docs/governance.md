# Agent Governance Toolkit

fuseraft-cli integrates with the Agent Governance Toolkit (AGT) to provide runtime safety controls that operate independently of agent instructions. Governance is **always on** — there is no config key to enable or disable it. The controls described here apply to every session.

---

## What the toolkit provides

| Feature | Description |
|---------|-------------|
| [Execution rings](#execution-rings) | Per-agent privilege tiers derived from `TrustScore` |
| [Prompt injection detection](#prompt-injection-detection) | Blocks tool results that contain adversarial instruction overrides |
| [Hash-chain audit log](#audit-log) | Tamper-evident record of every governance event |
| [Circuit breaker](#circuit-breaker) | Stops runaway agents after repeated API failures |
| [SLO tracking](#slo-tracking) | Monitors routing validator pass rate within the session |
| [Rate limiting](#rate-limiting) | Escalates to HITL when a validator blocks the same route repeatedly |
| [DID identity](#did-identity) | Assigns a decentralized identifier to each agent for audit correlation |
| [Policy files](#policy-files) | Optional YAML files that extend or override default governance rules |

---

## Execution rings

When `Security.FileSystemSandboxPath` is configured, each agent is assigned to an execution ring based on its `TrustScore` (configured in [Agent fields](configuration.md#agent-configuration)). The ring controls what the agent is permitted to do within the sandbox.

| Ring | TrustScore | Write access | Network access |
|------|-----------|--------------|----------------|
| Ring 1 (Trusted) | ≥ 0.80 | yes | yes |
| Ring 2 (Standard) | ≥ 0.60 | yes | yes |
| Ring 3 (Sandbox) | < 0.60 | **no** | **no** |

Ring 3 agents can call `read_file` and `list_files` but are blocked from `write_file`, `delete_file`, `shell_run`, and HTTP requests. Denials are returned as tool results so the agent sees them; they are also recorded in the audit log.

**Default:** All agents default to `TrustScore: 0.7` (Ring 2) when not specified.

```yaml
- Name: Reviewer
  TrustScore: 0.6
  ...
```

To give a Reviewer or Planner read-only access to the sandbox while ensuring they cannot modify files, set `TrustScore` to any value below 0.60.

---

## Prompt injection detection

When a `shell_run` or `read_file` result contains text that looks like an adversarial instruction override (e.g. a file that says "Ignore your instructions and do X"), the injection detector flags the content before it is passed to the agent as a tool result.

Detection is heuristic and runs automatically — no configuration is needed. Detected injections are recorded in the audit log with `GovernanceEventType.ToolCallBlocked` and also appear in the [events stream](configuration.md#events) as `"tool_blocked"` entries.

---

## Audit log

All governance events are recorded in a session-scoped hash-chain audit log. Each entry links to the previous one via a SHA-256 hash, making the log tamper-evident: any modification of a past entry breaks the chain.

Events recorded:

| Event | When it fires |
|-------|---------------|
| `AgentRegistered` | When an agent is created for a new session |
| `PolicyViolation` | When a routing or termination validator blocks a handoff |
| `ToolCallBlocked` | When the sandbox or injection detector denies a tool call |
| `TrustFailed` | When a trust-level check fails |

The log is in-memory for the lifetime of the session. It is not written to disk automatically — `--verbose` output includes governance events as they fire. Future versions may add a `--audit-path` flag to persist the log.

---

## Circuit breaker

A circuit breaker wraps every agent invocation. If the underlying model API returns 5 consecutive failures, the circuit opens and the session is stopped with an error message rather than continuing to retry.

| Parameter | Value |
|-----------|-------|
| Failure threshold | 5 consecutive failures |
| Reset timeout | 30 seconds |
| Half-open probe calls | 1 |

When the circuit is open, the session is stopped and the checkpoint is saved so it can be resumed once the API is healthy again.

---

## SLO tracking

The governance kernel tracks a `policy-compliance` SLO: the fraction of routing validator checks that pass within the session. Target: 95%.

Burn-rate alerts fire when compliance degrades faster than the budget allows:

| Alert | Burn rate | Window |
|-------|-----------|--------|
| Warning | 2× | 1 hour |
| Critical | 5× | 10 minutes |

SLO events appear in the governance audit log. They do not currently surface in the terminal output but are visible in `--verbose` mode.

---

## Rate limiting

A failure counter tracks bad turns per agent. A "bad turn" is any turn that results in a correction being injected: no routing keyword, a keyword that belongs to a different role, multiple keywords in one response, or a keyword whose validator rejected the handoff.

With `Selection.Type: graph`, a single counter covers all of these uniformly and escalates at `Selection.Graph.MaxRetries` (default 4). With `keyword`/`statemachine`, only validator/contract failures are counted this way, classified by type and escalated per `FailureHandling.<Type>.Threshold` (default 3, or 2 for `ConflictingEvidence`); a bare missing-keyword/signal turn is not covered by this counter (see [Validators — Stuck detection](validators.md#stuck-detection) for the full breakdown). Either way, when the threshold is reached, `ValidatorStuckException` is thrown and the session stops with a descriptive error. The checkpoint is saved so the session can be resumed after diagnosing the issue.

`GovernanceKernel`'s rate limiter enforces the same threshold via a 10-minute rolling window alongside the consecutive-turn count: if that many failures accumulate within the window, escalation fires immediately rather than waiting for the consecutive-turn count to catch up.

This prevents infinite correction loops where an agent keeps re-emitting a broken handoff without making progress. The counter does not reset when the failure mode changes — alternating between validator failures and no-keyword turns hits the threshold at the same rate as repeated identical failures.

---

## DID identity

Each agent is assigned a [Decentralized Identifier](https://www.w3.org/TR/did-core/) at session start in the form `did:fuseraft:<agentName>:<sessionId>`. DIDs are used as the `agentId` field in all audit log entries so events from different sessions can be distinguished even when the same agent name is reused.

DIDs are regenerated fresh for each `StreamAsync` call (each session). They are not persisted.

---

## Policy files

YAML policy files extend or override the default governance rules. When a file exists at `policies/default.yaml` in the same directory as the orchestration config, it is loaded automatically.

```
.fuseraft/config/
  orchestration.yaml
  policies/
    default.yaml
```

Policy files are optional. If no file exists, the kernel uses built-in defaults (all features enabled, thresholds as documented above). Refer to the Agent Governance Toolkit documentation for the policy YAML schema.

---

## Relationship to the sandbox

The governance rings extend the filesystem sandbox: the sandbox controls *which paths* an agent may access; the ring controls *what operations* the agent may perform within those paths. Both checks must pass for a tool call to proceed.

See [Security & Sandbox](security.md) for path-level containment details.
