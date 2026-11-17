using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    private static GeneratedConfig Pipeline(string model, string? endpoint) => GeneratedConfig.Inline($$"""
        team: Pipeline
        description: "Planner → Developer → Tester → Reviewer. Developer and Tester have investigation tooling for structured failure tracking. For stricter evidence gates and full safeguards, use the swe template."

        models:
          default:
            modelId: {{model}}{{Ep(endpoint, "    ")}}

        artifacts:
          brief:
            path: "~/.fuseraft/sessions/{project_slug}/{session_id}/brief.json"
          testResults:
            path: .fuseraft/artifacts/test-report.json

        agents:
          Planner:
            role: Analyses the task and writes a structured brief.
            model: default
            tools: [FileSystem, Search, SessionContext, Subagent, Brief]
            instructions: |
              You are a software architect. Your job is to:
              1. {{ContextReadStep}}
              2. Read and understand the task thoroughly.
              3. Use subagent_explore for broad codebase questions without filling your context
                 with raw file contents. For any direct file reads: {{LargeFileProtocol}}
              4. Check if ~/.fuseraft/sessions/{project_slug}/{session_id}/brief.json already exists. If it does, read it — if it
                 still covers the current task, call handoff(route_keyword: "Developer")
                 immediately without rewriting it.
              5. Call write_file_brief(content: ..., format: "json"). content must be a JSON
                 object with exactly these top-level fields:
                   goal — one-sentence description of what to build
                   files_to_change — array of paths RELATIVE TO THE SANDBOX ROOT
                     Correct:  src/module/file.py
                     Wrong:    project_name/src/module/file.py  (never prefix with the project dir)
                   acceptance_criteria — array of testable criteria the code must satisfy
              6. {{ContextWriteStep}}
              When done, call handoff(route_keyword: "Developer").

              You are read-only with respect to this project's own files — you have no
              write_file/patch_file access. write_file_brief is the only way to persist this
              brief; implementing the task itself is the Developer's job, not yours.
            advanced:
              functionChoice: required
              capabilities:
                FileSystem: [read]
          Developer:
            role: Implements the changes described in the brief.
            model: default
            tools: [FileSystem, Shell, Git, Changes, Investigation, SessionContext]
            instructions: |
              You are a senior software engineer. Your job is to:
              1. {{ContextReadStep}}
              2. Read ~/.fuseraft/sessions/{project_slug}/{session_id}/brief.json — implement every file in files_to_change.
                 Use patch_file for targeted edits to existing files; use write_file only for
                 new files. All paths are relative to the sandbox root.
                 The Execution State and Investigation Log in your context show what has already
                 failed this session. Do not repeat an approach listed under "Rejected Paths".
              3. Run a build command with shell_run to confirm it compiles.
                 If it fails, record the failed approach before trying another:
                 a. Call investigation_create_hypothesis(description) naming the specific approach.
                 b. If it fails: call investigation_reject_hypothesis(id, reason, evidence) with
                    the exact error. Read the source of the failure before writing new code.
                 c. If it passes: call investigation_confirm_hypothesis(id, evidence).
                 You MUST NOT call handoff with any open hypotheses.
              4. Commit with git_add and git_commit.
              5. {{ContextWriteStep}}
              When done, call handoff(route_keyword: "Tester").
              If the brief is unclear or needs rethinking, call handoff(route_keyword: "Planner").
            advanced:
              functionChoice: required
              contextWindow:
                textOnly: true
                maxTurnAge: 5
              maxInTurnContextTokens: 60000
              maxInTurnToolPairs: 12
          Tester:
            role: "Writes and runs tests, produces a structured test report."
            model: default
            tools: [FileSystem, Shell, Changes, Investigation, SessionContext]
            instructions: |
              You are a QA engineer. Your job is to:
              1. {{ContextReadStep}}
              2. Read ~/.fuseraft/sessions/{project_slug}/{session_id}/brief.json to understand the acceptance criteria.
              3. Write test scripts (any format) to .fuseraft/tests/ and any
                 fixture or seed files to .fuseraft/tests/fixtures/. Run them with shell_run.
              4. Write results to .fuseraft/artifacts/test-report.json:
                   passed — true if every criterion passes, false otherwise
                   results — array of objects:
                     PASS: name, status, exit_code, command (exact shell_run command — required)
                     FAIL: name, status, exit_code, command, output (relevant stderr/stdout from the failure — required)
              A PASS result with an empty or missing command field is treated as fabricated and will block handoff.
              {{TestReportCommandFieldRule}}
              Always write the report before routing, even when tests fail.
              If a test failure reveals a clear root cause (wrong return value, missing
              dependency, incorrect wiring), call investigation_identify_root_cause(cause) before
              routing.
              5. {{ContextWriteStep}}
              If all tests pass, call handoff(route_keyword: "Reviewer").
              If any tests fail, call handoff(route_keyword: "Developer").
            advanced:
              functionChoice: required
              contextWindow:
                textOnly: true
                maxTurnAge: 6
              maxInTurnToolPairs: 12
          Reviewer:
            role: Reviews implementation and test results; gives final approval or requests changes.
            model: default
            tools: [FileSystem, Shell, Changes, SessionContext]
            instructions: |
              You are a principal engineer. Your job is to:
              1. {{ContextReadStep}}
              2. Read the implementation files listed in ~/.fuseraft/sessions/{project_slug}/{session_id}/brief.json under
                 files_to_change, and .fuseraft/artifacts/test-report.json. For any large file:
                 {{LargeFileProtocolReviewer}}
              3. Run at least one acceptance criterion as a spot-check with shell_run.
              4. {{ReviewerJudgementBlockRule}}
              If all criteria pass, call handoff(route_keyword: "Approved").
              If targeted fixes are needed, call handoff(route_keyword: "Developer").
                For each fix: name the file and line, quote the current incorrect code, and provide the exact corrected replacement.
                Do not describe the problem in prose — provide the code change.
            advanced:
              contextWindow:
                textOnly: true
              capabilities:
                FileSystem: [read]
          Approved:
            role: Terminal confirmation node — emits a one-line completion summary.
            model: default
            instructions: |
              All acceptance criteria have already been verified and approved.
              Write exactly one sentence confirming the task is complete, then hand off to end.

        flow:
          start: Planner
          steps:
            Planner:
              - to: Developer
                requires: [brief]
            Developer:
              - to: Tester
                requires: [anyFileWritten]
              - to: Planner
                when: the plan needs rethinking
            Tester:
              - to: Reviewer
                requires:
                  - testReport: testResults
              - to: Developer
                when: a test fails
            Reviewer:
              - to: Approved
                requires: [reviewJudgement]
              - to: Developer
                when: the code or tests need fixing
            Approved:
              - to: end
                when: your work is complete

        limits:
          maxTurns: 60

        security:
          fileSystemSandboxPath: .

        advanced:
          compaction:
            triggerTurnCount: 30
            keepRecentTurns: 8
            mode: lossless
        """);
}
