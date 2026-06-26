using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    /// <summary>
    /// Generates the <c>greenfield</c> template:
    /// Preflight → Planner → Developer → Tester → Reviewer.
    ///
    /// Differences from <c>swe</c>:
    /// <list type="bullet">
    ///   <item>No PlannerCritic — the Planner self-critiques with greenfield-specific rules instead.</item>
    ///   <item>No Verifier — reduces overhead and avoids the verify_command confusion seen on pure-new-file tasks.</item>
    ///   <item>Planner always reads brief-review before re-handing off (fixes the short-circuit-on-stale-brief bug).</item>
    ///   <item>Planner enforces greenfield rules: manifest required, no test files, verify_command must be a smoke test.</item>
    ///   <item>ImplementationComplete drops the secondary pattern match — only the verify_command itself must succeed.</item>
    ///   <item>Developer gets a larger per-turn context window for writing multiple new files at once.</item>
    ///   <item>Tester receives changes_recent so it can detect Developer fixes and re-run automatically.</item>
    /// </list>
    /// </summary>
    private static GeneratedConfig Greenfield(string model, string? endpoint)
    {
        var preflight = $"""
            Name: Preflight
            Description: Validates the execution environment before planning begins.
            Instructions: |
              You are an environment validator. Run exactly once, at session start.
              Your job is to confirm the sandbox is ready before any code is written.
              Complete these steps in order, then route.

              STEP 1 — SCAN SANDBOX
              Call list_directory on "." to confirm the sandbox root exists and see
              its top-level contents. Note everything present.

              STEP 2 — DETECT PROJECT TYPE
              Call path_exists for each indicator file below:
                Python: pyproject.toml, setup.py, requirements.txt, setup.cfg
                Node:   package.json
                Rust:   Cargo.toml
                .NET:   global.json  (also call list_files(".", "*.csproj") — any hit = .NET)
                Go:     go.mod
              Record every type whose file is present. If none match, type = "unknown".

              STEP 3 — VERIFY RUNTIME(S)
              For each detected type, run the version command below:
                Python: shell_run("python3 --version")  [fallback: shell_run("python --version")]
                Node:   shell_run("node --version")
                Rust:   shell_run("rustc --version")
                .NET:   shell_run("dotnet --version")
                Go:     shell_run("go version")
              If type = "unknown", run all five to detect what is available.
              Exit 0 = runtime present. Exit 127 or 128 = missing.

              STEP 4 — CHECK GIT
              git_is_inside_work_tree()
                Returns "true"  → git repo. Also run git_status() and note whether
                                  the working tree is clean (no lines beyond the branch header).
                Returns "false" → not a git repo. Record this — agents will skip git steps.

              STEP 5 — WRITE PREFLIGHT REPORT
              Call write_file_preflight(content: ..., format: "json"). content must be a JSON
              object with exactly these top-level fields:
                project_types    — array of detected types, e.g. ["python"]
                runtime_versions — array, each entry "runtime: version", e.g. ["python3: 3.12.1"]
                missing_runtimes — array of runtimes that returned exit 127/128
                git_repo         — boolean: true if git_is_inside_work_tree() returned "true"
                git_clean        — boolean or null: true if git_status() output has no changed-file lines
                warnings         — array of non-fatal observations
              You are read-only with respect to this project's own files — you have no
              write_file/patch_file access. write_file_preflight is the only way to persist
              this report; implementing the task itself is the Developer's job, not yours.

              STEP 6 — DETERMINE OUTCOME
              FAILURE condition: a specific project type was detected (not "unknown")
              AND its primary runtime is missing (exit 127/128 from step 3).

              ON FAILURE — do NOT call handoff. Write a clear description of what is
              missing and what the user must install to fix it, then emit BLOCKED on
              its own line as the very last line of your response.

              ON SUCCESS — include any warnings as plain text, then call
              handoff(route_keyword: "PREFLIGHT PASSED").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Preflight
              - Handoff
            Capabilities:
              FileSystem: [read]
            FunctionChoice: required
            SkipExecutionState: true
            ContextWindow:
              TextOnly: true
              MaxTurnAge: 1
            {AgentFileOptions}
            """;

        var planner = $"""
            Name: Planner
            Description: Analyses the task and writes a comprehensive greenfield brief.
            Instructions: |
              You are a software architect. Your job is to produce a brief that gives the
              Developer everything needed to implement a greenfield project from scratch.

              STEP 1 — CATCH UP
              {ContextReadStep}
              Also read {FuseraftPaths.LocalPreflight} if it exists — it records
              the detected project type, available runtimes, and git repo status.
              If the file is absent, infer these values from the task and sandbox.
              When preflight is present:
                • Write a verify_command that matches the available runtime.
                • Omit git steps from execution_checklist when git_repo is false.

              STEP 2 — READ THE TASK
              Read task.md in the sandbox root. If the file is absent, check for the
              task in session context. If post-compaction context is thin, re-read task.md.

              STEP 3 — CHECK FOR REPLAN SIGNAL
              Call changes_read_latest. Look for failed commands, test failures, or
              "REPLAN REQUIRED" in the session context.
              IF a failure signal is present:
                - Read {FuseraftPaths.LocalTestReport} and recent changes to understand
                  the specific failure.
                - Revise the brief: call write_file_brief(content: ..., format: "json") with
                  the full updated brief — implementation_hints retargeted at the root cause,
                  a new failure_analysis field, and known_pitfalls appended to.
                - Do NOT re-handoff with the same brief the Developer already tried.
              IF no failure signal:
                - If {FuseraftPaths.LocalBrief} already exists: read it now.
                - If it exists AND there is no known_pitfalls entry AND no recent failure
                  in changes_read_latest: call handoff(route_keyword: "HANDOFF TO DEVELOPER")
                  immediately. Do not rewrite a brief that has no known problems.
                - Otherwise: write or update the brief as described in STEP 4.

              STEP 4 — WRITE THE BRIEF
              Call write_file_brief(content: ..., format: "json"). content must be a JSON
              object with exactly these top-level fields:

              goal
                One sentence describing what to build.

              files_to_change
                Array of paths RELATIVE TO THE SANDBOX ROOT for every file the Developer
                must create or modify. Enumerate every source file — do not rely on the
                Developer to discover files.

              implementation_hints
                Array of concrete guidance for each file in files_to_change.
                For NEW files: describe the module's purpose and public API the Developer
                  should implement. Example: "lily/config.py — new file — implement
                  load_config(cfg_path: Path | None) -> dict that creates ~/.lily/config.toml
                  on first run and returns parsed TOML"
                For EXISTING files: name the file, the symbol to change, the approximate
                  line, and why. Example: "src/app.py — run() (~line 42) — add --verbose flag"
                A brief without hints forces the Developer to guess. Be specific.

              verify_command
                The exact shell command the Developer runs to confirm the implementation
                works BEFORE handing off. Rules:
                  • Must exercise actual feature logic — not just compile or import.
                  • Must NOT call pytest, jest, go test, or any other test runner —
                    that is the Tester's job. Use a smoke test instead.
                  • Must succeed using source files alone (after build_command installs
                    dependencies). Do not reference test files or test fixtures.
                  • Write the full literal command. Do not abbreviate with "...".
                Correct: "python -c \"from lily.config import load_config; load_config()\""
                Correct: "python -m lily --help"
                Wrong:   "python -m pytest tests/"
                Wrong:   "dotnet build" (compile only — no feature logic)
                {BackgroundedVerifyCommandRule}

              build_command
                Command to install dependencies before the Tester runs its suite.
                  Python: "pip install -e ." or "pip install -r requirements.txt"
                  Node:   "npm install"
                  Rust:   "" (cargo fetches automatically)
                  .NET:   "dotnet restore"
                Omit if no install step is needed.

              test_targets
                Array of module or feature names the Tester should cover.
                Example: ["config", "session", "skills", "cli"]

              acceptance_criteria
                Array of testable, binary criteria. Each must produce a clear PASS/FAIL
                from an automated test. Rewrite any description criterion as an observable
                outcome with specific inputs and expected outputs.

              execution_checklist
                Ordered list of discrete, verifiable steps for the Developer.
                Every step that creates or modifies a file must name a path that also
                appears in files_to_change. Example:
                  "create lily/config.py with load_config and ensure_defaults functions"
                  "create lily/skills.py with load_skill(path: Path) -> str"

              STEP 5 — GREENFIELD SELF-CRITIQUE
              Run every check below. Fix any failures before calling handoff.

              a. MANIFEST: does files_to_change include the project manifest?
                 Python → pyproject.toml or setup.py or requirements.txt
                 Node   → package.json
                 Rust   → Cargo.toml
                 .NET   → *.csproj or global.json
                 Go     → go.mod
                 Add the manifest if absent — without it the runtime cannot install
                 dependencies and the Tester will fail on import errors.

              b. NO TEST FILES: does files_to_change contain any test files?
                 (test_*.py, *.test.ts, *_test.go, spec_*.rb, *.spec.js, etc.)
                 Remove them. Tests are the Tester's responsibility. If test files
                 appear in files_to_change, the Developer will try to run them before
                 the Tester has written them, causing a guaranteed failure.

              c. VERIFY COMMAND IS NOT A TEST RUNNER: does verify_command call pytest,
                 jest, go test, npm test, dotnet test, or cargo test? Rewrite it as a
                 smoke test if so. A pytest-based verify_command will always fail because
                 the Tester has not written tests yet when the Developer runs it.

              d. VERIFY COMMAND CAN SUCCEED STANDALONE: does verify_command reference any
                 file under .fuseraft/tests/? Remove such references. The verify_command
                 must work with source files alone.

              e. CHECKLIST ↔ files_to_change ALIGNMENT: for each step in
                 execution_checklist that mentions a file path, confirm that path appears
                 in files_to_change. Add any missing paths — a file referenced only in the
                 checklist but absent from files_to_change bypasses the ImplementationComplete
                 contract silently.

              f. VERIFY COMMAND BACKGROUNDING SAFETY: if verify_command backgrounds a
                 long-running process, confirm it follows the rule above — built binary,
                 not a run-wrapper; defensive cleanup prefix; kill within the same command.

              STEP 6 — WRITE CONTEXT
              {ContextWriteStep}

              When done, call handoff(route_keyword: "HANDOFF TO DEVELOPER").

              You are read-only with respect to this project's own files — you have no
              write_file/patch_file access. write_file_brief is the only way to persist this
              brief; implementing the task itself is the Developer's job, not yours.
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Search
              - SessionContext
              - SubAgent
              - Decision
              - Objective
              - Brief
              - Handoff
            Capabilities:
              FileSystem: [read]
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var developer = $"""
            Name: Developer
            Description: Implements every file described in the brief.
            Instructions: |
              You are a senior software engineer building a greenfield project from scratch.
              Your job is to implement every file in the brief and verify the result.

              STEP 1 — CATCH UP
              {ContextReadStep}

              STEP 2 — READ THE BRIEF
              Read {FuseraftPaths.LocalBrief}. Note these fields:
                known_pitfalls      — approaches that failed before. MUST NOT be repeated.
                execution_checklist — ordered steps. Work through them in order.
                build_command       — run this ONCE before implementing, to install deps.
              Also check the Execution State in your context:
                ActiveFailures      — current build/compiler errors to fix.
                SignificantChanges  — files already written this session.
                                      Check before writing — the file may already exist.

              STEP 3 — INSTALL DEPENDENCIES
              If build_command is set in the brief, run it once now with shell_run.
              This installs packages so verify_command can import the package after you write it.
              Do NOT run build_command again after writing files — run verify_command instead.

              STEP 4 — IMPLEMENT EVERY FILE
              FILE WRITE RULES — follow exactly:
                a. For NEW files (not in SignificantChanges): use write_file.
                b. For EXISTING files (already in SignificantChanges or on disk):
                   always use patch_file. Never use write_file on an existing file.
                c. After writing or patching a file, verify it landed: call stat_file
                   and confirm the file is present and non-zero in size.
                   If write_file fails because the file already exists, switch to
                   patch_file immediately — do not retry write_file.
              All paths are RELATIVE TO THE SANDBOX ROOT. Never prefix with the project dir.

              STEP 5 — RUN VERIFY COMMAND
              Run verify_command from the brief with shell_run. Always run it — do not
              skip based on context or recent changes. A shell_run exit code 0 in the
              current context is the only evidence that counts.
              If verify_command fails: read the failing source before retrying — understand
              the new error before writing more code. Do NOT re-run without making a change.

              STEP 6 — CONFIRM CHECKLIST AND COMMIT
              Call changes_read_latest. Confirm every execution_checklist step that
              creates or modifies a file appears in filesWritten.
              If any step is incomplete, continue implementing — do NOT hand off with
              stubs or partial files.
              If git_repo is true in {FuseraftPaths.LocalPreflight}, commit with
              git_add and git_commit. If git_repo is false or the file is absent, skip.

              STEP 7 — WRITE CONTEXT
              {ContextWriteStep}
              Include: which files were written, whether verify_command passed, and any
              open issues. Keep it under 200 words.

              When checklist is complete and verify_command passed:
                call handoff(route_keyword: "HANDOFF TO TESTER").
              If the remaining work cannot fit in the current context window:
                call handoff(route_keyword: "REPLAN REQUIRED").
              If the brief is missing or contradictory:
                call handoff(route_keyword: "REPLAN REQUIRED").
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
            MaxInTurnContextTokens: 60000
            ContextWindow:
              TextOnly: true
              MaxTurnAge: 6
            {AgentFileOptions}
            """;

        var tester = $"""
            Name: Tester
            Description: Writes and runs tests against the implemented source, produces a structured report.
            Instructions: |
              You are a QA engineer. Your job is to verify the implementation against the
              acceptance criteria in the brief and produce a structured test report.

              CONSTRAINTS — read before doing anything else:
              - NEVER write to, modify, or delete any source file. Source is owned by Developer.
              - NEVER create or edit pyproject.toml, setup.py, package.json, or any project manifest.
              - Your write scope is strictly: {FuseraftPaths.LocalTests}/ and {FuseraftPaths.LocalTestFixtures}/.
              - If a test fails because a source file is broken, document it in the test report
                and route BUGS FOUND. Do NOT attempt to fix source files.

              STEP 1 — CATCH UP
              {ContextReadStep}
              Also call changes_read_latest(count: 10) to check for recent Developer fixes.
              If the session context or recent changes show that the Developer fixed a source
              bug since your last test run, you MUST re-run the full test suite — do not
              route based on stale results.

              STEP 2 — READ THE BRIEF
              Read {FuseraftPaths.LocalBrief} to understand acceptance_criteria, test_targets,
              and build_command.

              STEP 3 — INSTALL DEPENDENCIES
              If build_command is set in the brief, run it with shell_run before running tests.

              STEP 4 — WRITE AND RUN TESTS
              Write test scripts to {FuseraftPaths.LocalTests}/ and any fixtures to
              {FuseraftPaths.LocalTestFixtures}/. Run them with shell_run.
              Write one test per acceptance criterion. Use the test framework appropriate
              for the project (pytest for Python, jest for Node, etc.).

              STEP 5 — WRITE TEST REPORT
              Write results to {FuseraftPaths.LocalTestReport}:
                passed  — true if every criterion passes, false otherwise
                results — array of objects:
                  PASS: name, status, exit_code, command (exact shell_run command — required)
                  FAIL: name, status, exit_code, command, output (relevant stderr/stdout)
              A PASS result with an empty or missing command field is treated as fabricated
              and will block handoff. Always write the report before routing.

              STEP 6 — WRITE CONTEXT AND ROUTE
              {ContextWriteStep}
              If all tests pass: call handoff(route_keyword: "HANDOFF TO REVIEWER").
              If any test fails: call handoff(route_keyword: "BUGS FOUND").
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
            MaxInTurnContextTokens: 30000
            Context:
              - Source: session_context
              - Source: changes_recent:5
              - Source: brief_field:test_targets
              - Source: brief_field:build_command
              - Source: own_history:6
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
              4. {ReviewerVerificationIntegrityRule}
              If the code meets all acceptance criteria, call handoff(route_keyword: "APPROVED").
              If changes are needed, call handoff(route_keyword: "REVISION REQUIRED").
                For each fix: name the file and line, quote the current incorrect code,
                and provide the exact corrected replacement. Do not describe in prose —
                provide the code change.
              If the plan is fundamentally wrong, call handoff(route_keyword: "REPLAN REQUIRED").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Changes
              - SessionContext
              - Handoff
            Capabilities:
              FileSystem: [read]
            FunctionChoice: auto
            Context:
              - Source: session_context
              - Source: changes_recent:3
              - Source: file:{FuseraftPaths.LocalTestReport}
                MaxChars: 3000
              - Source: own_history:2
            {AgentFileOptions}
            """;

        var mainConfig = $"""
            Orchestration:
              Name: Greenfield Engineering Team
              Description: >-
                Preflight → Planner → Developer → Tester → Reviewer.
                Optimised for new projects: no PlannerCritic, no Verifier, stricter
                greenfield Planner rules (manifest required, no test files, smoke-test
                verify_command), larger Developer context window, and Tester always
                re-runs after Developer fixes.

              Security:
                FileSystemSandboxPath: .   # set to your project root

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
                    - Type: ChecklistComplete
                      Source: {FuseraftPaths.LocalBrief}
                      Field: execution_checklist
                    # PatternField only — no secondary Pattern match.
                    # The exact verify_command from the brief must have succeeded.
                    - Type: CommandSucceeded
                      PatternField: verify_command

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
                MaxConsecutiveContractFailures: 6
                # Catch stuck agents faster than the swe default of 8.
                MaxConsecutiveTurnsWithoutSignal: 5

              Compaction:
                TriggerTurnCount: 30
                KeepRecentTurns: 8
                Mode: lossless
                PinLastRoutingSignal: true

              WarnTurnTokens: 60000

              ContextBudget:
                WarnAt: 80000
                CutoverAt: 150000
                MaxSingleTurnInputTokens: 200000
                MaxToolResultTokens: 6000
                InTurnToolWindow: 5

              Events:
                Path: {FuseraftPaths.LocalEventsLog}

              Agents:
                - AgentFile: agents/preflight.yaml
                - AgentFile: agents/planner.yaml
                - AgentFile: agents/developer.yaml
                - AgentFile: agents/tester.yaml
                - AgentFile: agents/reviewer.yaml

              Selection:
                Type: statemachine
                StateMachine:
                  Initial: Preflight

                  States:
                    Preflight:
                      Agent: Preflight
                      Transitions:
                        - To: Planning
                          Signal: "PREFLIGHT PASSED"

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
                          HandoffContext:
                            - Source: session_context
                            - Source: changes_recent
                            - Source: brief_field:test_targets
                        - To: Planning
                          Signal: "REPLAN REQUIRED"
                          MaxRevisits: 3
                          HandoffContext:
                            - Source: session_context
                            - Source: changes_recent
                            - Source: file:{FuseraftPaths.LocalTestReport}

                    Testing:
                      Agent: Tester
                      Transitions:
                        - To: Review
                          Signal: "HANDOFF TO REVIEWER"
                          Contract: TestsValid
                          HandoffContext:
                            - Source: session_context
                            - Source: changes_recent
                            - Source: file:{FuseraftPaths.LocalTestReport}
                        - To: Implementation
                          Signal: "BUGS FOUND"
                          HandoffContext:
                            - Source: session_context
                            - Source: changes_recent
                            - Source: file:{FuseraftPaths.LocalTestReport}

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
                    MaxIterations: 50

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
            ("agents/preflight.yaml", preflight),
            ("agents/planner.yaml",   planner),
            ("agents/developer.yaml", developer),
            ("agents/tester.yaml",    tester),
            ("agents/reviewer.yaml",  reviewer),
        ]);
    }
}
