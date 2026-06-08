using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    /// <summary>
    /// Generates the <c>solo</c> template: a single general-purpose agent with lossless
    /// compaction. The right starting point for simple tasks, scripts, and one-shot jobs.
    /// </summary>
    private static string Solo(string model, string? endpoint) => $"""
        Orchestration:
          Name: Solo Agent
          Description: >-
            A single capable agent with lossless compaction.
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
                4. If a command or action fails, try a different approach — do not repeat
                   a failing action without changing something.
                5. When the task is fully done, end your response with: TASK_COMPLETE
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - Shell

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
