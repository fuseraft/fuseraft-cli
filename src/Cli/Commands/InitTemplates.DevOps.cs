using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    /// <summary>
    /// Generates the <c>devops</c> template: Planner → Developer → Operator state-machine pipeline
    /// for infrastructure and deployment tasks. The Operator executes the deployment and runs smoke
    /// tests; a <c>DEPLOYMENT_FAILED</c> back-edge returns to Developer for remediation.
    /// </summary>
    private static GeneratedConfig DevOps(string model, string? endpoint)
    {
        var planner = $"""
            Name: Planner
            Description: Designs the deployment or infrastructure plan.
            Instructions: |
              You are a DevOps architect. Your job is to:
              1. Understand the infrastructure or deployment task.
              2. Use sub_agent_explore to survey relevant config files and scripts. For any direct
                 file reads: call get_file_summary first (shows first 30 lines and file size),
                 grep_file to locate the relevant section, then read_file with startLine/maxLines
                 — never cold-read a large file in full.
              3. Check if {FuseraftPaths.LocalBrief} already exists. If it does, read it — if it
                 still covers the current task, call handoff(route_keyword: "PLANNING_COMPLETE")
                 immediately without rewriting it.
              4. Write a step-by-step execution plan to {FuseraftPaths.LocalBrief} with fields:
                   goal — what the deployment achieves
                   steps — ordered list of execution steps
                   rollback — steps to undo if something goes wrong
              When the plan is ready, call handoff(route_keyword: "PLANNING_COMPLETE").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - SubAgent
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var developer = $"""
            Name: Developer
            Description: Implements scripts, manifests, and config files.
            Instructions: |
              You are a DevOps engineer. Your job is to:
              1. Read the plan from {FuseraftPaths.LocalBrief} and implement all required
                 scripts, manifests, or config files using write_file.
              2. Run static analysis or validation with shell_run (e.g. lint, validate, check).
              3. Commit with git_add and git_commit when ready.
              When done, call handoff(route_keyword: "DEVELOPMENT_COMPLETE").
              If the plan is unclear, call handoff(route_keyword: "REPLAN_REQUIRED").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Git
              - Changes
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var operator_ = $"""
            Name: Operator
            Description: Executes the deployment and verifies success.
            Instructions: |
              You are a site reliability engineer. Your job is to:
              1. Execute the deployment steps from {FuseraftPaths.LocalBrief} using shell_run.
              2. Run smoke tests to verify the deployment succeeded.
              3. Report the outcome clearly with exact command output.
              If successful, call handoff(route_keyword: "DEPLOYMENT_COMPLETE").
              If failed, call handoff(route_keyword: "DEPLOYMENT_FAILED") and describe what went wrong.
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - Shell
              - Git
              - Changes
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var mainConfig = $"""
            Orchestration:
              Name: DevOps Team
              Description: >-
                Planner → Developer → Operator pipeline for infrastructure and deployment tasks.

              EvidenceStore:
                Path: {FuseraftPaths.LocalEvidence}

              Contracts:
                - Name: PlanExists
                  Requires:
                    - Type: FileExists
                      Path: {FuseraftPaths.LocalBrief}

                - Name: ArtifactsReady
                  Requires:
                    - Type: CommandSucceeded
                      Pattern: "lint|validate|check|test"

              FailureHandling:
                MissingEvidence:
                  Action: Reinstruct
                  Threshold: 3
                NoProgress:
                  Action: Abort
                  Threshold: 3

              # Each agent lives in its own YAML file in agents/ — edit, version, or reuse
              # them independently across configs. Inline fields override the file at load time.
              Agents:
                - AgentFile: agents/planner.yaml
                - AgentFile: agents/developer.yaml
                - AgentFile: agents/operator.yaml

              Selection:
                Type: statemachine
                StateMachine:
                  Initial: Planning

                  States:
                    Planning:
                      Agent: Planner
                      Transitions:
                        - To: Development
                          Signal: "PLANNING_COMPLETE"
                          Contract: PlanExists

                    Development:
                      Agent: Developer
                      Transitions:
                        - To: Operations
                          Signal: "DEVELOPMENT_COMPLETE"
                          Contract: ArtifactsReady
                        - To: Planning
                          Signal: "REPLAN_REQUIRED"

                    Operations:
                      Agent: Operator
                      Transitions:
                        - To: Done
                          Signal: "DEPLOYMENT_COMPLETE"
                        - To: Development
                          Signal: "DEPLOYMENT_FAILED"

                    Done:
                      Agent: Operator
                      Terminal: true

              Termination:
                Type: composite
                Strategies:
                  - Type: regex
                    Pattern: DEPLOYMENT_COMPLETE
                    AgentNames: [Operator]
                  - Type: maxiterations
                    MaxIterations: 20
            {OptionalSections(model, endpoint)}
            """;

        return new GeneratedConfig(mainConfig, [
            ("agents/planner.yaml",   planner),
            ("agents/developer.yaml", developer),
            ("agents/operator.yaml",  operator_),
        ]);
    }
}
