using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    private static GeneratedConfig DevOps(string model, string? endpoint) => GeneratedConfig.Inline($$"""
        team: DevOps Pipeline
        description: OpsPlanner → Executor → Verifier with rollback handling. The ops plan includes verify_command and rollback_command; if health checks fail the Executor runs the rollback steps and the Verifier confirms a known-good state.

        models:
          default:
            modelId: {{model}}{{Ep(endpoint, "    ")}}

        agents:
          OpsPlanner:
            role: Designs the operations plan including rollback strategy.
            model: default
            tools: [FileSystem, SessionContext, Subagent, OpsPlan]
            instructions: |
              You are a DevOps architect. Your job is to:
              1. {{ContextReadStep}}
              2. Understand the infrastructure or deployment task in full.
              3. Use subagent_explore to survey relevant config files, scripts, and manifests.
                 For any direct file reads: {{LargeFileProtocol}}
              4. Check if .fuseraft/artifacts/ops-plan.yaml already exists. If it does, read it — if it
                 still covers the current task, call handoff(route_keyword: "Executor") immediately.
              5. Call write_file_ops_plan(content: ..., format: "yaml"). content must be YAML
                 with these top-level fields:
                   goal           — what the operation achieves (one sentence)
                   steps          — ordered list of exact shell commands to execute
                   verify_command — the exact command to confirm success (health check, smoke test)
                   rollback_command — the single command to run if verify fails (e.g. "helm rollback")
                   rollback_steps — ordered list of exact shell commands for manual rollback
                                    (used when rollback_command is insufficient)
                   notes          — any warnings, known dependencies, or timing constraints
              6. {{ContextWriteStep}}
              When the plan is ready, call handoff(route_keyword: "Executor").

              You are read-only with respect to this project's own files — you have no
              write_file/patch_file access. write_file_ops_plan is the only way to persist this
              plan; running the operation is the Executor's job, not yours.
            advanced:
              functionChoice: required
              capabilities:
                FileSystem: [read]
          Executor:
            role: Runs the ops plan steps or rollback steps and records every exit code.
            model: default
            tools: [Shell, FileSystem, Git, Changes, SessionContext]
            instructions: |
              You are a site reliability engineer executing an operations plan. Your job is to:
              1. {{ContextReadStep}}
              2. Read .fuseraft/artifacts/ops-plan.yaml. Check whether this is a forward execution
                 or a rollback (the handoff context will say "ROLLBACK REQUIRED" if rolling back).

                 FORWARD EXECUTION:
                 - Run each command in the plan's steps array in order using shell_run.
                 - Record the exit code and relevant output for each step.
                 - If any step exits non-zero, stop immediately and call
                   handoff(route_keyword: "OpsPlanner") with the exact error output.
                 - If all steps succeed, call handoff(route_keyword: "Verifier").

                 ROLLBACK EXECUTION:
                 - Run rollback_command first. If that exits 0, call
                   handoff(route_keyword: "Verifier").
                 - If rollback_command fails or is absent, run each command in rollback_steps.
                 - Report outcome: call handoff(route_keyword: "Verifier") if rollback
                   succeeded, or handoff(route_keyword: "OpsPlanner") if it did not.
              3. {{ContextWriteStep}}
            advanced:
              functionChoice: required
              capabilities:
                FileSystem: [read]
              maxInTurnToolPairs: 12
          Verifier:
            role: Runs health checks from the ops plan; triggers rollback if checks fail.
            model: default
            tools: [Shell, FileSystem, Changes, SessionContext]
            instructions: |
              You are a site reliability engineer verifying an operation. Your job is to:
              1. {{ContextReadStep}}
              2. Read .fuseraft/artifacts/ops-plan.yaml and run verify_command with shell_run.
              3. Evaluate the output:
                 - If verify_command exits 0 and the output indicates healthy state:
                   call handoff(route_keyword: "end").
                 - If verify_command exits non-zero or output indicates failure:
                   Report the exact command, exit code, and relevant output.
                   call handoff(route_keyword: "Executor") so the Executor
                   can run the rollback steps.
              4. {{ContextWriteStep}}
            advanced:
              functionChoice: required
              context:
                - source: session_context
                - source: file:.fuseraft/artifacts/ops-plan.yaml
                  maxChars: 4000
                - source: changes_recent:3
                - source: own_history:2
              capabilities:
                FileSystem: [read]

        flow:
          start: OpsPlanner
          steps:
            OpsPlanner:
              - to: Executor
                when: plan ready
                requires:
                  - fileExists: .fuseraft/artifacts/ops-plan.yaml
            Executor:
              - to: Verifier
                when: execution complete
              - to: OpsPlanner
                when: execution failed
            Verifier:
              - to: end
                when: ops verified
              - to: Executor
                when: rollback required

        limits:
          maxTurns: 20

        advanced:
          compaction:
            triggerTurnCount: 30
            keepRecentTurns: 8
            mode: lossless
        """);
}
