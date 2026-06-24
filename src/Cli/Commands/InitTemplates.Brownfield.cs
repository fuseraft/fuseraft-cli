using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    /// <summary>
    /// Generates the <c>brownfield</c> template: Archaeologist → Planner → Developer → Reviewer
    /// expressed as a directed graph. The Archaeologist writes a convention profile and discovery
    /// brief (one-time recon); all subsequent agents read these artifacts rather than re-exploring
    /// the codebase. Graph selection gives the Reviewer two distinct back-edge targets:
    /// <c>REVISION REQUIRED</c> → Developer (targeted fix) and <c>REPLAN REQUIRED</c> → Planner
    /// (approach rethink). Supersedes both the old state-machine <c>brownfield</c> and the
    /// <c>brownfield-graph</c> templates.
    /// </summary>
    private static GeneratedConfig Brownfield(string model, string? endpoint)
    {
        var archaeologist = $"""
            Name: Archaeologist
            Description: Recons the codebase and writes the discovery brief and convention profile.
            Instructions: |
              You are a codebase archaeologist. Your job is to understand an existing project
              before any changes are made. Follow this procedure:

              1. Check if both {FuseraftPaths.LocalBrownfieldBrief} and {FuseraftPaths.LocalConventions}
                 already exist. If they do, call handoff(route_keyword: "RECON COMPLETE") immediately
                 without re-running recon.
              2. For any file you need to examine: {LargeFileProtocolArchaeologist}
              3. Use list_files and sub_agent_explore to map the directory structure — do NOT
                 read every file; prefer sub_agent_explore for structural questions.
              4. Identify: primary language and framework, naming conventions (snake_case vs camelCase),
                 import style, test framework, build system, and key architectural patterns.
              5. Call write_file_conventions(content: ..., format: "json"). content must be a JSON
                 object with exactly these top-level fields: language (string), naming_patterns
                 (array), error_handling (array of idioms to follow), forbidden_patterns (array),
                 test_patterns (array), structural_notes (array — fold framework/import-style
                 observations in here), build_command (string), test_command (string).
              6. Identify the files most likely to need modification for the given task.
              7. Call write_file_discovery_brief(content: ..., format: "json"). content must be a
                 JSON object with exactly these top-level fields: summary (one-paragraph string
                 describing the codebase structure), in_scope_files (array of paths likely relevant
                 to the task), fragility_signals (array of objects, each a "file" string and a
                 "reason" string — e.g. file "internal/legacy/queue.go", reason "no tests, high
                 churn"), test_coverage_gaps (array of files lacking a corresponding test file).
              8. For each significant architectural risk or pattern you uncover, call
                 record_investigation(summary, conclusion) — these findings survive compaction
                 and will be visible to every subsequent agent without re-reading the codebase.

              You are read-only with respect to this project's own files — you have no
              write_file/patch_file access. write_file_conventions and write_file_discovery_brief
              are the only ways to persist your findings; implementing the task itself is the
              Developer's job, not yours.

              When both write_file_conventions and write_file_discovery_brief have been called,
              call handoff(route_keyword: "RECON COMPLETE").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Search
              - SubAgent
              - Investigation
              - Conventions
              - DiscoveryBrief
              - Handoff
            Capabilities:
              FileSystem: [read]
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var planner = $"""
            Name: Planner
            Description: Designs the targeted change based on the discovery brief.
            Instructions: |
              You are a software architect working on an existing codebase. Your job is to:
              1. {ContextReadStep}
              2. Check for a REPLAN signal: read changes_read_latest and look for failed
                 commands or "REPLAN REQUIRED" in the session context.
                 IF a failure signal is present:
                   - Read any available test output or reviewer notes in the handoff context.
                   - Check the Investigation Log in your context: rejected hypotheses show what
                     the Developer already tried. Do not propose an approach that is already
                     rejected. If you now know definitively why it failed, call
                     identify_root_cause(cause) before writing the revised brief.
                   - Revise the brief: call write_file_brief(content: ..., format: "json") with
                     the full updated brief — implementation_hints retargeted at the root cause,
                     plus a new failure_analysis field describing what went wrong.
                   - Do NOT re-handoff with the same brief — the Developer already tried it.
                 IF no failure signal and {FuseraftPaths.LocalBrief} already exists and still
                 covers the current task: call handoff(route_keyword: "HANDOFF TO DEVELOPER")
                 immediately without rewriting it.
              3. Read {FuseraftPaths.LocalBrownfieldBrief} to understand the codebase shape and risks.
              4. Read {FuseraftPaths.LocalConventions} — follow the project's conventions exactly.
              5. Use sub_agent_explore for additional targeted questions. For direct file reads:
                 {LargeFileProtocol}
              6. Call write_file_brief(content: ..., format: "json"). content must be a JSON
                 object with exactly these top-level fields:
                   goal — one-sentence description of the change
                   findings — summary of relevant existing code to modify
                   files_to_change — only the files that genuinely need to change
                                     (paths relative to the sandbox root)
                   implementation_hints — concrete symbol-level anchors from your exploration.
                     Each entry: file + symbol/method + approximate line + reason.
                     Without these, the Developer re-explores everything from scratch on every
                     compaction boundary. A symbol name and line hint is worth hundreds of tokens.
                   verify_command — the exact shell command to verify runtime correctness.
                     Must exercise the actual code path, not just compile. Full literal command.
                   acceptance_criteria — observable code properties the change must satisfy
                   convention_notes — specific conventions to follow from the profile
              7. {ContextWriteStep}
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
              - Brief
              - Handoff
            Capabilities:
              FileSystem: [read]
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var developer = $"""
            Name: Developer
            Description: Implements the change staying strictly within the scoped file list.
            Instructions: |
              You are a developer working carefully inside an existing codebase. Your job is to:
              1. {ContextReadStep}
              2. Read {FuseraftPaths.LocalBrief}. If the handoff context includes reviewer notes
                 or a failure summary, read it before writing any code — root-cause first,
                 patch second. Read the source of any failing call before patching it.
                 The Execution State and Investigation Log in your context show what has already
                 failed this session. Do not repeat an approach listed under "Rejected Paths".
              3. Read {FuseraftPaths.LocalConventions} — follow the project's naming, import,
                 and style conventions exactly.
              4. Before modifying an existing file: {LargeFileProtocolDeveloper}
                 Never overwrite blindly.
              5. Use patch_file for surgical edits to existing files; use write_file only for
                 new files. All paths relative to the sandbox root.
              6. Run the build command from the convention profile to confirm compilation.
              7. Run verify_command from the brief to confirm runtime correctness.
                 HYPOTHESIS PROTOCOL — required for every verify_command attempt:
                 a. Call create_hypothesis(description) naming the specific approach.
                 b. If it fails: call reject_hypothesis(id, reason, evidence) with the exact
                    error. Read the failing source before retrying.
                 c. If it passes: call confirm_hypothesis(id, evidence).
                 You MUST NOT call handoff with any open hypotheses.
              8. Commit with git_add and git_commit.
              9. {ContextWriteStep}
              When done, call handoff(route_keyword: "HANDOFF TO REVIEWER").
              If the brief is fundamentally unclear, call handoff(route_keyword: "REPLAN REQUIRED").
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

        var reviewer = $"""
            Name: Reviewer
            Description: Verifies the change via code inspection and runtime execution; routes to Developer, Planner, or final approval.
            Instructions: |
              You are a principal engineer reviewing a change to an existing codebase. Your job is to:
              1. {ContextReadStep}
              2. For each file listed in {FuseraftPaths.LocalBrief} under files_to_change:
                 {LargeFileProtocolReviewer}
              3. Inspect the code against every acceptance criterion.
              4. Check that the change follows conventions from {FuseraftPaths.LocalConventions}.
              5. Confirm no files outside files_to_change were modified (use changes_read_latest).
              6. Run the build command from the convention profile to confirm the project compiles.
              7. Run the verify_command from the brief to confirm runtime correctness.
              8. {ReviewerVerificationIntegrityRule}
              Emit a JSON review block covering every acceptance criterion with verdict (PASS/FAIL)
              and evidence before your routing keyword.
              If all criteria pass, call handoff(route_keyword: "APPROVED").
              If targeted fixes are needed, call handoff(route_keyword: "REVISION REQUIRED") and
              describe each fix: file, line, current code, exact replacement.
              If the approach is wrong, call handoff(route_keyword: "REPLAN REQUIRED").
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
              Name: Brownfield Pipeline
              Description: >-
                Archaeologist → Planner → Developer → Reviewer as a directed graph. One-time recon
                writes a convention profile and discovery brief; all subsequent agents read these
                rather than re-exploring the codebase. Reviewer has two back-edge targets:
                REVISION REQUIRED → Developer, REPLAN REQUIRED → Planner.

              Security:
                FileSystemSandboxPath: .   # set to your project root (e.g. ~/projects/myapp)

              Brownfield:
                EntryPoints:
                  - src/   # replace with your actual entry points (e.g. cmd/server/main.go)
                SeedEnvelopeFromBrief: true
                DiscoveryBriefPath: {FuseraftPaths.LocalBrownfieldBrief}
                ConventionProfilePath: {FuseraftPaths.LocalConventions}

              ChangeTracking:
                Path: {FuseraftPaths.LocalChanges}

              Validation:
                BriefPath: {FuseraftPaths.LocalBrief}
                ChangeLogPath: {FuseraftPaths.LocalChanges}

              Events:
                Path: {FuseraftPaths.LocalEventsLog}

              WarnTurnTokens: 60000

              # Each agent lives in its own YAML file in agents/ — edit, version, or reuse
              # them independently across configs.
              Agents:
                - AgentFile: agents/archaeologist.yaml
                - AgentFile: agents/planner.yaml
                - AgentFile: agents/developer.yaml
                - AgentFile: agents/reviewer.yaml
                - AgentFile: agents/approved.yaml

              Selection:
                Type: graph
                Graph:
                  EntryNode: recon
                  MaxRetries: 4

                  Nodes:
                    - Id: recon
                      Agent: Archaeologist
                    - Id: planner
                      Agent: Planner
                    - Id: developer
                      Agent: Developer
                    - Id: reviewer
                      Agent: Reviewer
                    - Id: approved
                      Agent: Approved
                      Terminal: true

                  Edges:
                    # Forward edges
                    - From: recon
                      To: planner
                      Keyword: "RECON COMPLETE"
                      Validators: [RequireWriteFile]       # blocks until discovery files are written

                    - From: planner
                      To: developer
                      Keyword: "HANDOFF TO DEVELOPER"
                      Validators: [RequireBrief]           # blocks until brief.json is valid

                    - From: developer
                      To: reviewer
                      Keyword: "HANDOFF TO REVIEWER"
                      Validators: [RequireWriteFile]       # blocks until at least one file is written

                    - From: reviewer
                      To: approved
                      Keyword: "APPROVED"
                      Validators: [RequireReviewJudgement]

                    # Back-edges
                    - From: reviewer
                      To: developer
                      Keyword: "REVISION REQUIRED"         # targeted fix — bypass recon and planning

                    - From: reviewer
                      To: planner
                      Keyword: "REPLAN REQUIRED"           # approach rethink — skip recon

                    - From: developer
                      To: planner
                      Keyword: "REPLAN REQUIRED"           # developer can also escalate

              Termination:
                Type: composite
                Strategies:
                  - Type: regex
                    Pattern: "\\bAPPROVED\\b"
                    AgentNames: [Reviewer]
                  - Type: maxiterations
                    MaxIterations: 60

              Compaction:
                TriggerTurnCount: 30
                KeepRecentTurns: 8
                Mode: intent

              ContextBudget:
                WarnAt: 60000
                CutoverAt: 100000
                MaxSingleTurnInputTokens: 200000

              # ---------------------------------------------------------------------------
              # OPTIONAL EXTRAS — uncomment as needed
              # ---------------------------------------------------------------------------

              # EvidenceStore:
              #   Path: {FuseraftPaths.LocalEvidence}

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
            ("agents/archaeologist.yaml", archaeologist),
            ("agents/planner.yaml",       planner),
            ("agents/developer.yaml",     developer),
            ("agents/reviewer.yaml",      reviewer),
            ("agents/approved.yaml",      approved),
        ]);
    }
}
