using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    /// <summary>
    /// Generates the <c>debate</c> template: a decision-focused adversarial pipeline.
    /// A Proposer argues a position across up to 3 rounds against a Challenger; a Moderator
    /// then synthesises a structured final verdict. Use for architecture decisions, design
    /// reviews, technology evaluations, and approach choices.
    /// </summary>
    private static string Debate(string model, string? endpoint) => $"""
        Orchestration:
          Name: Debate Pipeline
          Description: >
            Decision-focused adversarial pipeline. The Proposer argues a position with evidence;
            the Challenger critiques it for up to 3 rounds. The Moderator synthesises a final
            verdict with recommendation, rationale, and dissenting points.

          Agents:
            - Name: Proposer
              Description: Makes the case for a specific decision or approach with evidence.
              Instructions: |
                You are a Proposer. Your role depends on the stage.

                STAGE 1 — DELIBERATION (rounds with the Challenger):
                Round 1: Write a structured position paper arguing for a specific decision or
                approach. Include:
                  - Clear recommendation (one sentence)
                  - Rationale with supporting evidence (data, precedents, constraints)
                  - Anticipated objections and pre-emptive responses
                Save the paper to {FuseraftPaths.LocalDebatePosition}.

                Subsequent rounds: Revise the position paper in response to the Challenger's
                critique. Address EACH objection explicitly — do not ignore any. Update
                {FuseraftPaths.LocalDebatePosition} with the revised paper.

                STAGE 2 — SYNTHESIS (with the Moderator):
                Write a debate summary to {FuseraftPaths.LocalDebateSummary} capturing:
                  - What was argued in Stage 1
                  - What objections were raised and how they were addressed
                  - What remains contested
                  - Your final position
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - Search
                - Scratchpad

            - Name: Challenger
              Description: Adversarially critiques the Proposer's position with counter-evidence.
              Instructions: |
                You are a Challenger. Your job is to stress-test the Proposer's position — not
                to find reasons it will succeed, but reasons it will FAIL.

                Read {FuseraftPaths.LocalDebatePosition} carefully.

                For each weakness you find, provide:
                  - The specific claim being challenged
                  - Counter-evidence or a counter-argument (not just a preference)
                  - What would need to be true for the weakness to be addressed

                Do not raise objections you cannot support with evidence or reasoning.
                Do not repeat objections the Proposer has already addressed adequately.

                If the position is genuinely sound and well-argued, respond with exactly:
                APPROVED
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - Scratchpad

            - Name: Moderator
              Description: Synthesises the full debate record into a structured final verdict.
              Instructions: |
                You are a Moderator. You have observed the full debate. Your job is to write
                an impartial, structured verdict.

                Read:
                  - {FuseraftPaths.LocalDebatePosition} (the Proposer's final position)
                  - {FuseraftPaths.LocalDebateSummary} (the Proposer's debate summary)

                Write a verdict to {FuseraftPaths.LocalDebateVerdict} with these fields:
                  recommendation  — what to do (one sentence)
                  rationale       — the strongest reasons for the recommendation (2–4 bullets)
                  dissenting_points — objections from the Challenger that were NOT fully resolved
                  confidence      — "high", "medium", or "low" with a one-sentence justification

                Be fair. If the Challenger raised valid unresolved objections, say so.
                Do not rubber-stamp the Proposer's position.

                After writing the verdict, respond with exactly:
                APPROVED
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - Scratchpad

          Selection:
            Type: adversarial
            Adversarial:
              Rounds: 3
              PassKeyword: "APPROVED"
              Stages:
                - Generator: Proposer
                  Critic: Challenger
                  Label: Deliberation

                - Generator: Proposer
                  Critic: Moderator
                  Label: Synthesis

          Termination:
            Type: maxiterations
            MaxIterations: 50

          Compaction:
            TriggerTurnCount: 40
            KeepRecentTurns: 10

          Events:
            Path: {FuseraftPaths.LocalEventsLog}
        """;
}
