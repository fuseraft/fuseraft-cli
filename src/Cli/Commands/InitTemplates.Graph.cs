using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    /// <summary>
    /// Generates the <c>graph</c> template: Planner → Developer → Tester → Reviewer expressed as a
    /// declarative directed graph. Back-edges (<c>BUGS FOUND</c>, <c>REVISION REQUIRED</c>,
    /// <c>REPLAN REQUIRED</c>) return control to earlier nodes without restarting the full pipeline.
    /// <c>APPROVED</c> routes to a lightweight terminal <c>Approved</c> node that ends the session.
    /// </summary>
    private static GeneratedConfig Graph(string model, string? endpoint)
    {
        var planner = $"""
            Name: Planner
            Description: Analyses the task and writes a structured brief.
            Instructions: |
              You are a software architect. Your job is to:
              1. Read and understand the task thoroughly.
              2. Use sub_agent_explore for broad codebase questions without filling your context
                 with raw file contents. For any direct file reads: call get_file_summary first
                 (shows first 30 lines and file size), grep_file to locate the relevant section,
                 then read_file with startLine/maxLines for that section only — files can exceed
                 10,000 lines; never cold-read a large file in full.
              3. Check if {FuseraftPaths.LocalBrief} already exists. If it does, read it — if it
                 still covers the current task, call handoff(route_keyword: "HANDOFF TO DEVELOPER")
                 immediately without rewriting it.
              4. Write a brief to {FuseraftPaths.LocalBrief} with fields:
                   goal — one-sentence description of what to build
                   files_to_change — array of file paths to create or modify
                   acceptance_criteria — array of testable criteria the code must satisfy
              5. Break work into concrete steps for the Developer.
              When done, call handoff(route_keyword: "HANDOFF TO DEVELOPER").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Search
              - SubAgent
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var developer = $"""
            Name: Developer
            Description: Implements the changes described in the brief.
            Instructions: |
              You are a senior software engineer. Your job is to:
              1. Read {FuseraftPaths.LocalBrief} and implement every listed file using write_file.
              2. Run a build command with shell_run to confirm it compiles.
              3. Commit your work with git_add and git_commit.
              When done, call handoff(route_keyword: "HANDOFF TO TESTER").
              If the brief is unclear or needs rethinking, call handoff(route_keyword: "REPLAN REQUIRED").
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

        var tester = $"""
            Name: Tester
            Description: Writes and runs tests, produces a structured test report.
            Instructions: |
              You are a QA engineer. Your job is to:
              1. Read {FuseraftPaths.LocalBrief} to understand the acceptance criteria.
              2. Write test scripts (any format) to {FuseraftPaths.LocalTests}/ and any
                 fixture or seed files to {FuseraftPaths.LocalTestFixtures}/. Run them with shell_run.
              3. Write results to {FuseraftPaths.LocalTestReport}:
                   passed — true if every criterion passes, false otherwise
                   results — array of objects:
                     PASS: name, status, exit_code, command (exact shell_run command — required)
                     FAIL: name, status, exit_code, command, output (relevant stderr/stdout from the failure — required)
              A PASS result with an empty or missing command field is treated as fabricated and will block handoff.
              Always write the report before routing, even when tests fail.
              If all tests pass, call handoff(route_keyword: "HANDOFF TO REVIEWER").
              If any tests fail, call handoff(route_keyword: "BUGS FOUND").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Changes
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var reviewer = $"""
            Name: Reviewer
            Description: Reviews implementation and test results; gives final approval or requests changes.
            Instructions: |
              You are a principal engineer. Your job is to:
              1. Read the implementation files listed in {FuseraftPaths.LocalBrief} under
                 files_to_change, and {FuseraftPaths.LocalTestReport}. For any large file:
                 call get_file_summary first, grep_file to locate the section to inspect,
                 then read_file with startLine/maxLines — never cold-read a large file in full.
              2. Run at least one acceptance criterion as a spot-check with shell_run.
              3. Emit a JSON review block listing each acceptance criterion with verdict (PASS/FAIL)
                 and evidence before your routing keyword.
              If all criteria pass, call handoff(route_keyword: "APPROVED").
              If targeted fixes are needed, call handoff(route_keyword: "REVISION REQUIRED").
                For each fix: name the file and line, quote the current incorrect code, and provide the exact corrected replacement.
                Do not describe the problem in prose — provide the code change.
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Changes
              - Handoff
            FunctionChoice: auto
            ContextWindow:
              TextOnly: true
            {AgentFileOptions}
            """;

        var approved = $"""
            Name: Approved
            Description: Terminal confirmation node — emits a one-line completion summary.
            Instructions: |
              All acceptance criteria have already been verified and approved.
              Write exactly one sentence confirming the task is complete. Nothing else.
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            FunctionChoice: none
            {AgentFileOptions}
            """;

        var mainConfig = $"""
            Orchestration:
              Name: Graph Pipeline
              Description: >-
                Planner → Developer → Tester → Reviewer expressed as a declarative directed graph.
                Back-edges (BUGS FOUND, REVISION REQUIRED, REPLAN REQUIRED) return to earlier nodes
                without restarting the full pipeline. APPROVED routes to a terminal confirmation node.

              ChangeTracking:
                Path: {FuseraftPaths.LocalChanges}

              Validation:
                BriefPath: {FuseraftPaths.LocalBrief}
                TestReportPath: {FuseraftPaths.LocalTestReport}
                ChangeLogPath: {FuseraftPaths.LocalChanges}

              Events:
                Path: {FuseraftPaths.LocalEventsLog}

              # Each agent lives in its own YAML file in agents/ — edit, version, or reuse
              # them independently across configs. Inline fields override the file at load time.
              Agents:
                - AgentFile: agents/planner.yaml
                - AgentFile: agents/developer.yaml
                - AgentFile: agents/tester.yaml
                - AgentFile: agents/reviewer.yaml
                - AgentFile: agents/approved.yaml

              Selection:
                Type: graph
                Graph:
                  EntryNode: planner
                  MaxRetries: 4   # max consecutive validator failures per node before HITL escalation

                  # Nodes bind agents to named positions in the graph. IDs are stable,
                  # lowercase, and appear in event logs. Multiple nodes may share an agent.
                  Nodes:
                    - Id: planner
                      Agent: Planner
                    - Id: developer
                      Agent: Developer
                    - Id: tester
                      Agent: Tester
                    - Id: reviewer
                      Agent: Reviewer             # routes on keyword — NOT terminal
                    - Id: approved                # terminal node — session ends after this run
                      Agent: Approved
                      Terminal: true

                  # Edges define control flow — first matching edge fires each turn.
                  # Forward edges (target has higher BFS layer) use SendMessage within the
                  # current MAF phase. Back-edges (lower layer) yield and restart the phase
                  # loop from the target node, enabling cycles without a DAG violation.
                  Edges:
                    # Forward edges
                    - From: planner
                      To: developer
                      Keyword: "HANDOFF TO DEVELOPER"
                      Validators: [RequireBrief]      # blocks until brief.json is valid

                    - From: developer
                      To: tester
                      Keyword: "HANDOFF TO TESTER"
                      Validators: [RequireWriteFile]  # blocks until at least one file is written

                    - From: tester
                      To: reviewer
                      Keyword: "HANDOFF TO REVIEWER"
                      Validators: [TestReportValid]   # blocks until test-report.json passes

                    - From: reviewer
                      To: approved
                      Keyword: "APPROVED"
                      Validators: [RequireReviewJudgement]  # gate on edge; terminal node runs clean

                    # Back-edges (cycles)
                    - From: tester
                      To: developer
                      Keyword: "BUGS FOUND"           # test failures → back to developer

                    - From: reviewer
                      To: developer
                      Keyword: "REVISION REQUIRED"    # review feedback → back to developer

                    - From: developer
                      To: planner
                      Keyword: "REPLAN REQUIRED"      # unclear brief → back to planner

              Termination:
                Type: composite
                Strategies:
                  - Type: regex
                    Pattern: "\\bAPPROVED\\b"
                    AgentNames: [Reviewer]
                  - Type: maxiterations
                    MaxIterations: 60

              # ---------------------------------------------------------------------------
              # OPTIONAL EXTRAS — uncomment as needed
              # ---------------------------------------------------------------------------

              # EvidenceStore:              # required for evidence contracts
              #   Path: {FuseraftPaths.LocalEvidence}

              # Contracts:
              #   - Name: BriefExists
              #     Requires:
              #       - Type: FileExists
              #         Path: {FuseraftPaths.LocalBrief}

              # Security:
              #   FileSystemSandboxPath: ~/my-project

              Compaction:
                TriggerTurnCount: 30
                KeepRecentTurns: 8
                Mode: lossless

              # ContextBudget: per-agent cumulative input-token thresholds. Warns before
              # context rot sets in, then triggers compaction automatically. Requires Compaction.
              # ContextBudget:
              #   WarnAt: 80000
              #   CutoverAt: 120000

              # Checkpoint:
              #   Mode: json
              #   Path: .fuseraft/checkpoints

              # Models:
              #   fast:
              #     ModelId: {model}
              #   reasoning:
              #     ModelId: {model}
            """;

        return new GeneratedConfig(mainConfig, [
            ("agents/planner.yaml",   planner),
            ("agents/developer.yaml", developer),
            ("agents/tester.yaml",    tester),
            ("agents/reviewer.yaml",  reviewer),
            ("agents/approved.yaml",  approved),
        ]);
    }
}
