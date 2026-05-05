using fuseraft.Core;

namespace fuseraft.Cli.Commands;

internal static partial class InitTemplates
{
    // ─── Content ────────────────────────────────────────────────────────────────

    private static GeneratedConfig Content(string model, string? endpoint)
    {
        var writer = $"""
            Name: Writer
            Description: Produces a complete first draft and saves it to disk.
            Instructions: |
              You are a creative and precise writer. Your job is to:
              1. Understand the content brief from the task.
              2. Write a complete draft and save it to output/draft.md using write_file.
              When the draft is ready for review, call handoff(route_keyword: "DRAFT_COMPLETE").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Search
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var editor = $"""
            Name: Editor
            Description: Edits for clarity, accuracy, and style; writes the final version.
            Instructions: |
              You are a senior editor. Your job is to:
              1. Read the draft from output/draft.md.
              2. Edit for clarity, accuracy, tone, and structure.
              3. Save the final version to output/final.md using write_file.
              When editing is complete, call handoff(route_keyword: "CONTENT_APPROVED").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var mainConfig = $"""
            Orchestration:
              Name: Content Pipeline
              Description: >-
                Writer drafts content with a verified handoff; Editor refines and approves.

              EvidenceStore:
                Path: {FuseraftPaths.LocalEvidence}

              Contracts:
                - Name: DraftExists
                  Requires:
                    - Type: FileExists
                      Path: output/draft.md

              # Each agent lives in its own YAML file in agents/ — edit, version, or reuse
              # them independently across configs. Inline fields override the file at load time.
              Agents:
                - AgentFile: agents/writer.yaml
                - AgentFile: agents/editor.yaml

              Selection:
                Type: statemachine
                StateMachine:
                  Initial: Writing

                  States:
                    Writing:
                      Agent: Writer
                      Transitions:
                        - To: Editing
                          Signal: "DRAFT_COMPLETE"
                          Contract: DraftExists

                    Editing:
                      Agent: Editor
                      Transitions:
                        - To: Done
                          Signal: "CONTENT_APPROVED"

                    Done:
                      Agent: Editor
                      Terminal: true

              Termination:
                Type: composite
                Strategies:
                  - Type: regex
                    Pattern: CONTENT_APPROVED
                    AgentNames: [Editor]
                  - Type: maxiterations
                    MaxIterations: 10
            {OptionalSections(model, endpoint)}
            """;

        return new GeneratedConfig(mainConfig, [
            ("agents/writer.yaml", writer),
            ("agents/editor.yaml", editor),
        ]);
    }
}
