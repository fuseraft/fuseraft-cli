using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    private static GeneratedConfig Solo(string model, string? endpoint) => GeneratedConfig.Inline($$"""
        team: Solo Agent
        description: "A single capable agent with lossless compaction. The right starting point for simple tasks, scripts, and one-shot jobs."

        models:
          default:
            modelId: {{model}}{{Ep(endpoint, "    ")}}

        agents:
          Agent:
            role: Completes the given task using available tools.
            model: default
            tools: [FileSystem, Shell]
            instructions: |
              You are a capable, methodical assistant. Your job is to:
              1. Read the task and break it into concrete steps.
              2. For any file you need to examine: {{LargeFileProtocol}}
              3. Use available tools to complete each step in order.
              4. If a command or action fails, try a different approach — do not repeat
                 a failing action without changing something.
              5. When the task is fully done, hand off to end.

        flow:
          start: Agent
          steps:
            Agent:
              - to: end
                when: the work is complete

        limits:
          maxTurns: 20

        advanced:
          compaction:
            triggerTurnCount: 30
            keepRecentTurns: 8
            mode: lossless
        """);
}
