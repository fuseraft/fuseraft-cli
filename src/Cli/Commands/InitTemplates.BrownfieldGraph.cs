using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    /// <summary>
    /// Generates the <c>brownfield-graph</c> template: Archaeologist → Planner → Developer → Reviewer
    /// expressed as a directed graph rather than a state machine.
    /// The key advantage over the state-machine brownfield template is that the Reviewer has two
    /// distinct back-edge targets: <c>REVISION REQUIRED</c> returns to Developer (targeted fix) while
    /// <c>REPLAN REQUIRED</c> returns to Planner (approach rethink). Expressing this in a state machine
    /// requires an extra state and duplicated transitions; the graph expresses it as two labelled edges.
    /// </summary>
    private static GeneratedConfig BrownfieldGraph(string model, string? endpoint)
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
              2. For any file you need to examine: call get_file_summary first (shows the first
                 30 lines and total line count), grep_file to locate key structures (classes,
                 entry points, imports), then read_file with startLine/maxLines for those
                 sections only — files can exceed 10,000 lines; never cold-read a large file
                 in full.
              3. Use list_files and sub_agent_explore to map the directory structure — do NOT
                 read every file; prefer sub_agent_explore for structural questions — it returns
                 a prose summary, not raw file contents.
              4. Identify: primary language and framework, naming conventions (snake_case vs camelCase),
                 import style, test framework, build system, and key architectural patterns.
              5. Write the convention profile to {FuseraftPaths.LocalConventions} with fields:
                   language, framework, naming_convention, import_style, test_framework,
                   build_command, lint_command, notes (array of key architectural observations).
              6. Identify the files most likely to need modification for the given task.
              7. Write the discovery brief to {FuseraftPaths.LocalBrownfieldBrief} with fields:
                   summary — one paragraph describing the codebase structure
                   in_scope_files — array of file paths likely relevant to the task
                   dependencies — key external dependencies to be aware of
                   risks — array of fragility signals (e.g. no tests, circular deps, god objects)

              When both files are written, call handoff(route_keyword: "RECON COMPLETE").
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

        var planner = $"""
            Name: Planner
            Description: Designs the targeted change based on the discovery brief.
            Instructions: |
              You are a software architect working on an existing codebase. Your job is to:
              1. Check if {FuseraftPaths.LocalBrief} already exists. If it does, read it — if it
                 still covers the current task, call handoff(route_keyword: "HANDOFF TO DEVELOPER")
                 immediately without rewriting it.
              2. Read {FuseraftPaths.LocalBrownfieldBrief} to understand the codebase shape and risks.
              3. Read {FuseraftPaths.LocalConventions} to understand the project's conventions — follow them exactly.
              4. Use sub_agent_explore for any additional targeted questions. For direct file
                 reads: call get_file_summary first, grep_file to locate the section, then
                 read_file with startLine/maxLines — never cold-read a large file in full.
              5. Write a scoped brief to {FuseraftPaths.LocalBrief} with fields:
                   goal — one-sentence description of the change
                   findings — summary of relevant existing code to modify
                   files_to_change — only the files that genuinely need to change
                   acceptance_criteria — observable code properties the change must satisfy
                   convention_notes — specific conventions to follow from the profile
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
            Description: Implements the change staying strictly within the scoped file list.
            Instructions: |
              You are a developer working carefully inside an existing codebase. Your job is to:
              1. Read {FuseraftPaths.LocalBrief} — implement ONLY the files listed in files_to_change.
              2. Read {FuseraftPaths.LocalConventions} — follow the project's naming, import, and style conventions exactly.
              3. Before modifying an existing file: call get_file_summary to check its size, grep_file
                 to locate the exact section to edit, then read_file with startLine/maxLines for
                 that section only — never cold-read a large file in full. Never overwrite blindly.
              4. Use patch_file for surgical edits to existing files; use write_file only for new files.
              5. Run the build command from the convention profile to confirm nothing is broken.
              6. Commit with git_add and git_commit.
              When done, call handoff(route_keyword: "HANDOFF TO REVIEWER").
              If the brief is fundamentally unclear or the approach is wrong, call handoff(route_keyword: "REPLAN REQUIRED").
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

        var reviewer = $"""
            Name: Reviewer
            Description: Verifies the change via code inspection and runtime execution; routes to Developer, Planner, or final approval.
            Instructions: |
              You are a principal engineer reviewing a change to an existing codebase. Your job is to:
              1. Read each file listed in {FuseraftPaths.LocalBrief} under files_to_change.
              2. Inspect the code against every acceptance criterion.
              3. Check that the change follows conventions from {FuseraftPaths.LocalConventions}.
              4. Confirm no files outside files_to_change were modified (use changes_read_latest).
              5. Run the build command from the convention profile (e.g. shell_run("dotnet build"),
                 shell_run("cargo build"), shell_run("make"), etc.) to confirm the project compiles.
              6. Run the test command (e.g. shell_run("dotnet test"), shell_run("cargo test"),
                 shell_run("pytest"), etc.) to confirm the test suite passes.
              Emit a JSON review block covering every acceptance criterion with verdict (PASS/FAIL)
              and evidence — including what you ran and what you observed — before your routing keyword.
              If all criteria pass and the tests pass, call handoff(route_keyword: "APPROVED").
              If targeted fixes are needed, call handoff(route_keyword: "REVISION REQUIRED") and describe what to fix.
              If the approach itself is wrong and the brief needs rethinking, call handoff(route_keyword: "REPLAN REQUIRED").
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
              Name: Brownfield Graph Pipeline
              Description: >-
                Archaeologist → Planner → Developer → Reviewer expressed as a directed graph.
                The Reviewer has two distinct back-edge targets: "REVISION REQUIRED" returns to
                Developer for targeted fixes; "REPLAN REQUIRED" returns to Planner when the
                approach needs rethinking. Multi-target back-edges from a single node are the
                key advantage of graph routing over state machine for complex review cycles.

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

              # Each agent lives in its own YAML file in agents/ — edit, version, or reuse
              # them independently across configs. Inline fields override the file at load time.
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
                      Agent: Reviewer             # routes on keyword — NOT terminal
                    - Id: approved                # terminal node — session ends after this run
                      Agent: Approved
                      Terminal: true

                  # Key pattern: Reviewer routes to TWO different back-edge targets
                  # "REVISION REQUIRED" → developer  (fix is targeted; recon/planning stay valid)
                  # "REPLAN REQUIRED"   → planner    (approach is wrong; needs a new brief)
                  # This cannot be expressed in a state machine without duplicating states or
                  # adding a routing guard — in graph it is simply two labelled edges.
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
                      Validators: [RequireReviewJudgement] # blocks until a review JSON block exists

                    # Back-edges
                    - From: reviewer
                      To: developer
                      Keyword: "REVISION REQUIRED"         # targeted fix → restart from developer

                    - From: reviewer
                      To: planner
                      Keyword: "REPLAN REQUIRED"           # rethink approach → restart from planner

                    - From: developer
                      To: planner
                      Keyword: "REPLAN REQUIRED"           # developer can also escalate to planner

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

              # EvidenceStore:
              #   Path: {FuseraftPaths.LocalEvidence}

              # Contracts:
              #   - Name: ReconComplete
              #     Requires:
              #       - Type: FileExists
              #         Path: {FuseraftPaths.LocalBrownfieldBrief}
              #       - Type: FileExists
              #         Path: {FuseraftPaths.LocalConventions}
              #   - Name: BriefExists
              #     Requires:
              #       - Type: FileExists
              #         Path: {FuseraftPaths.LocalBrief}

              # FailureHandling:
              #   MissingEvidence:
              #     Action: Reinstruct
              #     Threshold: 3
              #   NoProgress:
              #     Action: Abort
              #     Threshold: 3

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
            ("agents/archaeologist.yaml", archaeologist),
            ("agents/planner.yaml",       planner),
            ("agents/developer.yaml",     developer),
            ("agents/reviewer.yaml",      reviewer),
            ("agents/approved.yaml",      approved),
        ]);
    }
}
