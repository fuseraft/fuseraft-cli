using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    /// <summary>
    /// Generates the <c>pipeline</c> template (replaces <c>graph</c>): Planner → Developer → Tester
    /// → Reviewer expressed as a declarative directed graph. Back-edges return control to earlier nodes
    /// without restarting the full pipeline. Developer and Tester have investigation tooling for
    /// structured failure tracking. Use <c>swe</c> for production work with evidence contracts.
    /// </summary>
    private static GeneratedConfig Pipeline(string model, string? endpoint)
    {
        var planner = $"""
            Name: Planner
            Description: Analyses the task and writes a structured brief.
            Instructions: |
              You are a software architect. Your job is to:
              1. {ContextReadStep}
              2. Read and understand the task thoroughly.
              3. Use sub_agent_explore for broad codebase questions without filling your context
                 with raw file contents. For any direct file reads: {LargeFileProtocol}
              4. Check if {FuseraftPaths.LocalBrief} already exists. If it does, read it — if it
                 still covers the current task, call handoff(route_keyword: "HANDOFF TO DEVELOPER")
                 immediately without rewriting it.
              5. Write a brief to {FuseraftPaths.LocalBrief} with fields:
                   goal — one-sentence description of what to build
                   files_to_change — array of paths RELATIVE TO THE SANDBOX ROOT
                     Correct:  src/module/file.py
                     Wrong:    project_name/src/module/file.py  (never prefix with the project dir)
                   acceptance_criteria — array of testable criteria the code must satisfy
              6. {ContextWriteStep}
              When done, call handoff(route_keyword: "HANDOFF TO DEVELOPER").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Search
              - SessionContext
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
              1. {ContextReadStep}
              2. Read {FuseraftPaths.LocalBrief} — implement every file in files_to_change.
                 Use patch_file for targeted edits to existing files; use write_file only for
                 new files. All paths are relative to the sandbox root.
                 The Execution State and Investigation Log in your context show what has already
                 failed this session. Do not repeat an approach listed under "Rejected Paths".
              3. Run a build command with shell_run to confirm it compiles.
                 If it fails, record the failed approach before trying another:
                 a. Call create_hypothesis(description) naming the specific approach.
                 b. If it fails: call reject_hypothesis(id, reason, evidence) with the exact
                    error. Read the source of the failure before writing new code.
                 c. If it passes: call confirm_hypothesis(id, evidence).
                 You MUST NOT call handoff with any open hypotheses.
              4. Commit with git_add and git_commit.
              5. {ContextWriteStep}
              When done, call handoff(route_keyword: "HANDOFF TO TESTER").
              If the brief is unclear or needs rethinking, call handoff(route_keyword: "REPLAN REQUIRED").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Git
              - Changes
              - Investigation
              - SessionContext
              - Handoff
            FunctionChoice: required
            MaxInTurnToolPairs: 12
            {DeveloperContextWindow}
            {AgentFileOptions}
            """;

        var tester = $"""
            Name: Tester
            Description: Writes and runs tests, produces a structured test report.
            Instructions: |
              You are a QA engineer. Your job is to:
              1. {ContextReadStep}
              2. Read {FuseraftPaths.LocalBrief} to understand the acceptance criteria.
              3. Write test scripts (any format) to {FuseraftPaths.LocalTests}/ and any
                 fixture or seed files to {FuseraftPaths.LocalTestFixtures}/. Run them with shell_run.
              4. Write results to {FuseraftPaths.LocalTestReport}:
                   passed — true if every criterion passes, false otherwise
                   results — array of objects:
                     PASS: name, status, exit_code, command (exact shell_run command — required)
                     FAIL: name, status, exit_code, command, output (relevant stderr/stdout from the failure — required)
              A PASS result with an empty or missing command field is treated as fabricated and will block handoff.
              Always write the report before routing, even when tests fail.
              If a test failure reveals a clear root cause (wrong return value, missing
              dependency, incorrect wiring), call identify_root_cause(cause) before routing.
              5. {ContextWriteStep}
              If all tests pass, call handoff(route_keyword: "HANDOFF TO REVIEWER").
              If any tests fail, call handoff(route_keyword: "BUGS FOUND").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Changes
              - Investigation
              - SessionContext
              - Handoff
            FunctionChoice: required
            MaxInTurnToolPairs: 12
            {TesterContextWindow}
            {AgentFileOptions}
            """;

        var reviewer = $"""
            Name: Reviewer
            Description: Reviews implementation and test results; gives final approval or requests changes.
            Instructions: |
              You are a principal engineer. Your job is to:
              1. {ContextReadStep}
              2. Read the implementation files listed in {FuseraftPaths.LocalBrief} under
                 files_to_change, and {FuseraftPaths.LocalTestReport}. For any large file:
                 {LargeFileProtocolReviewer}
              3. Run at least one acceptance criterion as a spot-check with shell_run.
              4. Emit a JSON review block listing each acceptance criterion with verdict (PASS/FAIL)
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
              - SessionContext
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
              Name: Pipeline
              Description: >-
                Planner → Developer → Tester → Reviewer as a directed graph. Developer and Tester
                have investigation tooling for structured failure tracking. Back-edges return to earlier
                nodes without restarting. For evidence contracts and full safeguards, use the swe template.

              Security:
                FileSystemSandboxPath: .   # set to your project root (e.g. ~/projects/myapp)

              ChangeTracking:
                Path: {FuseraftPaths.LocalChanges}

              Validation:
                BriefPath: {FuseraftPaths.LocalBrief}
                TestReportPath: {FuseraftPaths.LocalTestReport}
                ChangeLogPath: {FuseraftPaths.LocalChanges}

              Events:
                Path: {FuseraftPaths.LocalEventsLog}

              WarnTurnTokens: 300000

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
              #   Path: {FuseraftPaths.LocalCheckpoints}

              # Models:
              #   fast:
              #     ModelId: {model}
              #   reasoning:
              #     ModelId: {model}
              #     ReasoningEffort: low
            """;

        return new GeneratedConfig(mainConfig, [
            ("agents/planner.yaml",    planner),
            ("agents/developer.yaml",  developer),
            ("agents/tester.yaml",     tester),
            ("agents/reviewer.yaml",   reviewer),
            ("agents/approved.yaml",   approved),
        ]);
    }
}
