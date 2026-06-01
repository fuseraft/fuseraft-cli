using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    /// <summary>
    /// Generates the default <c>devteam</c> template: Planner → Developer → Tester → Reviewer
    /// state-machine pipeline with evidence contracts, failure handling, lossless compaction,
    /// and a periodic Verifier agent that audits the evidence graph for inconsistencies.
    /// This is the most fully-featured template and serves as the reference implementation.
    /// </summary>
    private static GeneratedConfig DevTeam(string model, string? endpoint)
    {
        var planner = $"""
            Name: Planner
            Description: Analyses the task and writes a structured brief.
            Instructions: |
              You are a software architect and planner. Your job is to:
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
                 new files. All paths are relative to the sandbox root — never double-nest the
                 project directory name.
              3. Run a build or test command with shell_run to confirm correctness.
              4. Commit with git_add and git_commit.
              5. {ContextWriteStep}
              When done, call handoff(route_keyword: "HANDOFF TO TESTER").
              If the brief is missing or contradictory: handoff(route_keyword: "REPLAN REQUIRED").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Git
              - Changes
              - SessionContext
              - Handoff
            FunctionChoice: required
            MaxInTurnToolPairs: 12
            {DeveloperContextWindow}
            {AgentFileOptions}
            """;

        var tester = $"""
            Name: Tester
            Description: Writes and runs tests, produces a structured report.
            Instructions: |
              You are a QA engineer. Your job is to:
              1. {ContextReadStep}
              2. Read {FuseraftPaths.LocalBrief} to understand acceptance criteria.
              3. Write test scripts (any format) to {FuseraftPaths.LocalTests}/ and any
                 fixture or seed files to {FuseraftPaths.LocalTestFixtures}/. Run them with shell_run.
              4. Write results to {FuseraftPaths.LocalTestReport}:
                   passed — true if every criterion passes, false otherwise
                   results — array of objects:
                     PASS: name, status, exit_code, command (exact shell_run command — required)
                     FAIL: name, status, exit_code, command, output (relevant stderr/stdout from the failure — required)
              A PASS result with an empty or missing command field is treated as fabricated and will block handoff.
              Always write the report before routing, even when tests fail.
              5. {ContextWriteStep}
              If all pass, call handoff(route_keyword: "HANDOFF TO REVIEWER").
              If any fail, call handoff(route_keyword: "BUGS FOUND").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Changes
              - SessionContext
              - Handoff
            FunctionChoice: required
            MaxInTurnToolPairs: 12
            {TesterContextWindow}
            {AgentFileOptions}
            """;

        var reviewer = $"""
            Name: Reviewer
            Description: Reviews implementation and test results; gives final approval.
            Instructions: |
              You are a principal engineer. Your job is to:
              1. {ContextReadStep}
              2. Read the implementation files listed in {FuseraftPaths.LocalBrief} under
                 files_to_change, and {FuseraftPaths.LocalTestReport}. For any large file:
                 {LargeFileProtocolReviewer}
              3. Run at least one acceptance criterion as a spot-check with shell_run.
              If the code meets all acceptance criteria, call handoff(route_keyword: "APPROVED").
              If changes are needed, call handoff(route_keyword: "REVISION REQUIRED").
                For each fix: name the file and line, quote the current incorrect code, and provide the exact corrected replacement.
                Do not describe the problem in prose — provide the code change.
              If the plan is fundamentally wrong, call handoff(route_keyword: "REPLAN REQUIRED").
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

        var verifier = $"""
            Name: Verifier
            Description: Audits the evidence graph for inconsistencies between claims and recorded actions.
            Instructions: |
              You are an evidence auditor. Detect inconsistencies between what agents
              claim and what is recorded in the change log.

              1. Call changes_read_latest to see what was actually done this session.
              2. Compare recorded file writes, shell commands, and exit codes against
                 any claims made in recent conversation messages.
              3. If consistent: "Evidence verified — no inconsistencies found."
              4. If inconsistent: "INCONSISTENCY DETECTED: <what was claimed vs what the evidence shows>"
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - Changes
            FunctionChoice: required
            {VerifierContextWindow}
            {AgentFileOptions}
            """;

        var mainConfig = $"""
            Orchestration:
              Name: Software Development Team
              Description: >-
                Planner → Developer → Tester → Reviewer with state machine routing,
                evidence contracts, failure handling, and self-verification.

              Security:
                FileSystemSandboxPath: .   # set to your project root (e.g. ~/projects/myapp)

              EvidenceStore:
                Path: {FuseraftPaths.LocalEvidence}

              ChangeTracking:
                Path: {FuseraftPaths.LocalChanges}

              Validation:
                BriefPath: {FuseraftPaths.LocalBrief}
                TestReportPath: {FuseraftPaths.LocalTestReport}
                ChangeLogPath: {FuseraftPaths.LocalChanges}

              Contracts:
                - Name: BriefExists
                  Requires:
                    - Type: FileExists
                      Path: {FuseraftPaths.LocalBrief}

                - Name: ImplementationComplete
                  Requires:
                    - Type: FilesWritten
                      Source: {FuseraftPaths.LocalBrief}
                      Field: files_to_change
                    - Type: CommandSucceeded
                      Pattern: "build|compile"

                - Name: TestsValid
                  Requires:
                    - Type: FileExists
                      Path: {FuseraftPaths.LocalTestReport}
                    - Type: TestReport
                      NoFailures: true
                      HasAssertions: true

              FailureHandling:
                MissingEvidence:
                  Action: Reinstruct
                  Threshold: 3
                ConflictingEvidence:
                  Action: Reinstruct
                  Threshold: 2
                NoProgress:
                  Action: Abort
                  Threshold: 3
                # Hard backstop: escalate to HITL after this many consecutive contract
                # failures on any transition, regardless of per-type action. Prevents
                # Reinstruct from looping indefinitely when a contract cannot be satisfied.
                MaxConsecutiveContractFailures: 6
                # Escalate to HITL when an agent runs this many turns without emitting
                # any routing signal. Survives compaction — unlike the history-scan loop
                # warning — so it catches agents stuck after repeated compaction cycles.
                MaxConsecutiveTurnsWithoutSignal: 8

              Verifier:
                AgentName: Verifier
                EveryNTurns: 5
                TriggerOnSuspiciousTransition: true
                FindingsKeyword: INCONSISTENCY

              Compaction:
                TriggerTurnCount: 30
                KeepRecentTurns: 8
                Mode: lossless

              # WarnTurnTokens: warn when a single turn's input exceeds this value.
              # Keep this below ContextBudget.CutoverAt so the warning fires before
              # compaction is forced, giving an advance signal rather than a post-hoc note.
              WarnTurnTokens: 60000

              # ContextBudget: per-agent cumulative input-token thresholds. Warns before
              # context rot sets in, then triggers compaction automatically. Counters reset
              # after each compaction cycle so the session can run indefinitely.
              # MaxSingleTurnInputTokens guards against single-turn explosions that exhaust
              # the cumulative budget in one shot — compaction fires before the next turn.
              ContextBudget:
                WarnAt: 60000
                CutoverAt: 100000
                MaxSingleTurnInputTokens: 200000

              Events:
                Path: {FuseraftPaths.LocalEventsLog}

              # Each agent lives in its own YAML file in agents/ — edit, version, or reuse
              # them independently across configs. Inline fields override the file at load time.
              Agents:
                - AgentFile: agents/planner.yaml
                - AgentFile: agents/developer.yaml
                - AgentFile: agents/tester.yaml
                - AgentFile: agents/reviewer.yaml
                - AgentFile: agents/verifier.yaml

              Selection:
                Type: statemachine
                StateMachine:
                  Initial: Planning

                  States:
                    Planning:
                      Agent: Planner
                      Transitions:
                        - To: Implementation
                          Signal: "HANDOFF TO DEVELOPER"
                          Contract: BriefExists

                    Implementation:
                      Agent: Developer
                      Transitions:
                        - To: Testing
                          Signal: "HANDOFF TO TESTER"
                          Contract: ImplementationComplete
                        - To: Planning
                          Signal: "REPLAN REQUIRED"

                    Testing:
                      Agent: Tester
                      Transitions:
                        - To: Review
                          Signal: "HANDOFF TO REVIEWER"
                          Contract: TestsValid
                        - To: Implementation
                          Signal: "BUGS FOUND"

                    Review:
                      Agent: Reviewer
                      Transitions:
                        - To: Done
                          Signal: APPROVED
                        - To: Implementation
                          Signal: "REVISION REQUIRED"

                    Done:
                      Agent: Reviewer
                      Terminal: true

              Termination:
                Type: composite
                Strategies:
                  - Type: regex
                    Pattern: "\\bAPPROVED\\b"
                    AgentNames: [Reviewer]
                  - Type: maxiterations
                    MaxIterations: 60

              # ---------------------------------------------------------------------------
              # OPTIONAL EXTRAS — uncomment and fill in as needed
              # ---------------------------------------------------------------------------

              # MaxTotalTokens: 500000

              # McpServers:
              #   - Name: my-mcp-server
              #     Command: npx
              #     Args: [-y, "@modelcontextprotocol/server-filesystem", "."]

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
            ("agents/planner.yaml",   planner),
            ("agents/developer.yaml", developer),
            ("agents/tester.yaml",    tester),
            ("agents/reviewer.yaml",  reviewer),
            ("agents/verifier.yaml",  verifier),
        ]);
    }
}
