using fuseraft.Core;

namespace fuseraft.Cli.Commands;

internal static partial class InitTemplates
{
    /// <summary>
    /// Generates the <c>magentic</c> template: an AI-managed team where a manager LLM dynamically
    /// selects participants each round, plans the work, and replans when progress stalls.
    /// Termination is controlled by <c>MaxRoundCount</c>, <c>MaxStallCount</c>, and
    /// <c>MaxResetCount</c>; the <c>Termination</c> section is ignored for this selection type.
    /// </summary>
    private static string Magentic(string model, string? endpoint) => $"""
        Orchestration:
          Name: Magentic Team
          Description: >
            AI-managed team orchestrated by Magentic. A manager LLM plans the work,
            dynamically selects participants each round, and replans if progress stalls.

          # Named model aliases — agents reference these by alias name so you only need to
          # change the model ID in one place.  The manager benefits from a reasoning-capable
          # model (e.g. o3, claude-opus-4-6, gemini-2.5-pro); both default to '{model}' here.
          Models:
            manager:
              ModelId: {model}{Ep(endpoint, "      ")}
            worker:
              ModelId: {model}{Ep(endpoint, "      ")}

          Agents:
            - Name: Researcher
              Description: Gathers information, searches, and produces sourced summaries.
              Instructions: |
                You are a Researcher. Find information, analyse it, and produce well-sourced
                summaries. Use your tools to search and read content. Be thorough but concise.
              Model:
                ModelId: worker
              Plugins:
                - FileSystem
                - Search
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

          Selection:
            Type: magentic
            Magentic:
              # The manager drives the planning and progress-evaluation loop.
              # A reasoning-capable model is strongly recommended for this role.
              Model:
                ModelId: manager
              MaxRoundCount: 20      # hard cap on coordination rounds
              MaxStallCount: 3       # consecutive stalled rounds before replanning
              MaxResetCount: 2       # max replan cycles before terminating
              EnablePlanReview: false  # set to true to approve the plan before execution begins

          # NOTE: The Termination section is IGNORED for Selection.Type 'magentic'.
          # Session end is controlled entirely by MaxRoundCount, MaxStallCount, and
          # MaxResetCount in the Magentic block above.  This section is present only
          # to satisfy the config schema and may be removed.
          Termination:
            Type: maxiterations
            MaxIterations: 50

          Compaction:
            TriggerTurnCount: 50
            KeepRecentTurns: 10

          Checkpoint:
            Mode: json
            Path: .fuseraft/checkpoints

          Events:
            Path: {FuseraftPaths.LocalEventsLog}
        """;
}
