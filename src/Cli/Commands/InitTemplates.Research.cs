using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    private static GeneratedConfig Research(string model, string? endpoint) => GeneratedConfig.Inline($$"""
        team: Research Team
        description: Researcher gathers information with cited sources; Critic adversarially reviews findings before the Writer begins; Writer synthesises the final document.

        models:
          default:
            modelId: {{model}}{{Ep(endpoint, "    ")}}

        agents:
          Researcher:
            role: Gathers information and writes structured findings with inline citations.
            model: default
            tools: [Http, Search, FileSystem, ResearchFindings]
            instructions: |
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
              as unanswerable), call handoff(route_keyword: "Critic").

              You are read-only with respect to this project's own files — you have no
              write_file/patch_file access. write_file_research_findings is the only way to
              persist your findings.
            advanced:
              functionChoice: required
              context:
                - source: file:.fuseraft/docs/research-review.json
              capabilities:
                FileSystem: [read]
          Critic:
            role: "Adversarially reviews research findings for gaps, unsupported claims, and contradictions."
            model: default
            tools: [FileSystem, ResearchReview]
            instructions: |
              You are an adversarial research critic. Find reasons the findings will MISLEAD —
              not reasons they are correct.

              Read .fuseraft/docs/research-findings.md.

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

              If there are NO blocking issues, call handoff(route_keyword: "Writer").
              If there are blocking issues, call handoff(route_keyword: "Researcher").

              You are read-only with respect to this project's own files — you have no
              write_file/patch_file access. write_file_research_review is the only way to
              persist your review; revising the findings is the Researcher's job, not yours.
            advanced:
              functionChoice: required
              context:
                - source: session_context
                - source: file:.fuseraft/docs/research-findings.md
                  maxChars: 8000
                - source: own_history:2
              capabilities:
                FileSystem: [read]
          Writer:
            role: Synthesises approved research findings into a polished final document.
            model: default
            tools: [FileSystem]
            instructions: |
              You are a skilled technical writer. Your job is to:
              1. Read .fuseraft/docs/research-findings.md — the approved research.
              2. Read .fuseraft/docs/research-review.json — note any optional improvements
                 and incorporate the straightforward ones.
              3. Synthesise a clear, well-structured document that answers the original question.
                 - Lead with the answer, not the methodology.
                 - Use headers, bullet points, and tables where they aid comprehension.
                 - Cite sources inline for factual claims.
                 - Acknowledge uncertainty explicitly; do not present low-confidence findings
                   as established fact.
              4. Write the final document to .fuseraft/docs/report.md.
              When done, call handoff(route_keyword: "end").
            advanced:
              functionChoice: required

        flow:
          start: Researcher
          steps:
            Researcher:
              - to: Critic
                requires:
                  - fileExists: .fuseraft/docs/research-findings.md
            Critic:
              - to: Writer
                when: findings approved
                requires:
                  - fileExists: .fuseraft/docs/research-review.json
              - to: Researcher
                when: findings rejected
                maxRevisits: 2
            Writer:
              - to: end
                when: document complete

        limits:
          maxTurns: 30

        advanced:
          compaction:
            triggerTurnCount: 30
            keepRecentTurns: 8
            mode: lossless
        """);
}
