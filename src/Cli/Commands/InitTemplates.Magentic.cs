using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    /// <summary>
    /// Generates the <c>magentic</c> template: an AI-managed team where a manager LLM dynamically
    /// selects participants each round, plans the work, and replans when progress stalls.
    /// Five specialised worker agents cover research, planning, development, testing, and critique.
    /// <c>EnablePlanReview: true</c> lets the user approve the manager's plan before execution begins.
    /// </summary>
    private static string Magentic(string model, string? endpoint) => $"""
        Orchestration:
          Name: Magentic Team
          Description: >
            AI-managed team orchestrated by Magentic. A manager LLM plans the work,
            dynamically selects participants each round, and replans if progress stalls.
            The manager benefits from a reasoning-capable model; workers default to '{model}'.

          # Named model aliases — agents reference these by alias so you only change IDs once.
          # Set 'manager' to a reasoning-capable model (claude-opus-4-8, o3, gemini-2.5-pro).
          Models:
            manager:
              ModelId: {model}{Ep(endpoint, "      ")}
            worker:
              ModelId: {model}{Ep(endpoint, "      ")}

          Agents:
            - Name: Researcher
              Description: Gathers information, searches the web and filesystem, and produces sourced summaries.
              Instructions: |
                You are a Researcher. Find information, analyse it, and produce well-sourced
                summaries. Use your tools to search and read content. Be thorough but concise.
                Cite your sources and flag uncertainty explicitly.
              Model:
                ModelId: worker
              Plugins:
                - FileSystem
                - Search
                - Http
                - Scratchpad

            - Name: Planner
              Description: Designs the approach, writes structured briefs, and breaks work into tasks.
              Instructions: |
                You are a Planner. Design a concrete, step-by-step approach for the work at hand.
                Identify what needs to be done, in what order, and by whom. Be specific — vague
                instructions waste cycles. Write plans and briefs to the filesystem.
              Model:
                ModelId: worker
              Plugins:
                - FileSystem
                - SubAgent
                - Scratchpad

            - Name: Developer
              Description: Writes code, implements features, runs tests, and fixes bugs.
              Instructions: |
                You are a Developer. Write clean, working code that solves the problem.
                Implement what is asked, verify with shell_run, and report results accurately.
                Prefer working code over theoretical explanations.
              Model:
                ModelId: worker
              Plugins:
                - FileSystem
                - Shell
                - Git
                - Scratchpad

            - Name: Tester
              Description: Writes and runs tests; reports pass/fail with evidence.
              Instructions: |
                You are a Tester. Write tests that verify the feature works as intended.
                Run them with shell_run and report each result with the exact command and output.
                Never report a test as passing without evidence.
              Model:
                ModelId: worker
              Plugins:
                - FileSystem
                - Shell
                - Scratchpad

            - Name: Critic
              Description: Reviews artifacts for quality, correctness, and completeness.
              Instructions: |
                You are a Critic. Review whatever artifact you are given — code, plan, brief,
                or research — for correctness, completeness, and quality. Be specific: name the
                file and line, quote the problematic passage, and explain why it is wrong.
                If the artifact is sound, say so explicitly with supporting evidence.
              Model:
                ModelId: worker
              Plugins:
                - FileSystem
                - Scratchpad

          Selection:
            Type: magentic
            Magentic:
              Model:
                ModelId: manager
              MaxRoundCount: 25      # hard cap on coordination rounds
              MaxStallCount: 3       # consecutive stalled rounds before replanning
              MaxResetCount: 2       # max replan cycles before terminating
              EnablePlanReview: true # user approves the manager's plan before execution begins

          # NOTE: Termination is controlled entirely by MaxRoundCount, MaxStallCount, and
          # MaxResetCount above. This section exists only to satisfy the config schema.
          Termination:
            Type: maxiterations
            MaxIterations: 80

          Compaction:
            TriggerTurnCount: 40
            KeepRecentTurns: 12

          # ContextBudget: per-agent cumulative input-token thresholds.
          # ContextBudget:
          #   WarnAt: 80000
          #   CutoverAt: 120000

          Checkpoint:
            Mode: json
            Path: {FuseraftPaths.LocalCheckpoints}

          Events:
            Path: {FuseraftPaths.LocalEventsLog}
        """;
}
