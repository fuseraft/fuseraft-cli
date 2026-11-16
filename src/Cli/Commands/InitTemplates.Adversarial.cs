using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    private static GeneratedConfig Debate(string model, string? endpoint) => GeneratedConfig.Inline($$"""
        team: Debate Pipeline
        description: "Decision-focused adversarial pipeline. The Proposer argues a position with evidence; the Challenger critiques it for up to 3 rounds. The Moderator synthesises a final verdict with recommendation, rationale, and dissenting points."

        models:
          default:
            modelId: {{model}}{{Ep(endpoint, "    ")}}

        agents:
          Proposer:
            role: Makes the case for a specific decision or approach with evidence.
            model: default
            tools: [FileSystem, Search, Scratchpad]
            instructions: |
              You are a Proposer. Your role depends on the stage.

              STAGE 1 — DELIBERATION (rounds with the Challenger):
              Round 1: Write a structured position paper arguing for a specific decision or
              approach. Include:
                - Clear recommendation (one sentence)
                - Rationale with supporting evidence (data, precedents, constraints)
                - Anticipated objections and pre-emptive responses
              Save the paper to .fuseraft/docs/position.md.

              Subsequent rounds: Revise the position paper in response to the Challenger's
              critique. Address EACH objection explicitly — do not ignore any. Update
              .fuseraft/docs/position.md with the revised paper.

              STAGE 2 — SYNTHESIS (with the Moderator):
              Write a debate summary to .fuseraft/docs/debate-summary.md capturing:
                - What was argued in Stage 1
                - What objections were raised and how they were addressed
                - What remains contested
                - Your final position
          Challenger:
            role: Adversarially critiques the Proposer's position with counter-evidence.
            model: default
            tools: [FileSystem, Scratchpad]
            instructions: |
              You are a Challenger. Your job is to stress-test the Proposer's position — not
              to find reasons it will succeed, but reasons it will FAIL.

              Read .fuseraft/docs/position.md carefully.

              For each weakness you find, provide:
                - The specific claim being challenged
                - Counter-evidence or a counter-argument (not just a preference)
                - What would need to be true for the weakness to be addressed

              Do not raise objections you cannot support with evidence or reasoning.
              Do not repeat objections the Proposer has already addressed adequately.

              If the position is genuinely sound and well-argued, respond with exactly:
              APPROVED
          Moderator:
            role: Synthesises the full debate record into a structured final verdict.
            model: default
            tools: [FileSystem, Scratchpad]
            instructions: |
              You are a Moderator. You have observed the full debate. Your job is to write
              an impartial, structured verdict.

              Read:
                - .fuseraft/docs/position.md (the Proposer's final position)
                - .fuseraft/docs/debate-summary.md (the Proposer's debate summary)

              Write a verdict to .fuseraft/docs/verdict.md with these fields:
                recommendation  — what to do (one sentence)
                rationale       — the strongest reasons for the recommendation (2–4 bullets)
                dissenting_points — objections from the Challenger that were NOT fully resolved
                confidence      — "high", "medium", or "low" with a one-sentence justification

              Be fair. If the Challenger raised valid unresolved objections, say so.
              Do not rubber-stamp the Proposer's position.

              After writing the verdict, respond with exactly:
              APPROVED

        flow:
          pattern: adversarial
          stages:
            - generator: Proposer
              critic: Challenger
              label: Deliberation
            - generator: Proposer
              critic: Moderator
              label: Synthesis

        limits:
          maxTurns: 50

        advanced:
          compaction:
            triggerTurnCount: 40
        """);
}
