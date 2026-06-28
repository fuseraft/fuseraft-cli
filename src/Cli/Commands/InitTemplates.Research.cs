using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    /// <summary>
    /// Generates the <c>research</c> template: Researcher → Critic → Writer state-machine pipeline.
    /// The Critic adversarially reviews research findings before the Writer begins — preventing
    /// hollow or unsupported research from reaching the final document. A <c>ResearchComplete</c>
    /// contract gates the Critic; a <c>ReviewComplete</c> contract gates the Writer.
    /// </summary>
    private static GeneratedConfig Research(string model, string? endpoint)
    {
        var researcher = $"""
            Name: Researcher
            Description: Gathers information and writes structured findings with inline citations.
            Instructions: |
              You are a diligent researcher. Your job is to:
              1. Break the topic into focused questions — list them before you start.
              2. For each question: search, read sources, and record findings with citations.
                 Use Http for web content and Search for filesystem content.
              3. Call write_file_research_findings(content: ..., format: "md") with structured
                 Markdown findings. One section per question, each with:
                   - finding: what you learned
                   - sources: URLs or file paths consulted
                   - confidence: "high" | "medium" | "low" with a brief justification
                   - open_questions: sub-questions raised but not yet answered
              4. Every claim must be backed by a cited source. Do not assert conclusions
                 you did not verify.
              When research is thorough and every original question is answered (or documented
              as unanswerable), call handoff(route_keyword: "HANDOFF TO CRITIC").

              You are read-only with respect to this project's own files — you have no
              write_file/patch_file access. write_file_research_findings is the only way to
              persist your findings.
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - Http
              - Search
              - FileSystem
              - ResearchFindings
              - Handoff
            Capabilities:
              FileSystem: [read]
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var critic = $"""
            Name: Critic
            Description: Adversarially reviews research findings for gaps, unsupported claims, and contradictions.
            Instructions: |
              You are an adversarial research critic. Find reasons the findings will MISLEAD —
              not reasons they are correct.

              Read {FuseraftPaths.LocalResearchFindings}.

              AUDIT for these specific failure modes:
              1. COVERAGE GAPS — questions raised in the findings but not answered; topics
                 central to the subject that are not covered.
              2. UNSUPPORTED CLAIMS — assertions without a cited source, or where the cited
                 source does not actually support the claim.
              3. CONTRADICTIONS — findings in different sections that are logically inconsistent.
              4. LOW-CONFIDENCE GAPS — items marked "confidence: low" that are load-bearing
                 for any conclusion; these must be resolved or the conclusion must be hedged.
              5. MISSING PERSPECTIVES — on contested topics, findings that present only one side.

              Call write_file_research_review(content: ..., format: "json"). content must be a
              JSON object with two fields:
                blocking_issues      — array of strings; each a mandatory gap the Researcher MUST
                                       fix before the Writer can start (unsupported claims, missing
                                       coverage of central topics, logical contradictions)
                optional_improvements — array of strings; suggestions that improve quality but
                                        will not block approval

              A blocking issue is one where the Writer would produce an inaccurate or misleading
              document if they relied on the current findings. Stylistic issues are not blocking.

              If there are NO blocking issues, call handoff(route_keyword: "FINDINGS APPROVED").
              If there are blocking issues, call handoff(route_keyword: "FINDINGS REJECTED").

              You are read-only with respect to this project's own files — you have no
              write_file/patch_file access. write_file_research_review is the only way to
              persist your review; revising the findings is the Researcher's job, not yours.
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - ResearchReview
              - Handoff
            Capabilities:
              FileSystem: [read]
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var writer = $"""
            Name: Writer
            Description: Synthesises approved research findings into a polished final document.
            Instructions: |
              You are a skilled technical writer. Your job is to:
              1. Read {FuseraftPaths.LocalResearchFindings} — the approved research.
              2. Read {FuseraftPaths.LocalResearchReview} — note any optional improvements
                 and incorporate the straightforward ones.
              3. Synthesise a clear, well-structured document that answers the original question.
                 - Lead with the answer, not the methodology.
                 - Use headers, bullet points, and tables where they aid comprehension.
                 - Cite sources inline for factual claims.
                 - Acknowledge uncertainty explicitly; do not present low-confidence findings
                   as established fact.
              4. Write the final document to {FuseraftPaths.LocalDocs}/report.md.
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
                Researcher gathers information with cited sources; Critic adversarially reviews
                findings before the Writer begins; Writer synthesises the final document.

              EvidenceStore:
                Path: {FuseraftPaths.LocalEvidence}

              Contracts:
                - Name: ResearchComplete
                  Requires:
                    - Type: FileExists
                      Path: {FuseraftPaths.LocalResearchFindings}

                - Name: ReviewComplete
                  Requires:
                    - Type: FileExists
                      Path: {FuseraftPaths.LocalResearchReview}

              # Each agent lives in its own YAML file in agents/ — edit, version, or reuse
              # them independently across configs.
              Agents:
                - AgentFile: agents/researcher.yaml
                - AgentFile: agents/critic.yaml
                - AgentFile: agents/writer.yaml

              Selection:
                Type: statemachine
                StateMachine:
                  Initial: Research

                  States:
                    Research:
                      Agent: Researcher
                      Transitions:
                        - To: CriticalReview
                          Signal: "HANDOFF TO CRITIC"
                          Contract: ResearchComplete

                    CriticalReview:
                      Agent: Critic
                      Transitions:
                        - To: Writing
                          Signal: "FINDINGS APPROVED"
                          Contract: ReviewComplete
                        - To: Research
                          Signal: "FINDINGS REJECTED"
                          MaxRevisits: 2
                          HandoffContext:
                            - Source: file:{FuseraftPaths.LocalResearchReview}

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
                    Pattern: "DOCUMENT COMPLETE"
                    AgentNames: [Writer]
                  - Type: maxiterations
                    MaxIterations: 30
            {OptionalSections(model, endpoint)}
            """;

        return new GeneratedConfig(mainConfig, [
            ("agents/researcher.yaml", researcher),
            ("agents/critic.yaml",     critic),
            ("agents/writer.yaml",     writer),
        ]);
    }
}
