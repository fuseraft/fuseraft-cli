using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    /// <summary>
    /// Generates the <c>audit</c> template: Auditor → Prioritizer → Developer → Verifier directed
    /// graph for security, quality, and compliance audits. The Auditor writes a machine-readable
    /// findings report; the Prioritizer triages by severity; the Developer applies fixes in priority
    /// order with hypothesis tracking; the Verifier confirms each finding is addressed.
    /// </summary>
    private static GeneratedConfig Audit(string model, string? endpoint)
    {
        var auditor = $"""
            Name: Auditor
            Description: Scans the codebase for security, quality, correctness, and compliance issues.
            Instructions: |
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
                 - Use grep_file / sub_agent_explore for pattern matching and structural analysis.
                 - Use shell_run for static analysis tools (e.g. semgrep, bandit, eslint, clippy).
                 - Use read_file (with startLine/maxLines) to read relevant code sections in full.
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
              When the scan is complete, call handoff(route_keyword: "AUDIT COMPLETE").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Search
              - Shell
              - SubAgent
              - Investigation
              - AuditFindings
              - Handoff
            Capabilities:
              FileSystem: [read]
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var prioritizer = $"""
            Name: Prioritizer
            Description: Triages audit findings by severity and writes an ordered remediation plan.
            Instructions: |
              You are a triage engineer. Your job is to:
              1. Read {FuseraftPaths.LocalAuditFindings} and understand every finding.
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
              When the plan is ready, call handoff(route_keyword: "PLAN READY").

              You are read-only with respect to this project's own files — you have no
              write_file/patch_file access. write_file_remediation_plan is the only way to
              persist this plan; fixing the findings yourself is the Developer's job, not yours.
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - RemediationPlan
              - Handoff
            Capabilities:
              FileSystem: [read]
            FunctionChoice: required
            Context:
              - Source: session_context
              - Source: file:.fuseraft/artifacts/audit-findings.json
                MaxChars: 6000
              - Source: own_history:2
            {AgentFileOptions}
            """;

        var developer = $"""
            Name: Developer
            Description: Applies fixes in remediation-plan priority order with hypothesis tracking.
            Instructions: |
              You are a developer remediating audit findings. Your job is to:
              1. Read {FuseraftPaths.LocalRemediationPlan} to get the ordered action items.
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
              When all actionable items are addressed, call handoff(route_keyword: "FIXES APPLIED").
              If you are blocked on an item (requires infrastructure changes, out of scope, etc.),
              document the reason in the remediation plan and call handoff(route_keyword: "FIXES APPLIED")
              for the completed items, noting what was skipped and why.
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Git
              - Changes
              - Investigation
              - Handoff
            FunctionChoice: required
            MaxInTurnToolPairs: 12
            {DeveloperContextWindow}
            {AgentFileOptions}
            """;

        var verifier = $"""
            Name: Verifier
            Description: Confirms each finding is addressed; routes back for any that remain open.
            Instructions: |
              You are a verification engineer. Your job is to:
              1. Read {FuseraftPaths.LocalAuditFindings} to get the original finding list.
              2. Read {FuseraftPaths.LocalRemediationPlan} to get the action items and verify hints.
              3. For each action item that the Developer addressed:
                 - Run the verify_hint command (or a targeted check) with shell_run.
                 - Record: finding_id, check performed, exit code, relevant output.
              4. Produce a verification report:
                 - VERIFIED: finding_id — what was checked and confirmed
                 - UNRESOLVED: finding_id — what the check found and why the fix didn't hold
              If all addressed findings are verified, call handoff(route_keyword: "VERIFIED").
              If any findings remain unresolved, call handoff(route_keyword: "ISSUES REMAIN")
              so the Prioritizer can update the plan and the Developer can retry.
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Changes
              - Handoff
            Capabilities:
              FileSystem: [read]
            FunctionChoice: required
            Context:
              - Source: session_context
              - Source: file:.fuseraft/artifacts/remediation-plan.json
                MaxChars: 6000
              - Source: changes_recent:5
              - Source: own_history:2
            {AgentFileOptions}
            """;

        var mainConfig = $"""
            Orchestration:
              Name: Audit Pipeline
              Description: >-
                Auditor scans for security, quality, and compliance issues; Prioritizer triages
                by severity; Developer applies fixes with hypothesis tracking; Verifier confirms.
                ISSUES REMAIN back-edges return to Prioritizer for replanning.

              Security:
                FileSystemSandboxPath: .   # set to your project root (e.g. ~/projects/myapp)

              EvidenceStore:
                Path: {FuseraftPaths.LocalEvidence}

              Contracts:
                - Name: AuditComplete
                  Requires:
                    - Type: FileExists
                      Path: {FuseraftPaths.LocalAuditFindings}

                - Name: PlanComplete
                  Requires:
                    - Type: FileExists
                      Path: {FuseraftPaths.LocalRemediationPlan}

              ChangeTracking:
                Path: {FuseraftPaths.LocalChanges}

              Events:
                Path: {FuseraftPaths.LocalEventsLog}

              # Each agent lives in its own YAML file in agents/ — edit, version, or reuse
              # them independently across configs.
              Agents:
                - AgentFile: agents/auditor.yaml
                - AgentFile: agents/prioritizer.yaml
                - AgentFile: agents/developer.yaml
                - AgentFile: agents/verifier.yaml

              Selection:
                Type: graph
                Graph:
                  EntryNode: audit
                  MaxRetries: 3

                  Nodes:
                    - Id: audit
                      Agent: Auditor
                    - Id: prioritizer
                      Agent: Prioritizer
                    - Id: developer
                      Agent: Developer
                    - Id: verifier
                      Agent: Verifier
                    - Id: done
                      Agent: Verifier
                      Terminal: true

                  Edges:
                    # Forward edges
                    - From: audit
                      To: prioritizer
                      Keyword: "AUDIT COMPLETE"
                      Validators: [RequireWriteFile]       # blocks until audit-findings.json exists

                    - From: prioritizer
                      To: developer
                      Keyword: "PLAN READY"
                      Validators: [RequireWriteFile]       # blocks until remediation-plan.json exists

                    - From: developer
                      To: verifier
                      Keyword: "FIXES APPLIED"
                      Validators: [RequireWriteFile]       # blocks until at least one file is patched

                    - From: verifier
                      To: done
                      Keyword: "VERIFIED"

                    # Back-edge
                    - From: verifier
                      To: prioritizer
                      Keyword: "ISSUES REMAIN"             # update plan and retry Developer

              Termination:
                Type: composite
                Strategies:
                  - Type: regex
                    Pattern: "\\bVERIFIED\\b"
                    AgentNames: [Verifier]
                  - Type: maxiterations
                    MaxIterations: 40

              Compaction:
                TriggerTurnCount: 30
                KeepRecentTurns: 8
                Mode: lossless

              # ContextBudget: per-agent cumulative input-token thresholds.
              # ContextBudget:
              #   WarnAt: 60000
              #   CutoverAt: 100000

              # Checkpoint:
              #   Mode: json
              #   Path: {FuseraftPaths.LocalCheckpoints}
            """;

        return new GeneratedConfig(mainConfig, [
            ("agents/auditor.yaml",     auditor),
            ("agents/prioritizer.yaml", prioritizer),
            ("agents/developer.yaml",   developer),
            ("agents/verifier.yaml",    verifier),
        ]);
    }
}
