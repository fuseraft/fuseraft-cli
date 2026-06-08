using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    /// <summary>
    /// Generates the <c>solo</c> template: a single general-purpose agent with execution state
    /// and investigation tooling. Unlike the retired <c>minimal</c> template, the agent can
    /// record failed attempts with <c>create_hypothesis</c> / <c>reject_hypothesis</c> and will
    /// never enter a blind retry loop.
    /// </summary>
    private static string Solo(string model, string? endpoint) => $"""
        Orchestration:
          Name: Solo Agent
          Description: >-
            A single capable agent with investigation tooling and lossless compaction.
            The right starting point for simple tasks, scripts, and one-shot jobs.

          Agents:
            - Name: Agent
              Description: Completes the given task using available tools.
              Instructions: |
                You are a capable, methodical assistant. Your job is to:
                1. Read the task and break it into concrete steps.
                2. For any file you need to examine: call get_file_summary first (shows the
                   first 30 lines and total size), grep_file to locate the relevant section,
                   then read_file with startLine/maxLines — never cold-read a large file.
                3. Use available tools to complete each step in order.
                4. If a command or action fails, record the failure before retrying:
                   - Call create_hypothesis(description) naming the specific approach.
                   - If it fails: call reject_hypothesis(id, reason, evidence) with the exact
                     error. Read the source of the failure before trying something new.
                   - If it succeeds: call confirm_hypothesis(id, evidence).
                   Do not retry a rejected approach — try a different one.
                5. When the task is fully done, end your response with: TASK_COMPLETE
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - Shell
                - Investigation

          Selection:
            Type: sequential

          Termination:
            Type: regex
            Pattern: TASK_COMPLETE
            MaxIterations: 20

          Compaction:
            TriggerTurnCount: 30
            KeepRecentTurns: 8
            Mode: lossless
        {OptionalSections(model, endpoint)}
        """;
}
