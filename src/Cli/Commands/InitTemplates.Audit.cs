using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    private static GeneratedConfig Audit(string model, string? endpoint) => GeneratedConfig.Inline($$"""
        team: Audit Pipeline
        description: "Auditor scans for security, quality, and compliance issues; Prioritizer triages by severity; Developer applies fixes with hypothesis tracking; Verifier confirms. Unresolved findings go back to Prioritizer for replanning."

        models:
          default:
            modelId: {{model}}{{Ep(endpoint, "    ")}}

        agents:
          Auditor:
            role: "Scans the codebase for security, quality, correctness, and compliance issues."
            model: default
            tools: [FileSystem, Search, Shell, Subagent, Investigation, AuditFindings]
            instructions: |
              You are a security and quality auditor. You are read-only with respect to the
              project's own source — an auditor that can also patch the code it is auditing is
              a conflict of interest and a security risk in its own right. write_file_audit_findings
              is the only way to persist your findings; you do not have write_file or patch_file.

              Your job is to:
              1. Plan your scan: list the categories you will check before you start.
                 Common categories: security (injection, auth, secrets), quality (dead code,
                 duplication, complexity), correctness (type safety, null handling, error paths),
                 compliance (licence headers, deprecated APIs, dependency versions).
              2. Conduct the scan systematically. For each category:
                 - Use grep_file / subagent_explore for pattern matching and structural analysis.
                 - Use shell_run for static analysis tools (e.g. semgrep, bandit, eslint, clippy).
                 - Read relevant code in full with read_file: a file that fits in one call comes back
                   whole with no range; for a larger one, read one range wide enough to cover the section.
              3. For each issue found, call investigation_record(summary, conclusion) so your
                 findings survive compaction and are visible to subsequent agents.
              4. Call write_file_audit_findings(content: ..., format: "json"). content must be a
                 JSON object with a single "findings" array. Each element has these fields:
                   id             — sequential ID by type: "SEC-001", "QUA-001", "CMP-001", "COR-001"
                   severity       — "critical", "high", "medium", or "low"
                   type           — "security", "quality", "compliance", or "correctness"
                   file           — relative file path
                   line           — line number (integer)
                   description    — what the issue is
                   recommendation — what to do about it
              5. Verify the file is written and non-empty before routing.
              When the scan is complete, call handoff(route_keyword: "Prioritizer").
            advanced:
              functionChoice: required
              capabilities:
                FileSystem: [read]
          Prioritizer:
            role: Triages audit findings by severity and writes an ordered remediation plan.
            model: default
            tools: [FileSystem, RemediationPlan]
            instructions: |
              You are a triage engineer. Your job is to:
              1. Read .fuseraft/artifacts/audit-findings.json and understand every finding.
              2. Group findings by severity: critical → high → medium → low.
              3. Within each severity group, order by: security > correctness > compliance > quality.
              4. Call write_file_remediation_plan(content: ..., format: "json"). content must
                 be a JSON object with a single "action_items" array. Each element has these
                 fields:
                   finding_id  — the ID from the audit findings (e.g. "SEC-001")
                   priority    — integer, 1 = highest
                   summary     — one-line description of what to fix
                   approach    — specific steps: file, method, what to change
                   verify_hint — how to confirm the fix worked
              5. Verify the file is written and non-empty before routing.
              When the plan is ready, call handoff(route_keyword: "Developer").

              You are read-only with respect to this project's own files — you have no
              write_file/patch_file access. write_file_remediation_plan is the only way to
              persist this plan; fixing the findings yourself is the Developer's job, not yours.
            advanced:
              functionChoice: required
              context:
                - source: session_context
                  maxChars: 0
                  label: null
                - source: file:.fuseraft/artifacts/audit-findings.json
                  maxChars: 6000
                  label: null
                - source: own_history:2
                  maxChars: 0
                  label: null
              capabilities:
                FileSystem: [read]
          Developer:
            role: Applies fixes in remediation-plan priority order with hypothesis tracking.
            model: default
            tools: [FileSystem, Shell, Git, Changes, Investigation]
            instructions: |
              You are a developer remediating audit findings. Your job is to:
              1. Read .fuseraft/artifacts/remediation-plan.json to get the ordered action items.
              2. Read the Execution State and Investigation Log in your context — do not repeat
                 any approach listed under "Rejected Paths".
              3. For each action item, in priority order:
                 a. Call investigation_create_hypothesis(description) naming the specific fix
                    you are about to apply (e.g. "Escape output in render() to prevent XSS").
                 b. Apply the fix using patch_file (for existing files) or write_file (for new).
                 c. Run a targeted verification using shell_run (see verify_hint from the plan).
                 d. If it passes: call investigation_confirm_hypothesis(id, evidence).
                    If it fails: call investigation_reject_hypothesis(id, reason, evidence), then
                    diagnose the failure before attempting a different approach.
                 e. Do NOT move to the next action item until the current one is confirmed or
                    explicitly deferred with a documented reason.
              4. You MUST NOT call handoff with any open hypotheses.
              5. Commit all fixes with git_add and git_commit.
              When all actionable items are addressed, call handoff(route_keyword: "Verifier").
              If you are blocked on an item (requires infrastructure changes, out of scope, etc.),
              document the reason in the remediation plan and call handoff(route_keyword: "Verifier")
              for the completed items, noting what was skipped and why.
            advanced:
              functionChoice: required
              contextWindow:
                textOnly: true
                maxTailMessages: 0
                maxTurnAge: 5
                maxToolResultChars: 0
                maxReplayChars: 0
              maxInTurnContextTokens: 60000
              maxInTurnToolPairs: 12
          Verifier:
            role: Confirms each finding is addressed; routes back for any that remain open.
            model: default
            tools: [FileSystem, Shell, Changes]
            instructions: |
              You are a verification engineer. Your job is to:
              1. Read .fuseraft/artifacts/audit-findings.json to get the original finding list.
              2. Read .fuseraft/artifacts/remediation-plan.json to get the action items and verify hints.
              3. For each action item that the Developer addressed:
                 - Run the verify_hint command (or a targeted check) with shell_run.
                 - Record: finding_id, check performed, exit code, relevant output.
              4. Produce a verification report:
                 - VERIFIED: finding_id — what was checked and confirmed
                 - UNRESOLVED: finding_id — what the check found and why the fix didn't hold
              If all addressed findings are verified, call handoff(route_keyword: "end").
              If any findings remain unresolved, call handoff(route_keyword: "Prioritizer")
              so the Prioritizer can update the plan and the Developer can retry.
            advanced:
              functionChoice: required
              context:
                - source: session_context
                  maxChars: 0
                  label: null
                - source: file:.fuseraft/artifacts/remediation-plan.json
                  maxChars: 6000
                  label: null
                - source: changes_recent:5
                  maxChars: 0
                  label: null
                - source: own_history:2
                  maxChars: 0
                  label: null
              capabilities:
                FileSystem: [read]

        flow:
          start: Auditor
          steps:
            Auditor:
              - to: Prioritizer
                when: audit complete
                requires: [anyFileWritten]
            Prioritizer:
              - to: Developer
                when: plan ready
                requires: [anyFileWritten]
            Developer:
              - to: Verifier
                when: fixes applied
                requires: [anyFileWritten]
            Verifier:
              - to: end
              - to: Prioritizer
                when: issues remain

        security:
          fileSystemSandboxPath: .

        advanced:
          compaction:
            triggerTurnCount: 30
            keepRecentTurns: 8
            mode: lossless
        """);
}
