using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    private static GeneratedConfig Magentic(string model, string? endpoint) => GeneratedConfig.Inline($$"""
        team: Magentic Team
        description: "AI-managed team orchestrated by Magentic. A manager LLM plans the work, dynamically selects participants each round, and replans if progress stalls. The manager benefits from a reasoning-capable model; workers default to '{{model}}'."

        models:
          manager:
            modelId: {{model}}{{Ep(endpoint, "    ")}}
          worker:
            modelId: {{model}}{{Ep(endpoint, "    ")}}

        agents:
          Researcher:
            role: "Gathers information, searches the web and filesystem, and produces sourced summaries."
            model: worker
            tools: [FileSystem, Search, Http, Scratchpad]
            instructions: |
              You are a Researcher. Find information, analyse it, and produce well-sourced
              summaries. Use your tools to search and read content. Be thorough but concise.
              Cite your sources and flag uncertainty explicitly.
          Planner:
            role: "Designs the approach, writes structured briefs, and breaks work into tasks."
            model: worker
            tools: [FileSystem, Subagent, Scratchpad]
            instructions: |
              You are a Planner. Design a concrete, step-by-step approach for the work at hand.
              Identify what needs to be done, in what order, and by whom. Be specific — vague
              instructions waste cycles. Write plans and briefs to the filesystem.
          Developer:
            role: "Writes code, implements features, runs tests, and fixes bugs."
            model: worker
            tools: [FileSystem, Shell, Git, Scratchpad]
            instructions: |
              You are a Developer. Write clean, working code that solves the problem.
              Implement what is asked, verify with shell_run, and report results accurately.
              Prefer working code over theoretical explanations.
          Tester:
            role: Writes and runs tests; reports pass/fail with evidence.
            model: worker
            tools: [FileSystem, Shell, Scratchpad]
            instructions: |
              You are a Tester. Write tests that verify the feature works as intended.
              Run them with shell_run and report each result with the exact command and output.
              Never report a test as passing without evidence.
          Critic:
            role: "Reviews artifacts for quality, correctness, and completeness."
            model: worker
            tools: [FileSystem, Scratchpad]
            instructions: |
              You are a Critic. Review whatever artifact you are given — code, plan, brief,
              or research — for correctness, completeness, and quality. Be specific: name the
              file and line, quote the problematic passage, and explain why it is wrong.
              If the artifact is sound, say so explicitly with supporting evidence.

        flow:
          pattern: magentic
          model: manager
          maxRoundCount: 25
          enablePlanReview: true

        limits:
          maxTurns: 80

        advanced:
          compaction:
            triggerTurnCount: 40
            keepRecentTurns: 12
        """);
}
