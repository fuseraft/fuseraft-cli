# Red Team Security Assessment — fuseraft-cli

Perform a full red-team security assessment of the fuseraft-cli project in
the current working directory.

## Scope

- **Source code:** all C# source under `src/`
- **Config surface:** all YAML/JSON config fields accepted by `fuseraft validate`
- **Security controls to test:** filesystem sandbox, shell filtering, HTTP allowlist,
  prompt injection detection, YAML config parsing, trust score / execution rings,
  ChangeEnvelope enforcement, credential handling, and env var expansion

## Objectives

1. **Recon:** map the attack surface — identify which source files implement each
   security control and enumerate all user-controlled config fields.

2. **Static attack:** read the source code for every security control and identify
   implementation vulnerabilities — path normalization edge cases, shell filter
   bypasses, YAML injection, prompt injection detection gaps, HTTP allowlist
   weaknesses, ring enforcement holes, and credential leakage in logs.

3. **Dynamic attack:** craft malicious YAML configs targeting each vulnerability
   category and run `fuseraft validate` against each probe. Record whether the
   config is rejected, accepted, or causes a crash.

4. **Triage:** deduplicate findings from both attack agents, score each by severity
   (Critical / High / Medium / Low / Info), and produce a structured security report
   at `.fuseraft/red-team/security-report.md` and `.fuseraft/red-team/security-report.json`.

## Constraints

- Never run `fuseraft run` on a malicious config — only `fuseraft validate`.
- Never modify source files. All artifacts go under `.fuseraft/red-team/`.
- Base every finding on real code or real tool output — no speculation.
