using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    /// <summary>
    /// Generates the <c>swe</c> template (replaces <c>devteam</c>):
    /// Planner → PlannerCritic → Developer → Tester → Reviewer
    /// state-machine pipeline with evidence contracts, hypothesis tracking, failure handling,
    /// lossless compaction with adaptive ContextBudget, and a periodic Verifier agent.
    /// Durable execution state and investigation log are injected to all agents by default.
    /// This is the most fully-featured template and serves as the reference implementation.
    /// </summary>
    private static GeneratedConfig Swe(string model, string? endpoint)
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
              4. Check for a REPLAN signal: read changes_read_latest and look for failed
                 commands, test failures, or "REPLAN REQUIRED" in the session context.
                 IF a failure signal is present:
                   - Read the test report and recent changes to understand the specific failure.
                   - Check the Investigation Log in your context: rejected hypotheses show what
                     the Developer already tried. Confirmed root causes show what is definitively
                     known. Do not propose an approach that is already rejected.
                   - If you now know definitively why the previous approach failed, call
                     identify_root_cause(cause) before writing the revised brief.
                   - Update {FuseraftPaths.LocalBrief}: revise implementation_hints to target
                     the root cause, add a failure_analysis field describing what went wrong
                     and why the previous approach failed.
                   - Do NOT re-handoff with the same brief — the Developer already tried it.
                   - Append to (or create) the known_pitfalls array in the brief: each entry
                     names an approach already tried and why it failed. The Developer reads
                     this before starting and MUST NOT repeat any listed approach.
                 IF no failure signal and {FuseraftPaths.LocalBrief} already exists and still
                 covers the current task: call handoff(route_keyword: "HANDOFF TO CRITIC")
                 immediately without rewriting it.
              4b. Check for Critic feedback: call read_file on {FuseraftPaths.LocalBriefReview}.
                  IF it exists, the JSON contains:
                    "blocking_issues"       — MUST ALL be fixed before re-handoff.
                    "optional_improvements" — address if straightforward; safe to skip.
                  Address every blocking issue explicitly in the revised brief.
                  Do NOT re-handoff with blocking issues unresolved — the same brief will
                  be rejected again. For each fix, note what you changed in implementation_hints.
              5. If you have a theory about the root cause or the best implementation approach
                 (especially on a replanning cycle), record it with create_hypothesis(hypothesis)
                 so the Developer can confirm or reject it explicitly rather than abandoning it
                 silently. Check the Investigation Log for any approach already ruled out.
                 Write a brief to {FuseraftPaths.LocalBrief} with fields:
                   goal — one-sentence description of what to build
                   files_to_change — array of paths RELATIVE TO THE SANDBOX ROOT
                     Correct:  src/module/file.py
                     Wrong:    project_name/src/module/file.py  (never prefix with the project dir)
                   implementation_hints — array of concrete anchors discovered during exploration.
                     Each entry: file path, symbol/method name, approximate line, and why it matters.
                     Example: "src/VM/KiwiVM.cs — GetMember (~line 1876) — enum dispatch point"
                     A brief without anchors forces the Developer to re-explore the whole codebase
                     on every compaction boundary, wasting hundreds of thousands of tokens.
                     Be specific: file + symbol + reason is worth far more than file alone.
                   verify_command — the exact shell command to run to verify runtime correctness.
                     This must execute the actual code, not just compile it. Examples:
                       "dotnet run --project src/app.csproj -- tests/test.kiwi"
                       "python -m pytest tests/test_feature.py"
                       "cargo test -- feature_tests"
                     The Developer runs this before committing; the ImplementationComplete
                     contract requires it to succeed. Wrong: "dotnet build" (compile only).
                     IMPORTANT: write the full literal command — never abbreviate with "...".
                     Abbreviated commands cannot be matched against the session log and will
                     cause ImplementationComplete to loop indefinitely.
                   acceptance_criteria — array of testable criteria the code must satisfy
              5b. SELF-CRITIQUE — run these checks against the brief you just wrote (or
                  the existing brief if you skipped step 5). Fix before continuing.
                  a. files_to_change completeness: use sub_agent_explore to confirm no
                     clearly in-scope file is missing (call sites, tests, config). Add any
                     missing files.
                  b. acceptance_criteria testability: every criterion must produce a binary
                     PASS/FAIL from an automated test. Rewrite any description criterion.
                  c. verify_command concreteness: must run actual feature logic, not just
                     compile. Flags that assume pre-built state (--no-build, --no-restore)
                     are only valid when the build step precedes them in the same command
                     chain (&&). Rewrite any command that uses such flags standalone.
                  d. implementation_hints specificity: every hint must name file + symbol/
                     method + why it matters. Remove or expand file-only hints.
                  e. execution_checklist: write an execution_checklist array of discrete,
                     ordered, verifiable steps ("create fwc/Counter.cs", "add glob exclusion
                     to main.csproj"). The Developer works through this list in order.
              6. {ContextWriteStep}
              When done, call handoff(route_keyword: "HANDOFF TO CRITIC").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Search
              - SessionContext
              - SubAgent
              - Decision
              - Objective
              - Investigation
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var plannerCritic = $"""
            Name: PlannerCritic
            Description: Adversarially reviews the brief for completeness before the Developer starts.
            Instructions: |
              You are an adversarial brief reviewer. Find reasons the brief will FAIL — not reasons
              it will succeed. A brief that passes your review goes directly to the Developer; one
              that fails returns to the Planner with your specific objections.

              FOLLOW THESE STEPS IN ORDER:

              1. READ THE BRIEF: Call read_file on {FuseraftPaths.LocalBrief}.

              2. AUDIT files_to_change COMPLETENESS:
                 Use sub_agent_explore to ask which files are affected by the goal in the brief.
                 Compare the response against files_to_change. Flag any clearly in-scope file that
                 is absent — call sites, test files, related modules, config. Do NOT flag
                 out-of-scope files.

              3. AUDIT acceptance_criteria TESTABILITY:
                 For each criterion ask: can an automated test produce a binary PASS/FAIL for this?
                 Flag criteria that are descriptions ("the feature works", "code is clean") rather
                 than observable outcomes ("running X returns exit code 0 and output contains Y").

              4. AUDIT verify_command CONCRETENESS:
                 The command must exercise a real code path of the feature — not just compile or
                 import it. Flag commands that only call --help, --version, or build/compile without
                 running the actual feature logic.

              5. AUDIT implementation_hints SPECIFICITY:
                 Each hint must name a file AND a symbol/method AND explain why it matters. Flag
                 hints that name only a file with no symbol ("src/foo.py — relevant").

              6a. IF ANY BLOCKING ISSUES: Call write_file to save {FuseraftPaths.LocalBriefReview}
                  as a JSON object with two fields:
                    "blocking_issues"      — array of strings, each a mandatory fix the Planner
                                             MUST address before the brief can be approved
                                             (missing files, untestable criteria, hollow commands)
                    "optional_improvements" — array of strings, each a suggestion the Planner
                                              MAY incorporate but that will not block approval
                  Then call handoff(route_keyword: "BRIEF REJECTED").
                  Only use blocking_issues for real gaps that will cause the Developer to fail —
                  do not inflate this list with stylistic preferences.

              6b. IF NO BLOCKING ISSUES: Call handoff(route_keyword: "BRIEF APPROVED").
                  Optional improvements may still be written to {FuseraftPaths.LocalBriefReview}
                  as a record, but do not block on them.
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
            Description: Implements the changes described in the brief.
            Instructions: |
              You are a senior software engineer. Your job is to:
              1. {ContextReadStep}
              2. Read {FuseraftPaths.LocalBrief}. Check for these fields:
                   known_pitfalls — approaches already tried and known to fail. You MUST NOT
                                    repeat any listed approach, even partially.
                   execution_checklist — ordered steps. Work through them in order.
                 Then read the Execution State section in your context — it contains:
                   ActiveFailures   — build/compiler errors with file, line, and error code.
                                      These are the specific errors you must fix.
                   FailedAttempts   — approaches that were tried and failed this session.
                                      Do not repeat any approach listed here.
                   SignificantChanges — files already written or patched this session.
                                        Check this before writing: the file may already exist.
                 Also read the Investigation Log section: rejected hypotheses are ruled-out paths,
                 confirmed root causes are ground truth. Do not re-attempt anything that appears
                 under rejected hypotheses.
                 If the handoff context includes a test report or failure summary, read it before
                 writing any code. Root-cause first, patch second — read the source of the failing
                 call before patching; a patch without understanding the failure will fail again.
                 BUILD ERROR TRIAGE — follow before touching any source file:
                   a. Every compiler/linker error identifies a build unit — read that attribution
                      first (e.g. [project.csproj] suffix, CMake target, Cargo package, Make rule).
                      That tells you WHICH config file to fix, not just which source file.
                   b. If a source file's errors are attributed to build unit A but logically belong
                      to unit B, fix A's include/exclude rules — not B's source.
                   c. A "duplicate symbol" error almost always means one file is compiled by two
                      build units. Fix the glob/include patterns — do not touch the source.
                   d. Before each shell command, state in one sentence why it will produce a
                      different result than the previous run. A repeated command without a reason
                      is not a hypothesis — it is a loop.
              3. Implement every file in files_to_change.
                 FILE WRITE RULES — follow exactly:
                   a. For existing files: always use patch_file. Never use write_file on a file
                      that already exists — it may be non-empty and write_file will fail silently.
                   b. For new files: use write_file.
                   c. After writing or patching a file, verify it landed: call stat_file on the
                      path (or list_directory on its parent) and confirm the file is present and
                      non-zero in size. If write_file fails (file already exists), switch to
                      patch_file immediately — do not retry write_file on the same path.
                 All paths are relative to the sandbox root — never double-nest the project dir.
              4. HYPOTHESIS PROTOCOL — required for every verify_command attempt:
                 a. BEFORE running verify_command, call create_hypothesis(hypothesis) naming
                    the specific approach you are about to try (e.g. "patch AddCommand.cs to
                    add missing namespace import"). Record the hypothesis ID.
                 b. Run verify_command from the brief with shell_run. Check changes_read_latest
                    first — if verify_command already succeeded (exit code 0) this session,
                    skip re-running it and proceed to commit.
                 c. If verify_command FAILS: call reject_hypothesis(id, reason, evidence) with
                    the exact exit code and relevant error lines from the output. Do NOT attempt
                    a new fix without first closing the current hypothesis. Read the failing
                    source before retrying — understand the new error before writing new code.
                 d. If verify_command PASSES: call confirm_hypothesis(id, evidence) to close it.
                 You MUST NOT call handoff(route_keyword: "HANDOFF TO TESTER") with any open
                 (unclosed) hypotheses — close every hypothesis before claiming implementation
                 complete. For other routing signals (e.g., "REPLAN REQUIRED"), open hypotheses
                 are allowed when the build is still failing.
              5. Commit with git_add and git_commit.
              6. {ContextWriteStep}
              When done, call handoff(route_keyword: "HANDOFF TO TESTER").
              If the brief is missing or contradictory: handoff(route_keyword: "REPLAN REQUIRED").
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
              If a test failure reveals a clear root cause (wrong return value, missing
              dependency, incorrect wiring), call identify_root_cause(cause) before routing
              so the Developer does not need to re-diagnose it.
              5. {ContextWriteStep}
              If all pass, call handoff(route_keyword: "HANDOFF TO REVIEWER").
              If any fail, call handoff(route_keyword: "BUGS FOUND").
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
            MaxInTurnContextTokens: 30000
            Context:
              - Source: session_context
              - Source: changes_recent:5
              - Source: brief_field:test_targets
              - Source: brief_field:build_command
              - Source: own_history:4
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
            Context:
              - Source: session_context
              - Source: changes_recent:3
              - Source: file:.fuseraft/artifacts/test-report.json
                MaxChars: 3000
              - Source: own_history:2
            {AgentFileOptions}
            """;

        var verifier = $"""
            Name: Verifier
            Description: Audits the evidence graph, execution state, and investigation log for inconsistencies.
            Instructions: |
              You are an evidence auditor. Detect inconsistencies between what agents
              claim and what is recorded in the change log, execution state, and investigation log.

              FOLLOW THESE STEPS IN ORDER:

              1. Call changes_read_latest to see what file writes, shell commands, and exit codes
                 were recorded this session.

              2. Read {FuseraftPaths.LocalExecutionState} with read_file. Check:
                 - ActiveFailures: any build/compiler errors currently present.
                 - FailedAttempts: approaches that were recorded as failed.
                 - SignificantChanges: files written or patched this session.

              3. Read {FuseraftPaths.LocalInvestigationLog} with read_file. Check:
                 - ConfirmedRootCauses: known ground-truth causes.
                 - Hypotheses with status "open": any unclosed hypothesis is a protocol violation
                   (the Developer must close every hypothesis before handoff).
                 - Hypotheses with status "rejected": approaches that must not be retried.

              4. Cross-check for these specific inconsistency patterns:
                 a. REPEATED FAILURE: The same error code or error message appears in
                    ActiveFailures AND in a previous FailedAttempt — meaning a fix was
                    attempted but the same error recurred. The Developer has not made progress.
                 b. UNDOCUMENTED FAILURES: FailedAttempts is empty but the change log shows
                    multiple failed shell commands — the Developer is failing silently without
                    using the investigation tools.
                 c. KNOWN ROOT CAUSE UNADDRESSED: ConfirmedRootCauses is non-empty but
                    ActiveFailures still contains the same error category — the root cause
                    was identified but the fix was not applied or did not work.
                 d. OPEN HYPOTHESES: Any hypothesis with status "open" when ActiveFailures
                    is empty — the Developer claimed success but left a hypothesis unclosed.
                    Do NOT flag open hypotheses when ActiveFailures is non-empty; the
                    Developer is still actively debugging and open hypotheses are expected.
                 e. CLAIMED SUCCESS WITHOUT EVIDENCE: An agent claimed "verify_command passed"
                    or "ImplementationComplete" but the change log does not show a successful
                    shell_run of that command.

              5. If the change log shows verify_command was not yet run, use shell_run to
                 execute the verify_command from {FuseraftPaths.LocalBrief} and record the result.

              6. Report outcome:
                 - If consistent: "Evidence verified — no inconsistencies found."
                 - If inconsistent: "INCONSISTENCY DETECTED: <pattern letter> — <what was claimed
                   vs what the evidence shows, with specific error codes or file names>"
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Changes
              - Shell
            FunctionChoice: required
            SkipExecutionState: true
            {VerifierContextWindow}
            {AgentFileOptions}
            """;

        var mainConfig = $"""
            Orchestration:
              Name: Software Engineering Team
              Description: >-
                Planner → PlannerCritic → Developer → Tester → Reviewer with state machine routing,
                evidence contracts, hypothesis tracking, adaptive ContextBudget, and self-verification.

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
                      PatternField: verify_command
                      Pattern: "build|compile|test|check"

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
                EveryNTurns: 4
                TriggerOnSuspiciousTransition: true
                FindingsKeyword: INCONSISTENCY

              Compaction:
                TriggerTurnCount: 30
                KeepRecentTurns: 12
                Mode: lossless
                PinLastRoutingSignal: true

              # WarnTurnTokens: warn when a single turn's input exceeds this value.
              # Keep this below ContextBudget.CutoverAt so the warning fires before
              # compaction is forced, giving an advance signal rather than a post-hoc note.
              WarnTurnTokens: 60000

              # ContextBudget: per-agent cumulative input-token thresholds. Warns before
              # context rot sets in, then triggers compaction automatically. Counters reset
              # after each compaction cycle so the session can run indefinitely.
              # MaxSingleTurnInputTokens guards against single-turn explosions that exhaust
              # the cumulative budget in one shot — compaction fires before the next turn.
              # MaxToolResultTokens caps individual tool result size before it enters the
              # context slice — prevents a single large build log from filling the budget.
              ContextBudget:
                WarnAt: 60000
                CutoverAt: 100000
                MaxSingleTurnInputTokens: 200000
                MaxToolResultTokens: 8000

              Events:
                Path: {FuseraftPaths.LocalEventsLog}

              # Each agent lives in its own YAML file in agents/ — edit, version, or reuse
              # them independently across configs. Inline fields override the file at load time.
              Agents:
                - AgentFile: agents/planner.yaml
                - AgentFile: agents/planner-critic.yaml
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
                        - To: BriefReview
                          Signal: "HANDOFF TO CRITIC"

                    BriefReview:
                      Agent: PlannerCritic
                      Transitions:
                        - To: Implementation
                          Signal: "BRIEF APPROVED"
                          Contract: BriefExists
                        - To: Planning
                          Signal: "BRIEF REJECTED"
                          MaxRevisits: 3
                          ReviewArtifactPath: {FuseraftPaths.LocalBriefReview}
                          HandoffContext:
                            - Source: file:{FuseraftPaths.LocalBriefReview}

                    Implementation:
                      Agent: Developer
                      Transitions:
                        - To: Testing
                          Signal: "HANDOFF TO TESTER"
                          Contract: ImplementationComplete
                          RecoveryAgent: PlannerCritic
                          HandoffContext:
                            - Source: session_context
                            - Source: changes_recent
                            - Source: brief_field:test_targets
                        - To: Planning
                          Signal: "REPLAN REQUIRED"
                          MaxRevisits: 2
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
                            - Source: file:.fuseraft/artifacts/test-report.json
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
            ("agents/planner.yaml",         planner),
            ("agents/planner-critic.yaml",  plannerCritic),
            ("agents/developer.yaml",       developer),
            ("agents/tester.yaml",          tester),
            ("agents/reviewer.yaml",        reviewer),
            ("agents/verifier.yaml",        verifier),
        ]);
    }
}
