using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    /// <summary>
    /// Generates the <c>devops</c> template: OpsPlanner → Executor → Verifier state-machine pipeline
    /// for infrastructure and deployment tasks. The ops plan includes <c>rollback_command</c> and
    /// <c>rollback_steps</c>; the Verifier can trigger a rollback cycle if health checks fail.
    /// </summary>
    private static GeneratedConfig DevOps(string model, string? endpoint)
    {
        var planner = $"""
            Name: OpsPlanner
            Description: Designs the operations plan including rollback strategy.
            Instructions: |
              You are a DevOps architect. Your job is to:
              1. {ContextReadStep}
              2. Understand the infrastructure or deployment task in full.
              3. Use sub_agent_explore to survey relevant config files, scripts, and manifests.
                 For any direct file reads: {LargeFileProtocol}
              4. Check if {FuseraftPaths.LocalOpsPlan} already exists. If it does, read it — if it
                 still covers the current task, call handoff(route_keyword: "PLAN READY") immediately.
              5. Call write_file_ops_plan(content: ..., format: "yaml"). content must be YAML
                 with these top-level fields:
                   goal           — what the operation achieves (one sentence)
                   steps          — ordered list of exact shell commands to execute
                   verify_command — the exact command to confirm success (health check, smoke test)
                   rollback_command — the single command to run if verify fails (e.g. "helm rollback")
                   rollback_steps — ordered list of exact shell commands for manual rollback
                                    (used when rollback_command is insufficient)
                   notes          — any warnings, known dependencies, or timing constraints
              6. {ContextWriteStep}
              When the plan is ready, call handoff(route_keyword: "PLAN READY").

              You are read-only with respect to this project's own files — you have no
              write_file/patch_file access. write_file_ops_plan is the only way to persist this
              plan; running the operation is the Executor's job, not yours.
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - SessionContext
              - SubAgent
              - OpsPlan
              - Handoff
            Capabilities:
              FileSystem: [read]
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var executor = $"""
            Name: Executor
            Description: Runs the ops plan steps or rollback steps and records every exit code.
            Instructions: |
              You are a site reliability engineer executing an operations plan. Your job is to:
              1. {ContextReadStep}
              2. Read {FuseraftPaths.LocalOpsPlan}. Check whether this is a forward execution
                 or a rollback (the handoff context will say "ROLLBACK REQUIRED" if rolling back).

                 FORWARD EXECUTION:
                 - Run each command in the plan's steps array in order using shell_run.
                 - Record the exit code and relevant output for each step.
                 - If any step exits non-zero, stop immediately and call
                   handoff(route_keyword: "EXECUTION FAILED") with the exact error output.
                 - If all steps succeed, call handoff(route_keyword: "EXECUTION COMPLETE").

                 ROLLBACK EXECUTION:
                 - Run rollback_command first. If that exits 0, call
                   handoff(route_keyword: "EXECUTION COMPLETE").
                 - If rollback_command fails or is absent, run each command in rollback_steps.
                 - Report outcome: call handoff(route_keyword: "EXECUTION COMPLETE") if rollback
                   succeeded, or handoff(route_keyword: "EXECUTION FAILED") if it did not.
              3. {ContextWriteStep}
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - Shell
              - FileSystem
              - Git
              - Changes
              - SessionContext
              - Handoff
            Capabilities:
              FileSystem: [read]
            FunctionChoice: required
            MaxInTurnToolPairs: 12
            {AgentFileOptions}
            """;

        var verifier = $"""
            Name: Verifier
            Description: Runs health checks from the ops plan; triggers rollback if checks fail.
            Instructions: |
              You are a site reliability engineer verifying an operation. Your job is to:
              1. {ContextReadStep}
              2. Read {FuseraftPaths.LocalOpsPlan} and run verify_command with shell_run.
              3. Evaluate the output:
                 - If verify_command exits 0 and the output indicates healthy state:
                   call handoff(route_keyword: "OPS VERIFIED").
                 - If verify_command exits non-zero or output indicates failure:
                   Report the exact command, exit code, and relevant output.
                   call handoff(route_keyword: "ROLLBACK REQUIRED") so the Executor
                   can run the rollback steps.
              4. {ContextWriteStep}
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - Shell
              - FileSystem
              - Changes
              - SessionContext
              - Handoff
            Capabilities:
              FileSystem: [read]
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var mainConfig = $"""
            Orchestration:
              Name: DevOps Pipeline
              Description: >-
                OpsPlanner → Executor → Verifier with rollback handling. The ops plan includes
                verify_command and rollback_command; if health checks fail the Executor runs the
                rollback steps and the Verifier confirms a known-good state.

              EvidenceStore:
                Path: {FuseraftPaths.LocalEvidence}

              Contracts:
                - Name: PlanReady
                  Requires:
                    - Type: FileExists
                      Path: {FuseraftPaths.LocalOpsPlan}

              ChangeTracking:
                Path: {FuseraftPaths.LocalChanges}

              FailureHandling:
                MissingEvidence:
                  Action: Reinstruct
                  Threshold: 3
                NoProgress:
                  Action: Abort
                  Threshold: 3

              # Each agent lives in its own YAML file in agents/ — edit, version, or reuse
              # them independently across configs.
              Agents:
                - AgentFile: agents/ops-planner.yaml
                - AgentFile: agents/executor.yaml
                - AgentFile: agents/verifier.yaml

              Selection:
                Type: statemachine
                StateMachine:
                  Initial: Planning

                  States:
                    Planning:
                      Agent: OpsPlanner
                      Transitions:
                        - To: Execution
                          Signal: "PLAN READY"
                          Contract: PlanReady

                    Execution:
                      Agent: Executor
                      Transitions:
                        - To: Verification
                          Signal: "EXECUTION COMPLETE"
                        - To: Planning
                          Signal: "EXECUTION FAILED"

                    Verification:
                      Agent: Verifier
                      Transitions:
                        - To: Done
                          Signal: "OPS VERIFIED"
                        - To: Execution
                          Signal: "ROLLBACK REQUIRED"

                    Done:
                      Agent: Verifier
                      Terminal: true

              Termination:
                Type: composite
                Strategies:
                  - Type: regex
                    Pattern: "OPS VERIFIED"
                    AgentNames: [Verifier]
                  - Type: maxiterations
                    MaxIterations: 20

              Events:
                Path: {FuseraftPaths.LocalEventsLog}
            {OptionalSections(model, endpoint)}
            """;

        return new GeneratedConfig(mainConfig, [
            ("agents/ops-planner.yaml", planner),
            ("agents/executor.yaml",    executor),
            ("agents/verifier.yaml",    verifier),
        ]);
    }
}
