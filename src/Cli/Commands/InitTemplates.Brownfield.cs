using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    /// <summary>
    /// Generates the <c>brownfield</c> template: Archaeologist → Planner → Developer → Reviewer
    /// state-machine pipeline for making targeted changes to an existing codebase.
    /// The Archaeologist writes a convention profile and discovery brief before any code changes;
    /// both artifacts are injected into every subsequent agent's context.
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
              If the brief is unclear, call handoff(route_keyword: "REPLAN REQUIRED").
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
            Description: Code-review-only inspection against the brief and conventions.
            Instructions: |
              You are a principal engineer reviewing a change to an existing codebase. Your job is to:
              1. Read each file listed in {FuseraftPaths.LocalBrief} under files_to_change.
              2. Verify every acceptance criterion is satisfied by code inspection.
              3. Check that the change follows conventions from {FuseraftPaths.LocalConventions}.
              4. Confirm no files outside files_to_change were modified (use changes_read_latest).
              Do NOT run shell commands — this is a code-inspection-only review.
              If the change is correct, call handoff(route_keyword: "APPROVED").
              If revision is needed, call handoff(route_keyword: "REVISION REQUIRED") and explain what to fix.
              If the plan needs rethinking, call handoff(route_keyword: "REPLAN REQUIRED").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Changes
              - Handoff
            FunctionChoice: auto
            ContextWindow:
              TextOnly: true
            {AgentFileOptions}
            """;

        var mainConfig = $"""
            Orchestration:
              Name: Brownfield Codebase Pipeline
              Description: >-
                Archaeologist recons the existing codebase and writes a discovery brief;
                Planner designs the targeted change; Developer implements with a scoped change
                envelope; Reviewer inspects by code review. Conventions detected during recon
                are automatically injected into every agent's system prompt.

              Security:
                FileSystemSandboxPath: .   # set to your project root (e.g. ~/projects/myapp)
                # ChangeEnvelope is seeded automatically from the discovery brief when
                # Brownfield.SeedEnvelopeFromBrief is true — no need to list files manually.

              Brownfield:
                EntryPoints:
                  - src/   # replace with your actual entry points (e.g. cmd/server/main.go)
                SeedEnvelopeFromBrief: true
                DiscoveryBriefPath: {FuseraftPaths.LocalBrownfieldBrief}
                ConventionProfilePath: {FuseraftPaths.LocalConventions}

              EvidenceStore:
                Path: {FuseraftPaths.LocalEvidence}

              ChangeTracking:
                Path: {FuseraftPaths.LocalChanges}

              Validation:
                BriefPath: {FuseraftPaths.LocalBrief}
                ChangeLogPath: {FuseraftPaths.LocalChanges}

              Contracts:
                - Name: ReconComplete
                  Requires:
                    - Type: FileExists
                      Path: {FuseraftPaths.LocalBrownfieldBrief}
                    - Type: FileExists
                      Path: {FuseraftPaths.LocalConventions}

                - Name: BriefExists
                  Requires:
                    - Type: FileExists
                      Path: {FuseraftPaths.LocalBrief}

                - Name: ImplementationComplete
                  Requires:
                    - Type: FilesWritten
                      Source: {FuseraftPaths.LocalBrief}
                      Field: files_to_change

              FailureHandling:
                MissingEvidence:
                  Action: Reinstruct
                  Threshold: 3
                NoProgress:
                  Action: Abort
                  Threshold: 3

              Events:
                Path: {FuseraftPaths.LocalEventsLog}

              # Each agent lives in its own YAML file in agents/ — edit, version, or reuse
              # them independently across configs. Inline fields override the file at load time.
              Agents:
                - AgentFile: agents/archaeologist.yaml
                - AgentFile: agents/planner.yaml
                - AgentFile: agents/developer.yaml
                - AgentFile: agents/reviewer.yaml

              Selection:
                Type: statemachine
                StateMachine:
                  Initial: Recon

                  States:
                    Recon:
                      Agent: Archaeologist
                      Transitions:
                        - To: Planning
                          Signal: "RECON COMPLETE"
                          Contract: ReconComplete

                    Planning:
                      Agent: Planner
                      Transitions:
                        - To: Implementation
                          Signal: "HANDOFF TO DEVELOPER"
                          Contract: BriefExists

                    Implementation:
                      Agent: Developer
                      Transitions:
                        - To: Review
                          Signal: "HANDOFF TO REVIEWER"
                          Contract: ImplementationComplete
                        - To: Planning
                          Signal: "REPLAN REQUIRED"

                    Review:
                      Agent: Reviewer
                      Transitions:
                        - To: Done
                          Signal: APPROVED
                        - To: Implementation
                          Signal: "REVISION REQUIRED"
                        - To: Planning
                          Signal: "REPLAN REQUIRED"

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

              Compaction:
                TriggerTurnCount: 30
                KeepRecentTurns: 8
                Mode: lossless
            """;

        return new GeneratedConfig(mainConfig, [
            ("agents/archaeologist.yaml", archaeologist),
            ("agents/planner.yaml",       planner),
            ("agents/developer.yaml",     developer),
            ("agents/reviewer.yaml",      reviewer),
        ]);
    }
}
