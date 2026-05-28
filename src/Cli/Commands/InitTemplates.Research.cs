using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    /// <summary>
    /// Generates the <c>research</c> template: Researcher → Writer state-machine pipeline
    /// for information gathering and document synthesis. A <c>ResearchComplete</c> contract
    /// gates the handoff, ensuring findings are persisted to disk before the Writer begins.
    /// </summary>
    private static GeneratedConfig Research(string model, string? endpoint)
    {
        var researcher = $"""
            Name: Researcher
            Description: Gathers information and writes structured findings to disk.
            Instructions: |
              You are a diligent researcher. Your job is to:
              1. Break the topic into focused questions.
              2. Search for answers using available tools.
              3. Write your structured findings to {FuseraftPaths.LocalDocs}/research-findings.md.
              When your research is thorough and complete, call handoff(route_keyword: "HANDOFF TO WRITER").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - Http
              - Search
              - FileSystem
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var writer = $"""
            Name: Writer
            Description: Turns research findings into a polished final document.
            Instructions: |
              You are a skilled technical writer. Your job is to:
              1. Read the research findings from {FuseraftPaths.LocalDocs}/research-findings.md.
              2. Synthesize a clear, well-structured document that answers the original question.
              3. Write the final document to {FuseraftPaths.LocalDocs}/report.md.
              When done, call handoff(route_keyword: "DOCUMENT COMPLETE").
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
              Name: Research Team
              Description: >-
                Researcher gathers information with a verified handoff; Writer synthesises the final document.

              EvidenceStore:
                Path: {FuseraftPaths.LocalEvidence}

              Contracts:
                - Name: ResearchComplete
                  Requires:
                    - Type: FileExists
                      Path: {FuseraftPaths.LocalDocs}/research-findings.md

              # Each agent lives in its own YAML file in agents/ — edit, version, or reuse
              # them independently across configs. Inline fields override the file at load time.
              Agents:
                - AgentFile: agents/researcher.yaml
                - AgentFile: agents/writer.yaml

              Selection:
                Type: statemachine
                StateMachine:
                  Initial: Research

                  States:
                    Research:
                      Agent: Researcher
                      Transitions:
                        - To: Writing
                          Signal: "HANDOFF TO WRITER"
                          Contract: ResearchComplete

                    Writing:
                      Agent: Writer
                      Transitions:
                        - To: Done
                          Signal: "DOCUMENT COMPLETE"

                    Done:
                      Agent: Writer
                      Terminal: true

              Termination:
                Type: composite
                Strategies:
                  - Type: regex
                    Pattern: DOCUMENT COMPLETE
                    AgentNames: [Writer]
                  - Type: maxiterations
                    MaxIterations: 20
            {OptionalSections(model, endpoint)}
            """;

        return new GeneratedConfig(mainConfig, [
            ("agents/researcher.yaml", researcher),
            ("agents/writer.yaml",     writer),
        ]);
    }
}
